using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DownloadManager.Services.Tmdb;

/// <summary>
/// Accesso a TMDB (api.themoviedb.org). Accetta sia la "Chiave API" (v3, 32 caratteri) sia il
/// "Token di lettura" (v4, inizia con "eyJ"): sono le due chiavi che TMDB mostra nell'account.
/// Titoli in italiano, se mancano in inglese, poi l'originale. Le risposte restano in memoria
/// per un giorno; se TMDB chiede di rallentare si aspetta e si riprova.
/// </summary>
public class TmdbClient
{
    private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.themoviedb.org/3/"), Timeout = TimeSpan.FromSeconds(20) };
    private static readonly TimeSpan CacheTime = TimeSpan.FromHours(24);

    private readonly Func<string> _key;
    private readonly ConcurrentDictionary<string, (DateTime At, object Value)> _cache = new();
    private readonly SemaphoreSlim _gate = new(4, 4);   // massimo 4 richieste insieme

    public TmdbClient(Func<string> key) => _key = key;

    public bool HasKey => !string.IsNullOrWhiteSpace(_key());

    /// <summary>Token v4 (Bearer) o chiave v3 (parametro api_key).</summary>
    public static bool IsReadToken(string key) => key.Trim().StartsWith("eyJ", StringComparison.Ordinal);

