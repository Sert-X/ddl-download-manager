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
}