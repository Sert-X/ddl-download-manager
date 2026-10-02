using DownloadManager.Models;

namespace DownloadManager.Services.Download;

public interface IDownloadService
{
    /// <summary>
    /// Scarica un DownloadItem con download parallelo, resume e salvataggio stato.
    /// </summary>
    Task DownloadItemAsync(DownloadItem item, CancellationToken ct = default);
}