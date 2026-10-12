using DownloadManager.Models;

namespace DownloadManager.Services.Streaming;

/// <summary>
/// Interfaccia comune per provider di film/serie TV (non anime).
/// </summary>
public interface IStreamingProvider
{
    /// <summary>Nome visualizzato del provider (es. "StreamingCommunity").</summary>
    string Name { get; }

    /// <summary>URL base del provider (per la tab Domini).</summary>
    string BaseUrl { get; }

    /// <summary>True se è un provider di film/serie (non anime).</summary>
    bool IsStreamingProvider { get; }

    /// <summary>Inizializza il browser Playwright (o altra risorsa).</summary>
    Task InitializeAsync(bool headless = true);

    /// <summary>Cerca film e serie TV per nome.</summary>
    Task<List<MovieSearchResult>> SearchAsync(string query);

    /// <summary>Recupera le stagioni di una serie TV.</summary>
    Task<List<Season>> GetSeasonsAsync(string seriesLink);

    /// <summary>Recupera gli episodi di una stagione.</summary>
    Task<List<StreamingEpisode>> GetEpisodesAsync(string seriesLink, int seasonNumber);

    /// <summary>Recupera l'URL .m3u8 (o MP4 diretto) per un film o episodio.</summary>
    Task<string?> GetVideoUrlAsync(
    string contentLink, bool isSeries = false,
    int seasonNumber = 1, int episodeNumber = 1,
    int? episodeId = null);

    /// <summary>Chiude il browser e libera le risorse.</summary>
    ValueTask DisposeAsync();
}