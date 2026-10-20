using DownloadManager.Models;

namespace DownloadManager.Services.Streaming.StreamingCommunity;

public interface IStreamingCommunityService : IStreamingProvider, IAsyncDisposable
{
    Task<string?> GetMovieStreamUrlAsync(string movieUrl);
    Task<string?> GetEpisodeStreamUrlAsync(string seriesUrl, int season, int episode);
    Task<string?> ExtractM3u8Async(string pageUrl);
    Task NotifyAuthChangedAsync();
    Task NotifyProxyChangedAsync();
    Task<(bool LoggedIn, bool Premium, string Info)> CheckSessionAsync();

    Task<List<VideoQuality>> GetAvailableQualitiesAsync(
        string contentLink, bool isSeries, int seasonNumber, int episodeNumber);

    Task<(string? VideoUrl, string? AudioUrl)> ResolveQualityAsync(
        string masterUrl, string qualityName, CancellationToken ct = default);

    Task<string?> GetSessionCookiesNetscapeAsync();

    /// <summary>Varianti video, tracce audio e sottotitoli del contenuto (per l'analisi prima del download).</summary>
    Task<MasterTracks> GetAvailableTracksAsync(
        string contentLink, bool isSeries, int seasonNumber, int episodeNumber);

    /// <summary>Legge una playlist master già nota (al momento del download).</summary>
    Task<MasterTracks> GetMasterTracksAsync(string masterUrl, CancellationToken ct = default);

    /// <summary>Durata in secondi di una media playlist (somma degli #EXTINF), null se non leggibile.</summary>
    Task<double?> GetPlaylistDurationAsync(string mediaPlaylistUrl, CancellationToken ct = default);

    /// <summary>Scarica una traccia sottotitoli HLS e la restituisce come testo WebVTT unico.</summary>
    Task<string?> DownloadSubtitleAsync(string playlistUrl, CancellationToken ct = default);
}