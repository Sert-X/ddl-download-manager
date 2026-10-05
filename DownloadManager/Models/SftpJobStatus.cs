namespace DownloadManager.Models;

public enum SftpJobStatus
{
    Pending,
    Uploading,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled
}