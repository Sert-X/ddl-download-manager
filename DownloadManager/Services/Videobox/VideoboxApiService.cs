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

    // Ripristinato int? (nullable) per far funzionare la selezione "Tutti i Media" (che passa null/0)
    public async Task<VideoboxPagedDto> GetVideosByCategoryAsync(int? categoryId, int page = 1, CancellationToken ct = default)
    {
        ConfigureAuth();
        var settings = _settingsService.Current;
        
        // Se categoryId è 0 o null, omettiamo il parametro per caricare tutti i media
        string catFilter = (categoryId.HasValue && categoryId.Value > 0) ? $"categories={categoryId.Value}&" : "";
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

    public async Task UpdateVideoUrlsAsync(int videoId, string serverId, string title, int categoryId, int authorId, List<string> newUrls)
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

        var content = new FormUrlEncodedContent(formData);

        var response = await _httpClient.PostAsync(url, content);
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new Exception($"Errore API IPS ({response.StatusCode}): {errorBody}");
        }

        var responseString = await response.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(responseString) && responseString.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception($"Risposta IPS 200 OK ma con errore: {responseString}");
        }
    }
}