using DownloadManager.Models;

namespace DownloadManager.Services.Sftp;

public interface ISftpService
{
    bool IsConnected { get; }
    string CurrentRemotePath { get; }

    Task ConnectAsync(SftpConfig config, CancellationToken ct = default);
    Task DisconnectAsync();
    Task TestConnectionAsync(SftpConfig config, CancellationToken ct = default);

    string GetWorkingDirectory();

    Task<List<SftpRemoteEntry>> ListDirectoryAsync(string remotePath, CancellationToken ct = default);
    Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default);
    Task DeleteFileAsync(string remotePath, CancellationToken ct = default);
    Task DeleteDirectoryAsync(string remotePath, CancellationToken ct = default);
    Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default);

    Task UploadFileAsync(
        string localPath,
        string remotePath,
        IProgress<SftpProgress>? progress = null,
        CancellationToken ct = default);

    Task UploadFolderAsync(
        string localFolder,
        string remoteBaseFolder,
        IProgress<SftpProgress>? progress = null,
        CancellationToken ct = default);

    Task DownloadFileAsync(
        string remotePath,
        string localPath,
        IProgress<SftpProgress>? progress = null,
        CancellationToken ct = default);

    Task DownloadFolderAsync(
        string remoteFolder,
        string localBaseFolder,
        IProgress<SftpProgress>? progress = null,
        CancellationToken ct = default);
}