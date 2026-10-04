using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DownloadManager.Services.Update;

/// <summary>
/// Recupera il changelog (body) di una release GitHub tramite API pubblica.
/// Non richiede autenticazione per repo pubblici.
/// </summary>
public static class GitHubReleaseFetcher
{
    private static readonly HttpClient _http = new()
    {
        Timeout = System.TimeSpan.FromSeconds(10)
    };

    public static async Task<string> FetchChangelogAsync(string owner, string repo, string tag)
    {
        try
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("DDLDownloadManager");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                return "";

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("body", out var bodyEl))
                return bodyEl.GetString() ?? "";

            return "";
        }
        catch
        {
            return "";
        }
    }
}