    /// <summary>Richiesta GET firmata con la chiave (la chiave non viene mai scritta nei log).</summary>
    public static HttpRequestMessage BuildRequest(string key, string relativeUrl)
    {
        key = key.Trim();
        var url = relativeUrl;
        if (!IsReadToken(key))
            url += (url.Contains('?') ? "&" : "?") + "api_key=" + Uri.EscapeDataString(key);
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (IsReadToken(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    /// <summary>Verifica la chiave: null se funziona, altrimenti il motivo (in italiano).</summary>
    public static async Task<string?> ValidateKeyAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return "Inserisci la chiave.";
        try
        {
            using var req = BuildRequest(key, "configuration");
            using var resp = await Http.SendAsync(req, ct);
            return resp.StatusCode switch
            {
                HttpStatusCode.OK => null,
                HttpStatusCode.Unauthorized => "TMDB rifiuta la chiave: controlla di averla copiata tutta (Chiave API o Token di lettura).",
                HttpStatusCode.TooManyRequests => "TMDB chiede di rallentare: riprova tra qualche secondo.",
                _ => $"TMDB ha risposto {(int)resp.StatusCode} {resp.ReasonPhrase}."
            };
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return "TMDB non risponde (tempo scaduto): controlla la connessione."; }
        catch (HttpRequestException ex) { return "Impossibile raggiungere TMDB: " + ex.Message; }
    }

    // ============================================================
    //  RICHIESTE
    // ============================================================

    /// <summary>GET con nuovi tentativi (rete, 429 "rallenta", 5xx). null se la risorsa non esiste (404).</summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        var key = _key();
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Manca la chiave TMDB (tab Account).");
        for (int attempt = 1; ; attempt++)
        {
            await _gate.WaitAsync(ct);
            HttpResponseMessage? resp = null;
            try
            {
                using var req = BuildRequest(key, url);
                resp = await Http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("TMDB rifiuta la chiave (tab Account).");
                if (resp.IsSuccessStatusCode)
                    return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                if (attempt >= 4) throw new HttpRequestException($"TMDB ha risposto {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }
            catch (HttpRequestException) when (attempt < 4) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < 4) { }
            finally { _gate.Release(); }

            // Attesa: quella chiesta da TMDB (Retry-After) o crescente
            var wait = resp?.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt * 2);
            resp?.Dispose();
            await Task.Delay(wait, ct);
        }
    }

    private async Task<T?> CachedAsync<T>(string cacheKey, Func<Task<T?>> load) where T : class
    {
        if (_cache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < CacheTime) return (T)hit.Value;
        var value = await load();
        if (value != null) _cache[cacheKey] = (DateTime.UtcNow, value);
        return value;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static int? YearOf(string date) =>
        date.Length >= 4 && int.TryParse(date[..4], out var y) && y > 1800 ? y : null;

    /// <summary>Titolo tradotto: italiano, se manca inglese (campo "name" per le serie, "title" per i film).</summary>
    private static string Translated(JsonElement root, string field, string fallback)
    {
        if (root.TryGetProperty("translations", out var tr) && tr.TryGetProperty("translations", out var list))
        {
            string? Find(string lang) => list.EnumerateArray()
                .Where(t => Str(t, "iso_639_1") == lang && t.TryGetProperty("data", out _))
                .Select(t => Str(t.GetProperty("data"), field).Trim())
                .FirstOrDefault(n => n.Length > 0);
            var it = Find("it");
            if (it != null) return it;
            var en = Find("en");
            if (en != null) return en;
        }
        return fallback;
    }

    // ============================================================
    //  SERIE, FILM, RICERCA
    // ============================================================

    public Task<TmdbTv?> GetTvAsync(int id, CancellationToken ct = default) => CachedAsync($"tv:{id}", async () =>
    {
        using var doc = await GetJsonAsync($"tv/{id}?language=it-IT&append_to_response=translations", ct);
        if (doc == null) return null;
        var r = doc.RootElement;
        var seasons = new List<TmdbSeason>();
        if (r.TryGetProperty("seasons", out var ss))
            foreach (var s in ss.EnumerateArray())
                seasons.Add(new TmdbSeason(Int(s, "season_number") ?? 0, Str(s, "name"), Int(s, "episode_count") ?? 0, YearOf(Str(s, "air_date")),
                    Str(s, "poster_path") is { Length: > 0 } sp ? sp : null));
        int? lastS = null, lastE = null, nextS = null;
        if (r.TryGetProperty("last_episode_to_air", out var le) && le.ValueKind == JsonValueKind.Object)
        { lastS = Int(le, "season_number"); lastE = Int(le, "episode_number"); }
        if (r.TryGetProperty("next_episode_to_air", out var ne) && ne.ValueKind == JsonValueKind.Object)
            nextS = Int(ne, "season_number");
        var original = Str(r, "original_name");
        return new TmdbTv(id, Translated(r, "name", Str(r, "name") is { Length: > 0 } n ? n : original), original,
            YearOf(Str(r, "first_air_date")), Str(r, "status"),
            r.TryGetProperty("in_production", out var ip) && ip.ValueKind == JsonValueKind.True,
            seasons, lastS, lastE, nextS, Str(r, "poster_path"), Str(r, "backdrop_path") is { Length: > 0 } bd ? bd : null);
    });

    public Task<TmdbMovie?> GetMovieAsync(int id, CancellationToken ct = default) => CachedAsync($"movie:{id}", async () =>
    {
        using var doc = await GetJsonAsync($"movie/{id}?language=it-IT&append_to_response=translations", ct);
        if (doc == null) return null;
        var r = doc.RootElement;
        var original = Str(r, "original_title");
        return new TmdbMovie(id, Translated(r, "title", Str(r, "title") is { Length: > 0 } t ? t : original), original,
            YearOf(Str(r, "release_date")), Str(r, "poster_path"), Str(r, "backdrop_path") is { Length: > 0 } bd ? bd : null);
    });

    /// <summary>Ricerca per titolo (serie e film), con l'anno se noto. I più pertinenti prima.</summary>
    public async Task<List<TmdbSearchHit>> SearchAsync(string query, int? year, CancellationToken ct = default)
    {
        var hits = await CachedAsync($"search:{query}|{year}", async () =>
        {
            using var doc = await GetJsonAsync($"search/multi?language=it-IT&include_adult=false&query={Uri.EscapeDataString(query)}", ct);
            var list = new List<TmdbSearchHit>();
            if (doc?.RootElement.TryGetProperty("results", out var res) == true)
                foreach (var h in res.EnumerateArray())
                {
                    var kind = Str(h, "media_type");
                    if (kind is not ("tv" or "movie")) continue;
                    var name = kind == "tv" ? Str(h, "name") : Str(h, "title");
                    var date = kind == "tv" ? Str(h, "first_air_date") : Str(h, "release_date");
                    double pop = h.TryGetProperty("popularity", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
                    list.Add(new TmdbSearchHit(kind, Int(h, "id") ?? 0, name, YearOf(date), pop));
                }
            return list;
        });
        return hits ?? new List<TmdbSearchHit>();
    }
}
