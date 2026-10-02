using DownloadManager.Models;

namespace DownloadManager.Services.AnimeWorld;

public interface IAnimeWorldService
{
    Task InitializeAsync(bool headless = true);
    Task<List<AnimeSearchResult>> SearchAsync(string query, CancellationToken ct = default);
    Task<List<Episode>> GetEpisodesAsync(string animeUrl, CancellationToken ct = default);
    Task<string?> GetVideoUrlAsync(string episodePageUrl, CancellationToken ct = default);

    /// <summary>
    /// Recupera in blocco gli URL video per una lista di episodi.
    /// Ritorna una mappa episode.Link → videoUrl.
    /// </summary>
    Task<Dictionary<string, string>> GetVideoUrlsBatchAsync(
        List<Episode> episodes, CancellationToken ct = default);

    Task DisposeAsync();
    Task NotifyProxyChangedAsync();
}