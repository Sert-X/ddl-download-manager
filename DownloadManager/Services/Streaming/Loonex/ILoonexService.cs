using DownloadManager.Models;

namespace DownloadManager.Services.Streaming.Loonex;

public interface ILoonexService : IStreamingProvider, IAsyncDisposable
{
    Task<List<MovieSearchResult>> SearchAsync(string query, CancellationToken ct = default);
    Task<List<Season>> GetSeasonsAsync(string schedaUrl, CancellationToken ct = default);
    Task<List<StreamingEpisode>> GetEpisodesAsync(string schedaUrl, int seasonIndex, CancellationToken ct = default);
    Task<string?> GetVideoUrlAsync(string videoId, CancellationToken ct = default);
    Task<string?> GetSessionCookiesNetscapeAsync(CancellationToken ct = default);
}
