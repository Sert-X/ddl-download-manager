using DownloadManager.Models;
using Microsoft.Extensions.Logging;
using WinSCP;

namespace DownloadManager.Services.Sftp;

public class SftpWinScpService : ISftpService, IDisposable
{
    private readonly ILogger<SftpWinScpService> _logger;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private Session? _session;
    private SessionOptions? _currentOptions;

    public SftpWinScpService(ILogger<SftpWinScpService> logger)
    {
        _logger = logger;
    }

    public bool IsConnected => _session?.Opened == true;
    public string CurrentRemotePath { get; private set; } = "/";

    // ============================================================
    //  CONNESSIONE
    // ============================================================

    public async Task ConnectAsync(SftpConfig config, CancellationToken ct = default)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            await DisconnectInternalAsync();

            _currentOptions = BuildSessionOptions(config);
            _session = new Session();

            _logger.LogInformation("Connessione WinSCP a {Host}:{Port}...", config.Host, config.Port);

            await Task.Run(() => _session.Open(_currentOptions), ct);

            if (!_session.Opened)
                throw new InvalidOperationException("Connessione WinSCP fallita.");

            string homePath = "/";
            try
            {
                homePath = _session.HomePath;
                if (string.IsNullOrEmpty(homePath)) homePath = "/";
            }
            catch { }

            CurrentRemotePath = string.IsNullOrWhiteSpace(config.RemoteBasePath) || config.RemoteBasePath == "/"
                ? homePath
                : config.RemoteBasePath;

            _logger.LogInformation("Connesso. Path: {Path}", CurrentRemotePath);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        await _sessionGate.WaitAsync();
        try
        {
            await DisconnectInternalAsync();
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private async Task DisconnectInternalAsync()
    {
        if (_session == null) return;

        try
        {
            if (_session.Opened)
                await Task.Run(() => _session.Close());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Errore durante disconnessione");
        }
        finally
        {
            try { _session.Dispose(); } catch { }
            _session = null;
            _currentOptions = null;
        }
    }

    public async Task TestConnectionAsync(SftpConfig config, CancellationToken ct = default)
    {
        var options = BuildSessionOptions(config);

        using var testSession = new Session();
        await Task.Run(() => testSession.Open(options), ct);

        if (!testSession.Opened)
            throw new InvalidOperationException("Connessione fallita.");

        var path = string.IsNullOrWhiteSpace(config.RemoteBasePath) ? "/" : config.RemoteBasePath;

        var list = await Task.Run(() =>
        {
            var result = testSession.ListDirectory(path);
            return result.Files.Take(3).ToList();
        }, ct);

        _logger.LogInformation("Test OK: {Count} elementi visibili in {Path}", list.Count, path);
    }

    public string GetWorkingDirectory()
    {
        try
        {
            if (_session != null && _session.Opened)
            {
                var home = _session.HomePath;
                if (!string.IsNullOrEmpty(home)) return home;
            }
        }
        catch { }

        return string.IsNullOrEmpty(CurrentRemotePath) ? "/" : CurrentRemotePath;
    }

    private static SessionOptions BuildSessionOptions(SftpConfig config)
    {
        var options = new SessionOptions
        {
            Protocol = Protocol.Sftp,
            HostName = config.Host,
            PortNumber = config.Port,
            UserName = config.Username,
            Password = config.Password
        };

        if (!string.IsNullOrEmpty(config.SshHostKeyFingerprint))
        {
            options.SshHostKeyFingerprint = config.SshHostKeyFingerprint;
        }
        else
        {
            options.SshHostKeyPolicy = SshHostKeyPolicy.GiveUpSecurityAndAcceptAny;
        }

        if (config.UseKeyAuth && !string.IsNullOrEmpty(config.PrivateKeyPath))
        {
            options.Password = string.Empty;
            options.PrivateKeyPassphrase = config.PrivateKeyPassphrase;
            options.SshPrivateKeyPath = config.PrivateKeyPath;
        }

        return options;
    }

    private void EnsureConnected()
    {
        if (_session == null || !_session.Opened)
            throw new InvalidOperationException("Non connesso al server SFTP.");
    }

    // ============================================================
    //  LISTING
    // ============================================================

    public async Task<List<SftpRemoteEntry>> ListDirectoryAsync(
        string remotePath, CancellationToken ct = default)
    {
        EnsureConnected();

        if (string.IsNullOrWhiteSpace(remotePath))
            remotePath = "/";

        _logger.LogInformation("Listing: {Path}", remotePath);

        var entries = await _sessionGate.WaitAsync(ct).ContinueWith(_ =>
        {
            try
            {
                var list = new List<SftpRemoteEntry>();
                var result = _session!.ListDirectory(remotePath);

                foreach (RemoteFileInfo file in result.Files)
                {
                    if (file.Name == "." || file.Name == "..") continue;

                    list.Add(new SftpRemoteEntry
                    {
                        Name = file.Name,
                        FullPath = $"{remotePath.TrimEnd('/')}/{file.Name}",
                        IsDirectory = file.IsDirectory,
                        Size = file.Length,
                        Modified = file.LastWriteTime
                    });
                }

                return list;
            }
            finally
            {
                _sessionGate.Release();
            }
        }, ct);

        // Fallback: per i file con size sconosciuta (0 o -1), chiediamo
        // esplicitamente la dimensione con GetFileInfo. Serializzato perché
        // la Session non è thread-safe.
        var toFetch = entries.Where(e => !e.IsDirectory && e.Size <= 0).ToList();
        if (toFetch.Count > 0)
        {
            _logger.LogInformation("Fetching size for {Count} file/i (size sconosciuta)", toFetch.Count);

            await _sessionGate.WaitAsync(ct);
            try
            {
                foreach (var entry in toFetch)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var info = await Task.Run(() => _session!.GetFileInfo(entry.FullPath), ct);
                        if (info != null && !info.IsDirectory && info.Length > 0)
                        {
                            entry.Size = info.Length;
                            entry.Modified = info.LastWriteTime;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Impossibile recuperare info per {Path}", entry.FullPath);
                    }
                }
            }
            finally
            {
                _sessionGate.Release();
            }
        }

        return entries.OrderByDescending(e => e.IsDirectory)
                      .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                      .ToList();
    }

