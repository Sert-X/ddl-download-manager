using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using DownloadManager.Models;
using DownloadManager.Persistence;

namespace DownloadManager.Services.Videobox;

public class VideoboxApiService
{
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;

    public VideoboxApiService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _httpClient = new HttpClient();
        // Timeout a 2 minuti per evitare errori su liste enormi
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
    }

    private void ConfigureAuth()
    {
        var settings = _settingsService.Current;
        var authBytes = Encoding.ASCII.GetBytes($"{settings.IpsApiKey}:");
        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
    }

    public async Task<VideoboxDto> GetVideoByIdAsync(int videoId)
    {
        ConfigureAuth();
        var settings = _settingsService.Current;
        var url = $"{settings.IpsBaseUrl.TrimEnd('/')}/api/videobox/videos/{videoId}";

        // Sempre la versione aggiornata (subito dopo "Applica" una copia in cache mostrerebbe i link vecchi)
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
        var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        
        return await response.Content.ReadFromJsonAsync<VideoboxDto>() ?? new VideoboxDto();
    }

    /// <summary>
    /// Il post come lo restituisce l'API (JSON), sempre aggiornato: serve alla rinomina per confrontare
    /// prima e dopo l'invio. Un post eliminato dà HttpRequestException con StatusCode 404.
    /// </summary>
    public async Task<string> GetVideoJsonAsync(int videoId, CancellationToken ct = default)
    {
        ConfigureAuth();
        var url = $"{_settingsService.Current.IpsBaseUrl.TrimEnd('/')}/api/videobox/videos/{videoId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    public Task<VideoboxPagedDto> GetVideosByCategoryAsync(int? categoryId, int page = 1, CancellationToken ct = default)
        => GetVideosByCategoriesAsync(categoryId is > 0 ? new[] { categoryId.Value } : null, page, ct);

    /// <summary>Post di una o più categorie insieme (l'API IPS accetta gli Id separati da virgola). Vuoto/null = tutti i media.</summary>
    public async Task<VideoboxPagedDto> GetVideosByCategoriesAsync(IReadOnlyCollection<int>? categoryIds, int page = 1, CancellationToken ct = default)
    {
        ConfigureAuth();
        var settings = _settingsService.Current;

        // Nessuna categoria: si omette il parametro per caricare tutti i media
        var ids = categoryIds?.Where(id => id > 0).Distinct().ToList();
        string catFilter = ids is { Count: > 0 } ? $"categories={string.Join(",", ids)}&" : "";
        // sortDir=desc: dal post più nuovo (di default il forum li dà dal più vecchio)
        var url = $"{settings.IpsBaseUrl.TrimEnd('/')}/api/videobox/videos?{catFilter}page={page}&perPage=50&sortDir=desc";
        
        var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        
        return await response.Content.ReadFromJsonAsync<VideoboxPagedDto>(ct) ?? new VideoboxPagedDto();
    }

    // Metodo GetCategoriesAsync ripristinato (era stato eliminato per sbaglio)
    public async Task<List<VideoboxCategoryDto>> GetCategoriesAsync()
    {
        ConfigureAuth();
        var settings = _settingsService.Current;
        var url = $"{settings.IpsBaseUrl.TrimEnd('/')}/api/videobox/categories?perPage=100";
        
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();
        
        var pagedData = await response.Content.ReadFromJsonAsync<VideoboxCategoryPagedDto>();
        return pagedData?.results ?? new List<VideoboxCategoryDto>();
    }

    /// <summary>
    /// Miniatura da caricare con l'invio. L'API accetta solo questa immagine e solo come file
    /// (campo thumbnail[nome-file] = contenuto del file); immagine grande e sfondo li gestisce il forum.
    /// </summary>
    public sealed record ThumbnailUpload(string FileName, byte[] Content);

    /// <summary>
    /// Il caricamento della miniatura è stato provato sul forum vero (post 14725, 31/10/2026): il forum
    /// salva il file e lo usa sia come miniatura sia come immagine del post. Con false la spunta
    /// "aggiorna la miniatura" resta disattivata.
    /// </summary>
    public static bool ThumbnailUploadConfirmed { get; set; } = true;

    public async Task UpdateVideoUrlsAsync(int videoId, string serverId, string title, int categoryId, int authorId, List<string> newUrls,
        ThumbnailUpload? thumbnail = null)
    {
        ConfigureAuth();
        var settings = _settingsService.Current;
        var url = $"{settings.IpsBaseUrl.TrimEnd('/')}/api/videobox/videos/{videoId}";

        // Sintassi FormUrlEncoded (PHP-style) che IPS accetta senza problemi
        var formData = new List<KeyValuePair<string, string>>
        {
            new("title", title),
            new("category", categoryId.ToString()),
            new("author", authorId.ToString())
        };

        // videos[ID_SERVER][] = URL
        foreach (var link in newUrls)
        {
            formData.Add(new KeyValuePair<string, string>($"videos[{serverId}][]", link));
        }

        HttpContent content;
        if (thumbnail == null) content = new FormUrlEncodedContent(formData);
        else
        {
            // Con un file serve multipart. I campi sono senza "filename": così PHP li mette tutti tra i
            // normali valori del modulo, come vuole l'API (thumbnail[nome] = contenuto del file).
            var multi = new MultipartFormDataContent();
            foreach (var kv in formData) multi.Add(new StringContent(kv.Value, Encoding.UTF8), Quoted(kv.Key));
            multi.Add(new ByteArrayContent(thumbnail.Content), Quoted($"thumbnail[{thumbnail.FileName}]"));
            content = multi;
        }

        using var _ = content;
        var response = await _httpClient.PostAsync(url, content);
        
        var responseString = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Errore API IPS ({(int)response.StatusCode} {response.StatusCode}): {DescribeError(responseString)}");

        // Con 200 il forum restituisce il post aggiornato. Un errore vero ha la forma
        // {"errorCode": "...", "errorMessage": "..."}: non basta che la parola "error" compaia
        // da qualche parte (es. il titolo "Zankyou no Terror").
        var ipsError = ReadIpsError(responseString);
        if (ipsError != null)
            throw new Exception($"Il forum ha risposto con un errore: {ipsError}");
    }

    private static string Quoted(string name) => "\"" + name.Replace("\"", "") + "\"";

    /// <summary>Messaggio di un errore IPS ({errorCode, errorMessage}), null se la risposta non è un errore.</summary>
    internal static string? ReadIpsError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            bool hasCode = root.TryGetProperty("errorCode", out var code);
            bool hasMsg = root.TryGetProperty("errorMessage", out var msg);
            if (!hasCode && !hasMsg) return null;
            var text = hasMsg ? msg.ToString() : "";
            return hasCode ? $"{text} (codice {code})".Trim() : text;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Testo breve di una risposta di errore: mai la risposta intera (può contenere i dati
    /// dell'autore del post, come email e indirizzo IP).
    /// </summary>
    private static string DescribeError(string? body)
    {
        var ips = ReadIpsError(body);
        if (ips != null) return ips;
        var text = System.Text.RegularExpressions.Regex.Replace(body ?? "", @"<[^>]+>|\s+", " ").Trim();
        return text.Length == 0 ? "risposta vuota" : text.Length > 200 ? text[..200] + "…" : text;
    }
}