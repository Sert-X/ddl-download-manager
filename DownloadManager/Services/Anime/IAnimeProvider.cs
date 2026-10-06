using DownloadManager.Models;

namespace DownloadManager.Services.Anime;

/// <summary>
/// Interfaccia comune a tutti i provider anime (AnimeWorld oggi,
/// altri in futuro). I consumer che non hanno bisogno di sapere
/// QUALE provider stanno usando dovrebbero dipendere da questa.
/// </summary>
public interface IAnimeProvider
{
    /// <summary>Nome breve del provider (es. "AnimeWorld").</summary>
    string Name { get; }

    /// <summary>URL base del sito (es. "https://www.animeworld.ac").</summary>
    string BaseUrl { get; }

    Task InitializeAsync(bool headless = true);
    Task<List<AnimeSearchResult>> SearchAsync(string query, CancellationToken ct = default);
    Task<List<Episode>> GetEpisodesAsync(string animeUrl, CancellationToken ct = default);
    Task<string?> GetVideoUrlAsync(string episodePageUrl, CancellationToken ct = default);

    Task<Dictionary<string, string>> GetVideoUrlsBatchAsync(
        List<Episode> episodes, CancellationToken ct = default);

    Task DisposeAsync();
    Task NotifyProxyChangedAsync();
}