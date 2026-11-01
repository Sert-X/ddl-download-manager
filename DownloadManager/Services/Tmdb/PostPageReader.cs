using System.Net;
using System.Text.RegularExpressions;

namespace DownloadManager.Services.Tmdb;

/// <summary>Codice TMDB e stagione trovati nella pagina pubblica di un post del forum.</summary>
public sealed record PostPageInfo(string? Kind, int? TmdbId, int? Season, int? PosterSeason, string Source)
{
    public bool HasId => TmdbId is > 0 && Kind != null;
    public static PostPageInfo None(string why) => new(null, null, null, null, why);
}

/// <summary>
/// Legge la pagina pubblica di un post (l'API di Videobox non restituisce la descrizione, dove il
/// forum mette il collegamento a TMDB): link "themoviedb.org/tv|movie/ID", blocco informazioni con
/// data-serieid / data-season, nome della locandina "poster-ID-sN". Una pagina alla volta, a ritmo
/// calmo per non pesare sul forum, con nuovi tentativi se la connessione cade.
/// </summary>
public class PostPageReader
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _last = DateTime.MinValue;

    /// <summary>Pausa minima tra due pagine del forum.</summary>
    public static TimeSpan Spacing { get; set; } = TimeSpan.FromMilliseconds(400);

    private static readonly Regex LinkRx = new(@"themoviedb\.org/(tv|movie)/(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SerieIdRx = new(@"data-serieid=""(\d+)""", RegexOptions.Compiled);
    private static readonly Regex SeasonRx = new(@"data-season=""(\d+)""", RegexOptions.Compiled);
    private static readonly Regex PosterRx = new(@"poster-(\d+)-s(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) DDLDownloadManager");
        return c;
    }

    /// <summary>Analizza l'HTML di una pagina (separato dal download, per i test).</summary>
    public static PostPageInfo Parse(string html)
    {
        int? posterSeason = null;
        var pm = PosterRx.Match(html);
        if (pm.Success) posterSeason = int.Parse(pm.Groups[2].Value);

        int? season = null;
        var sm = SeasonRx.Match(html);
        if (sm.Success && int.TryParse(sm.Groups[1].Value, out var sv) && sv > 0) season = sv;

        // Il link a TMDB dice anche se è una serie o un film
        var counts = LinkRx.Matches(html).Select(m => (Kind: m.Groups[1].Value.ToLowerInvariant(), Id: int.Parse(m.Groups[2].Value)))
            .GroupBy(x => x).OrderByDescending(g => g.Count()).ToList();
        if (counts.Count > 0)
        {
            var (kind, id) = counts[0].Key;
            var src = counts.Select(g => g.Key).Distinct().Count() > 1 ? "più link TMDB diversi nella pagina: usato il più frequente" : "link TMDB nella pagina";
            return new PostPageInfo(kind, id, season, posterSeason, src);
        }
        var serie = SerieIdRx.Match(html);
        if (serie.Success) return new PostPageInfo("tv", int.Parse(serie.Groups[1].Value), season, posterSeason, "codice serie nella pagina");
        return new PostPageInfo(null, null, season, posterSeason, "nessun codice TMDB nella pagina");
    }

    /// <summary>Scarica e analizza la pagina del post. Eccezione se la pagina non si raggiunge dopo i tentativi.</summary>
    public async Task<PostPageInfo> ReadAsync(string url, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            await Gate.WaitAsync(ct);
            try
            {
                var wait = _last + Spacing - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
                _last = DateTime.UtcNow;
                using var resp = await Http.GetAsync(url, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) return PostPageInfo.None("pagina del post non trovata (eliminato?)");
                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                    throw new HttpRequestException($"il forum ha risposto {(int)resp.StatusCode}");
                resp.EnsureSuccessStatusCode();
                return Parse(await resp.Content.ReadAsStringAsync(ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested && attempt < 4)
            {
                // attesa crescente poi nuovo tentativo
            }
            finally { Gate.Release(); }
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
        }
    }
}
