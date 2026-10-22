namespace DownloadManager.Models;

public enum SftpJobStatus
{
    Pending,
    Uploading,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled,
    /// <summary>Copia da un server SFTP a un altro in corso.</summary>
    Transferring
}