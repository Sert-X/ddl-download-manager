using DownloadManager.Models;

namespace DownloadManager.Services.Sftp;

public interface ISftpService
{
    bool IsConnected { get; }
    string CurrentRemotePath { get; }

    /// <summary>Server a cui si è connessi (host:porta:utente), null se disconnessi.</summary>
    string? ServerKey { get; }

    /// <summary>Sollevato dopo ogni modifica riuscita al filesystem remoto.</summary>
    event Action<SftpRemoteChange>? RemoteChanged;

    Task ConnectAsync(SftpConfig config, CancellationToken ct = default);
    Task DisconnectAsync();
    Task TestConnectionAsync(SftpConfig config, CancellationToken ct = default);

    string GetWorkingDirectory();

    Task<List<SftpRemoteEntry>> ListDirectoryAsync(string remotePath, CancellationToken ct = default);
    Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default);

    /// <summary>Numero di cartelle sotto il percorso (lei compresa, senza quelle nascoste); null se il server non lo permette.</summary>
    Task<int?> CountDirectoriesAsync(string remotePath, CancellationToken ct = default);
    Task DeleteFileAsync(string remotePath, CancellationToken ct = default);
    Task DeleteDirectoryAsync(string remotePath, CancellationToken ct = default);
    Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default);

    /// <summary>Copia un file o una cartella sul server stesso (i dati non passano dal PC).</summary>
    Task DuplicateAsync(string sourcePath, string targetPath, CancellationToken ct = default);

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
    void AbortCurrentOperation();

    /// <summary>Segnala una modifica fatta da fuori (es. copia da un altro server), così chi ascolta si aggiorna.</summary>
    void NotifyRemoteChanged(SftpRemoteChange change);
}

/// <summary>
/// Cartella remota il cui contenuto è cambiato. Se <paramref name="Recursive"/> è false
/// è cambiato solo il contenuto diretto; se true anche le sottocartelle.
/// </summary>
public record SftpRemoteChange(string DirectoryPath, bool Recursive = false);