    public async Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        EnsureConnected();

        if (string.IsNullOrWhiteSpace(remotePath)) return;

        var normalized = remotePath.Replace('\\', '/');
        var hasLeadingSlash = normalized.StartsWith('/');
        var parts = normalized.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0) return;

        await _sessionGate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                var current = hasLeadingSlash ? "" : ".";
                foreach (var part in parts)
                {
                    current = current + "/" + part;
                    try
                    {
                        _session!.CreateDirectory(current);
                    }
                    catch (Exception ex)
                    {
                        bool exists = false;
                        try
                        {
                            _session!.ListDirectory(current);
                            exists = true;
                        }
                        catch { }

                        if (!exists)
                        {
                            _logger.LogWarning(ex, "CreateDirectory fallito: {Path}", current);
                            throw;
                        }
                    }
                }
            }, ct);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    // ============================================================
    //  DELETE
    // ============================================================

    public async Task DeleteFileAsync(string remotePath, CancellationToken ct = default)
    {
        EnsureConnected();

        _logger.LogInformation("[DEL-FILE] Request: {Path}", remotePath);

        await _sessionGate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                // 1. Verifica esistenza (con path letterale, senza escape)
                RemoteFileInfo? info = null;
                try
                {
                    info = _session!.GetFileInfo(remotePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DEL-FILE] GetFileInfo fallito su {Path}", remotePath);
                }

                if (info == null)
                    throw new InvalidOperationException($"File non trovato sul server: '{remotePath}'");

                if (info.IsDirectory)
                    throw new InvalidOperationException($"'{remotePath}' è una cartella. Usa DeleteDirectoryAsync.");

                _logger.LogInformation("[DEL-FILE] Exists: {Path} (size={Size})", remotePath, info.Length);

                // 2. Tentativo con EscapeFileMask
                var escaped = RemotePath.EscapeFileMask(remotePath);
                _logger.LogInformation("[DEL-FILE] Attempt 1 (escaped): {Escaped}", escaped);

                var result = _session!.RemoveFiles(escaped);

                _logger.LogInformation("[DEL-FILE] Result: IsSuccess={Ok} Removals={N} Failures={F}",
                    result.IsSuccess, result.Removals.Count, result.Failures.Count);

                if (result.IsSuccess && result.Removals.Count > 0)
                    return;

                // 3. Fallback: senza escape (alcuni server non gradiscono l'escape sui path)
                _logger.LogWarning("[DEL-FILE] Attempt 1 failed, trying raw path: {Path}", remotePath);

                var result2 = _session!.RemoveFiles(remotePath);

                _logger.LogInformation("[DEL-FILE] Result 2: IsSuccess={Ok} Removals={N} Failures={F}",
                    result2.IsSuccess, result2.Removals.Count, result2.Failures.Count);

                if (result2.IsSuccess && result2.Removals.Count > 0)
                    return;

                // 4. Fallback estremo: prova dal path relativo alla cartella corrente
                var parent = GetParentPath(remotePath);
                var fileName = remotePath.Substring(remotePath.LastIndexOf('/') + 1);
                var mask = $"{RemotePath.EscapeFileMask(parent)}/{RemotePath.EscapeFileMask(fileName)}";

                _logger.LogWarning("[DEL-FILE] Attempt 2 failed, trying recomposed mask: {Mask}", mask);

                var result3 = _session!.RemoveFiles(mask);

                if (result3.IsSuccess && result3.Removals.Count > 0)
                    return;

                // Tutti i tentativi falliti
                var errors = new List<string>();
                if (result.Failures.Count > 0)
                    errors.Add($"escaped: {string.Join("; ", result.Failures.Select(f => f.Message))}");
                if (result2.Failures.Count > 0)
                    errors.Add($"raw: {string.Join("; ", result2.Failures.Select(f => f.Message))}");

                var detail = errors.Count > 0 ? string.Join(" | ", errors) : "nessun dettaglio dal server";
                throw new InvalidOperationException(
                    $"Eliminazione non riuscita per '{remotePath}'. Dettagli: {detail}");
            }, ct);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public async Task DeleteDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        EnsureConnected();

        _logger.LogInformation("[DEL-DIR] Request: {Path}", remotePath);

        await _sessionGate.WaitAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                // Verifica esistenza
                RemoteFileInfo? info = null;
                try
                {
                    info = _session!.GetFileInfo(remotePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[DEL-DIR] GetFileInfo fallito su {Path}", remotePath);
                }

                if (info == null)
                    throw new InvalidOperationException($"Cartella non trovata sul server: '{remotePath}'");

                if (!info.IsDirectory)
                    throw new InvalidOperationException($"'{remotePath}' non è una cartella. Usa DeleteFileAsync.");

                // WinSCP rimuove ricorsivamente se passi "path/*"
                var normalized = remotePath.TrimEnd('/');

                // Tentativo 1: path/ con escape
                var escaped = RemotePath.EscapeFileMask(normalized) + "/";
                _logger.LogInformation("[DEL-DIR] Attempt 1: {Escaped}", escaped);

                var result = _session!.RemoveFiles(escaped);

                _logger.LogInformation("[DEL-DIR] Result 1: IsSuccess={Ok} Removals={N}",
                    result.IsSuccess, result.Removals.Count);

                if (result.IsSuccess && result.Removals.Count > 0)
                    return;

                // Tentativo 2: senza escape
                var raw = normalized + "/";
                _logger.LogWarning("[DEL-DIR] Attempt 1 failed, trying raw: {Raw}", raw);

                var result2 = _session!.RemoveFiles(raw);

                if (result2.IsSuccess && result2.Removals.Count > 0)
                    return;

                var errs = new List<string>();
                if (result.Failures.Count > 0)
                    errs.Add(string.Join("; ", result.Failures.Select(f => f.Message)));
                if (result2.Failures.Count > 0)
                    errs.Add(string.Join("; ", result2.Failures.Select(f => f.Message)));

                var detail = errs.Count > 0 ? string.Join(" | ", errs) : "nessun dettaglio dal server";
                throw new InvalidOperationException(
                    $"Eliminazione cartella non riuscita per '{remotePath}'. Dettagli: {detail}");
            }, ct);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// Ritorna il path della cartella genitore di un path remoto (con /).
    /// </summary>
    private static string GetParentPath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath)) return "/";
        var trimmed = fullPath.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed.Substring(0, lastSlash);
    }

    public async Task RenameAsync(string oldPath, string newPath, CancellationToken ct = default)
    {
        EnsureConnected();

        await _sessionGate.WaitAsync(ct);
        try
        {
            await Task.Run(() => _session!.MoveFile(oldPath, newPath), ct);
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    // ============================================================
    //  UPLOAD
    // ============================================================

    public async Task UploadFileAsync(
        string localPath, string remotePath,
        IProgress<SftpProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();

        if (!File.Exists(localPath))
            throw new FileNotFoundException("File locale non trovato", localPath);

        var fileInfo = new FileInfo(localPath);

        var transferOptions = new TransferOptions
        {
            ResumeSupport = new TransferResumeSupport { State = TransferResumeSupportState.On },
            TransferMode = TransferMode.Binary
        };

        _logger.LogInformation("Upload: {Local} → {Remote}", localPath, remotePath);

        await _sessionGate.WaitAsync(ct);
        try
        {
            var result = await Task.Run(() =>
                _session!.PutFiles(localPath, remotePath, false, transferOptions), ct);

            if (!result.IsSuccess)
            {
                var error = string.Join("; ", result.Failures.Select(f => f.Message));
                throw new InvalidOperationException($"Upload fallito: {error}");
            }
        }
        finally
        {
            _sessionGate.Release();
        }

        progress?.Report(new SftpProgress
        {
            CurrentFile = Path.GetFileName(localPath),
            FilesTotal = 1,
            FilesCompleted = 1,
            TotalBytes = fileInfo.Length,
            UploadedBytes = fileInfo.Length,
            Status = "completed"
        });
    }

    public async Task UploadFolderAsync(
        string localFolder, string remoteBaseFolder,
        IProgress<SftpProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();

        if (!Directory.Exists(localFolder))
            throw new DirectoryNotFoundException($"Cartella non trovata: {localFolder}");

        var localFolderName = Path.GetFileName(localFolder.TrimEnd(Path.DirectorySeparatorChar));
        var remoteTarget = $"{remoteBaseFolder.TrimEnd('/')}/{localFolderName}";

        var transferOptions = new TransferOptions
        {
            ResumeSupport = new TransferResumeSupport { State = TransferResumeSupportState.On },
            TransferMode = TransferMode.Binary
        };

        await _sessionGate.WaitAsync(ct);
        try
        {
            var result = await Task.Run(() =>
                _session!.PutFiles(localFolder, remoteTarget, false, transferOptions), ct);

            if (!result.IsSuccess)
            {
                var error = string.Join("; ", result.Failures.Select(f => f.Message));
                throw new InvalidOperationException($"Upload cartella fallito: {error}");
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    // ============================================================
    //  DOWNLOAD
    // ============================================================

    public async Task DownloadFileAsync(
        string remotePath, string localPath,
        IProgress<SftpProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();

        var dir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var transferOptions = new TransferOptions
        {
            ResumeSupport = new TransferResumeSupport { State = TransferResumeSupportState.On },
            TransferMode = TransferMode.Binary
        };

        await _sessionGate.WaitAsync(ct);
        try
        {
            var result = await Task.Run(() =>
                _session!.GetFiles(remotePath, localPath, false, transferOptions), ct);

            if (!result.IsSuccess)
            {
                var error = string.Join("; ", result.Failures.Select(f => f.Message));
                throw new InvalidOperationException($"Download fallito: {error}");
            }
        }
        finally
        {
            _sessionGate.Release();
        }

        progress?.Report(new SftpProgress
        {
            CurrentFile = Path.GetFileName(localPath),
            FilesTotal = 1,
            FilesCompleted = 1,
            Status = "completed"
        });
    }

    public async Task DownloadFolderAsync(
        string remoteFolder, string localBaseFolder,
        IProgress<SftpProgress>? progress = null, CancellationToken ct = default)
    {
        EnsureConnected();

        var remoteFolderName = Path.GetFileName(remoteFolder.TrimEnd('/'));
        var localTarget = Path.Combine(localBaseFolder, remoteFolderName);

        Directory.CreateDirectory(localTarget);

        var transferOptions = new TransferOptions
        {
            ResumeSupport = new TransferResumeSupport { State = TransferResumeSupportState.On },
            TransferMode = TransferMode.Binary
        };

        await _sessionGate.WaitAsync(ct);
        try
        {
            var result = await Task.Run(() =>
                _session!.GetFiles(remoteFolder + "/*", localTarget, false, transferOptions), ct);

            if (!result.IsSuccess)
            {
                var error = string.Join("; ", result.Failures.Select(f => f.Message));
                throw new InvalidOperationException($"Download cartella fallito: {error}");
            }
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    public void Dispose()
    {
        try { _session?.Dispose(); } catch { }
        _session = null;
        _sessionGate.Dispose();
    }
}