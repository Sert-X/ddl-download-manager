using System.Collections.ObjectModel;
using Avalonia.Threading;
using DownloadManager.Models;
using Microsoft.Extensions.Logging;
using WinSCP;

namespace DownloadManager.Services.Sftp;

public class SftpUploadQueueService
{
    private readonly ILogger<SftpUploadQueueService> _logger;

    private readonly Queue<SftpUploadJob> _pending = new();
    private readonly HashSet<int> _pendingIds = new();
    private readonly Dictionary<int, CancellationTokenSource> _active = new();
    private readonly Dictionary<int, Session> _activeSessions = new();
    private readonly object _queueLock = new();

    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly HashSet<string> _ensuredDirs = new(StringComparer.OrdinalIgnoreCase);

    private SftpConfig? _currentConfig;
    private int _maxConcurrency = 6;
    private bool _isPaused = false;
    private bool _isRunning = true;
    private int _nextJobId = 1;

    public SftpUploadQueueService(ILogger<SftpUploadQueueService> logger)
    {
        _logger = logger;
    }

    public ObservableCollection<SftpUploadJob> AllJobs { get; } = new();

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            if (value < 1) value = 1;
            if (value > 24) value = 24;
            _maxConcurrency = value;
            _logger.LogInformation("Max concurrency SFTP: {Value}", value);
            TryDispatch();
        }
    }

    public int ActiveCount { get { lock (_queueLock) return _active.Count; } }
    public int PendingCount { get { lock (_queueLock) return _pending.Count; } }
    public bool IsPaused => _isPaused;

    public event Action? QueueChanged;

    public void Configure(SftpConfig config)
    {
        _currentConfig = config;
        _ensuredDirs.Clear();
    }

    // ============================================================
    //  ENQUEUE
    // ============================================================

    public async Task<int> EnqueueFolderAsync(string localFolder, string remoteBaseFolder)
    {
        if (_currentConfig == null)
            throw new InvalidOperationException("Nessuna configurazione SFTP impostata.");

        if (!Directory.Exists(localFolder))
            throw new DirectoryNotFoundException($"Cartella non trovata: {localFolder}");

        var allFiles = Directory.GetFiles(localFolder, "*", SearchOption.AllDirectories);
        var localFolderName = Path.GetFileName(localFolder.TrimEnd(Path.DirectorySeparatorChar));
        var remoteTarget = CombineRemote(remoteBaseFolder, localFolderName);

        int count = 0;

        foreach (var localFile in allFiles)
        {
            var relativePath = Path.GetRelativePath(localFolder, localFile).Replace('\\', '/');
            var remoteFile = CombineRemote(remoteTarget, relativePath);

            var job = new SftpUploadJob
            {
                Id = _nextJobId++,
                LocalPath = localFile,
                RemotePath = remoteFile,
                TotalBytes = new FileInfo(localFile).Length,
                Status = SftpJobStatus.Pending
            };

            await EnqueueJobAsync(job);
            count++;
        }

        return count;
    }

    public async Task EnqueueJobAsync(SftpUploadJob job)
    {
        if (job.Id == 0)
            job.Id = _nextJobId++;

        await Dispatcher.UIThread.InvokeAsync(() => AllJobs.Add(job));

        lock (_queueLock)
        {
            if (_pendingIds.Add(job.Id))
                _pending.Enqueue(job);
        }

        QueueChanged?.Invoke();
        TryDispatch();
    }

    // ============================================================
    //  START / PAUSE / CANCEL
    // ============================================================

    public void StartAll()
    {
        _isPaused = false;

        lock (_queueLock)
        {
            foreach (var job in AllJobs)
            {
                if (job.Status == SftpJobStatus.Paused ||
                    job.Status == SftpJobStatus.Pending ||
                    job.Status == SftpJobStatus.Failed)
                {
                    job.Status = SftpJobStatus.Pending;
                    job.ErrorMessage = string.Empty;

                    if (!_active.ContainsKey(job.Id) && _pendingIds.Add(job.Id))
                        _pending.Enqueue(job);
                }
            }
        }

        QueueChanged?.Invoke();
        TryDispatch();
    }

    public void PauseAll()
    {
        _isPaused = true;

        List<SftpUploadJob> toPause;
        lock (_queueLock)
        {
            toPause = _active.Keys
                .Select(id => AllJobs.FirstOrDefault(j => j.Id == id))
                .Where(j => j != null)
                .Cast<SftpUploadJob>()
                .ToList();

            while (_pending.TryDequeue(out var job))
            {
                _pendingIds.Remove(job.Id);
                if (job.Status == SftpJobStatus.Pending)
                    job.Status = SftpJobStatus.Paused;
            }
        }

        foreach (var job in toPause)
        {
            job.PauseRequested = true;

            if (_activeSessions.TryGetValue(job.Id, out var session))
            {
                try { session.Abort(); } catch { }
            }

            if (_active.TryGetValue(job.Id, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }

        QueueChanged?.Invoke();
    }

    // FIX: marca come Cancelled anche Pending, Paused e Failed
    public void CancelAll()
    {
        List<int> activeIds;
        List<SftpUploadJob> toCancel;

        lock (_queueLock)
        {
            activeIds = _active.Keys.ToList();
            toCancel = AllJobs
                .Where(j => j.Status == SftpJobStatus.Pending
                         || j.Status == SftpJobStatus.Paused
                         || j.Status == SftpJobStatus.Failed)
                .ToList();

            _pending.Clear();
            _pendingIds.Clear();
        }

        foreach (var job in toCancel)
        {
            job.PauseRequested = false;
            job.Status = SftpJobStatus.Cancelled;
        }

        foreach (var id in activeIds)
        {
            if (_activeSessions.TryGetValue(id, out var session))
            {
                try { session.Abort(); } catch { }
            }
            if (_active.TryGetValue(id, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }

        QueueChanged?.Invoke();
    }

    public void ClearCompleted()
    {
        var toRemove = AllJobs.Where(j => j.Status == SftpJobStatus.Completed).ToList();
        foreach (var job in toRemove)
            AllJobs.Remove(job);
    }

    public void ClearCancelled()
    {
        var toRemove = AllJobs.Where(j => j.Status == SftpJobStatus.Cancelled).ToList();
        foreach (var job in toRemove)
            AllJobs.Remove(job);
    }

    public void ClearFailed()
    {
        var toRemove = AllJobs.Where(j => j.Status == SftpJobStatus.Failed).ToList();
        foreach (var job in toRemove)
            AllJobs.Remove(job);
    }

    // ============================================================
    //  PER SINGOLO JOB
    // ============================================================

    public void PauseJob(SftpUploadJob job)
    {
        if (job.Status == SftpJobStatus.Completed) return;

        job.PauseRequested = true;

        if (_active.TryGetValue(job.Id, out var cts))
        {
            if (_activeSessions.TryGetValue(job.Id, out var session))
            {
                try { session.Abort(); } catch { }
            }
            try { cts.Cancel(); } catch { }
        }
        else
        {
            lock (_queueLock) { _pendingIds.Remove(job.Id); }
            job.Status = SftpJobStatus.Paused;
        }

        QueueChanged?.Invoke();
    }

    public void ResumeJob(SftpUploadJob job)
    {
        if (job.Status == SftpJobStatus.Completed ||
            job.Status == SftpJobStatus.Uploading)
            return;

        job.Status = SftpJobStatus.Pending;
        job.PauseRequested = false;
        job.ErrorMessage = string.Empty;

        lock (_queueLock)
        {
            if (_pendingIds.Add(job.Id))
                _pending.Enqueue(job);
        }

        _isPaused = false;
        QueueChanged?.Invoke();
        TryDispatch();
    }

    public void CancelJob(SftpUploadJob job)
    {
        job.PauseRequested = false;

        if (_active.TryGetValue(job.Id, out var cts))
        {
            if (_activeSessions.TryGetValue(job.Id, out var session))
            {
                try { session.Abort(); } catch { }
            }
            try { cts.Cancel(); } catch { }
        }
        else
        {
            lock (_queueLock) { _pendingIds.Remove(job.Id); }
            job.Status = SftpJobStatus.Cancelled;
        }

        QueueChanged?.Invoke();
    }

    public void RemoveJob(SftpUploadJob job)
    {
        if (_active.TryGetValue(job.Id, out var cts))
        {
            if (_activeSessions.TryGetValue(job.Id, out var session))
            {
                try { session.Abort(); } catch { }
            }
            try { cts.Cancel(); } catch { }
        }

        lock (_queueLock) { _pendingIds.Remove(job.Id); }

        Dispatcher.UIThread.Post(() => AllJobs.Remove(job));
        QueueChanged?.Invoke();
    }

    // ============================================================
    //  DISPATCH
    // ============================================================

    private void TryDispatch()
    {
        if (!_dispatchGate.Wait(0)) return;

        Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    SftpUploadJob? job = null;
                    CancellationTokenSource? cts = null;

                    lock (_queueLock)
                    {
                        if (!_isRunning) return;
                        if (_isPaused) return;
                        if (_active.Count >= _maxConcurrency) return;
                        if (_currentConfig == null) return;
                        if (!_pending.TryDequeue(out job)) return;

                        _pendingIds.Remove(job.Id);

                        if (job.Status == SftpJobStatus.Cancelled ||
                            job.Status == SftpJobStatus.Completed ||
                            job.Status == SftpJobStatus.Paused)
                        {
                            continue;
                        }

                        cts = new CancellationTokenSource();
                        _active[job.Id] = cts;
                    }

                    await Task.Delay(Random.Shared.Next(100, 400));

                    _ = RunJobAsync(job!, cts!);
                }
            }
            finally
            {
                _dispatchGate.Release();
            }
        });
    }

    // ============================================================
    //  RUN JOB
    // ============================================================

    private async Task RunJobAsync(SftpUploadJob job, CancellationTokenSource cts)
    {
        Session? session = null;

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => job.Status = SftpJobStatus.Uploading);
            QueueChanged?.Invoke();

            session = new Session();

            long lastUiUpdate = 0;

            void OnFileTransferProgress(object? sender, FileTransferProgressEventArgs e)
            {
                // FIX: WinSCP a volte dà 0-1, a volte 0-100. Gestiamo entrambi.
                var val = e.FileProgress;
                double percent = val > 1.0 ? val : val * 100.0;
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;

                var uploadedBytes = (long)(job.TotalBytes * (percent / 100.0));
                var speed = e.CPS;

                var now = Environment.TickCount64;
                if (now - lastUiUpdate < 100) return;
                lastUiUpdate = now;

                Dispatcher.UIThread.Post(() =>
                {
                    job.UploadedBytes = uploadedBytes;
                    job.SpeedBytesPerSecond = speed;
                });
            }

            session.FileTransferProgress += OnFileTransferProgress;

            lock (_queueLock) { _activeSessions[job.Id] = session; }

            try
            {
                var options = CreateSessionOptions(_currentConfig!);

                var opened = await TryOpenWithRetryAsync(session, options, cts.Token);

                if (!opened)
                    throw new InvalidOperationException("Impossibile connettersi al server dopo diversi tentativi.");

                var remoteDir = Path.GetDirectoryName(job.RemotePath)?.Replace('\\', '/') ?? "";
                if (!string.IsNullOrEmpty(remoteDir))
                {
                    EnsureRemoteDir(session, remoteDir);
                }

                var transferOptions = new TransferOptions
                {
                    ResumeSupport = new TransferResumeSupport
                    {
                        State = TransferResumeSupportState.On
                    },
                    TransferMode = TransferMode.Binary
                };

                TransferOperationResult result;

                try
                {
                    result = await Task.Run(() =>
                        session.PutFiles(job.LocalPath, job.RemotePath, false, transferOptions), cts.Token);
                }
                catch (Exception) when (cts.IsCancellationRequested)
                {
                    throw new OperationCanceledException();
                }

                if (cts.IsCancellationRequested)
                {
                    if (job.PauseRequested)
                        await Dispatcher.UIThread.InvokeAsync(() => job.Status = SftpJobStatus.Paused);
                    else
                        await Dispatcher.UIThread.InvokeAsync(() => job.Status = SftpJobStatus.Cancelled);
                    return;
                }

                if (!result.IsSuccess)
                {
                    var error = string.Join("; ", result.Failures.Select(f => f.Message));
                    throw new InvalidOperationException(error);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    job.UploadedBytes = job.TotalBytes;
                    job.Status = SftpJobStatus.Completed;
                    job.SpeedBytesPerSecond = 0;
                });
            }
            finally
            {
                try { session.FileTransferProgress -= OnFileTransferProgress; } catch { }
            }
        }
        catch (OperationCanceledException)
        {
            if (job.PauseRequested)
                await Dispatcher.UIThread.InvokeAsync(() => job.Status = SftpJobStatus.Paused);
            else
                await Dispatcher.UIThread.InvokeAsync(() => job.Status = SftpJobStatus.Cancelled);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested)
            {
                if (job.PauseRequested)
                    await Dispatcher.UIThread.InvokeAsync(() => job.Status = SftpJobStatus.Paused);
                else
                    await Dispatcher.UIThread.InvokeAsync(() => job.Status = SftpJobStatus.Cancelled);
                return;
            }

            _logger.LogError(ex, "Upload fallito: {File}", job.LocalPath);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                job.Status = SftpJobStatus.Failed;
                job.ErrorMessage = ex.Message;
            });
        }
        finally
        {
            lock (_queueLock) { _activeSessions.Remove(job.Id); }

            try { session?.Dispose(); } catch { }

            job.PauseRequested = false;

            lock (_queueLock) { _active.Remove(job.Id); }

            QueueChanged?.Invoke();
            TryDispatch();
        }
    }

    private void EnsureRemoteDir(Session session, string remoteDir)
    {
        if (string.IsNullOrEmpty(remoteDir)) return;
        if (!_ensuredDirs.Add(remoteDir)) return;

        var parts = remoteDir.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        foreach (var p in parts)
        {
            current += "/" + p;
            try { session.CreateDirectory(current); }
            catch { }
        }
    }

    // ============================================================
    //  RETRY CON BACKOFF
    // ============================================================

    private async Task<bool> TryOpenWithRetryAsync(
        Session session, SessionOptions options, CancellationToken ct)
    {
        const int maxAttempts = 5;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await Task.Run(() => session.Open(options), ct);

                if (session.Opened)
                    return true;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                _logger.LogWarning(
                    "Tentativo {Attempt}/{Max} fallito: {Msg}",
                    attempt, maxAttempts, ex.Message);

                var baseDelay = 500 * Math.Pow(2, attempt - 1);
                var jitter = Random.Shared.Next(0, 500);
                var delay = (int)(baseDelay + jitter);

                await Task.Delay(delay, ct);
            }
        }

        return false;
    }

    // ============================================================
    //  ENQUEUE DA PATH ARBITRARI (drag dal SO)
    // ============================================================

    /// <summary>
    /// Accoda file e/o cartelle locali verso una cartella remota.
    /// Le cartelle vengono espanse in una sottocartella con lo stesso nome
    /// (comportamento di EnqueueFolderAsync). I file vengono accodati direttamente.
    /// Restituisce il numero di file accodati.
    /// </summary>
    public async Task<int> EnqueuePathsAsync(IEnumerable<string> localPaths, string remoteBaseFolder)
    {
        if (_currentConfig == null)
            throw new InvalidOperationException("Nessuna configurazione SFTP impostata.");

        int total = 0;

        foreach (var localPath in localPaths)
        {
            if (Directory.Exists(localPath))
            {
                total += await EnqueueFolderAsync(localPath, remoteBaseFolder);
            }
            else if (File.Exists(localPath))
            {
                var fileName = Path.GetFileName(localPath);
                var remotePath = CombineRemote(remoteBaseFolder, fileName);

                var job = new SftpUploadJob
                {
                    Id = _nextJobId++,
                    LocalPath = localPath,
                    RemotePath = remotePath,
                    TotalBytes = new FileInfo(localPath).Length,
                    Status = SftpJobStatus.Pending
                };

                await EnqueueJobAsync(job);
                total++;
            }
        }

        return total;
    }

    // ============================================================
    //  HELPERS
    // ============================================================

    private static SessionOptions CreateSessionOptions(SftpConfig config)
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

    private static string CombineRemote(string basePath, string relative)
    {
        if (string.IsNullOrEmpty(basePath)) basePath = "/";
        var baseNorm = basePath.TrimEnd('/');
        var rel = relative.Replace('\\', '/').TrimStart('/');
        if (baseNorm == "") return "/" + rel;
        return baseNorm + "/" + rel;
    }
}