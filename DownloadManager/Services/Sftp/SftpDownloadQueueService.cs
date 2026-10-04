using System.Collections.ObjectModel;
using Avalonia.Threading;
using DownloadManager.Models;
using Microsoft.Extensions.Logging;
using WinSCP;

namespace DownloadManager.Services.Sftp;

public class SftpDownloadQueueService
{
    private readonly ILogger<SftpDownloadQueueService> _logger;

    private readonly Queue<SftpDownloadJob> _pending = new();
    private readonly HashSet<int> _pendingIds = new();
    private readonly Dictionary<int, CancellationTokenSource> _active = new();
    private readonly Dictionary<int, Session> _activeSessions = new();
    private readonly object _queueLock = new();

    private readonly SemaphoreSlim _dispatchGate = new(1, 1);

    private SftpConfig? _currentConfig;
    private int _maxConcurrency = 6;
    private bool _isPaused = false;
    private bool _isRunning = true;
    private int _nextJobId = 1;

    public SftpDownloadQueueService(ILogger<SftpDownloadQueueService> logger)
    {
        _logger = logger;
    }

    public ObservableCollection<SftpDownloadJob> AllJobs { get; } = new();

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            if (value < 1) value = 1;
            if (value > 24) value = 24;
            _maxConcurrency = value;
            _logger.LogInformation("Max concurrency SFTP download: {Value}", value);
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
    }

    // ============================================================
    //  ENQUEUE
    // ============================================================

    public async Task EnqueueJobAsync(SftpDownloadJob job)
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

        List<SftpDownloadJob> toPause;
        lock (_queueLock)
        {
            toPause = _active.Keys
                .Select(id => AllJobs.FirstOrDefault(j => j.Id == id))
                .Where(j => j != null)
                .Cast<SftpDownloadJob>()
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

    // Cancella TUTTI: attivi, pending, paused, failed → Cancelled
    public void CancelAll()
    {
        List<int> activeIds;
        List<SftpDownloadJob> toCancel;

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
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var job in toRemove)
                AllJobs.Remove(job);
            QueueChanged?.Invoke();
        });
    }

    public void ClearCancelled()
    {
        var toRemove = AllJobs.Where(j => j.Status == SftpJobStatus.Cancelled).ToList();
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var job in toRemove)
                AllJobs.Remove(job);
            QueueChanged?.Invoke();
        });
    }

    public void ClearFailed()
    {
        var toRemove = AllJobs.Where(j => j.Status == SftpJobStatus.Failed).ToList();
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var job in toRemove)
                AllJobs.Remove(job);
            QueueChanged?.Invoke();
        });
    }

    // ============================================================
    //  PER SINGOLO JOB
    // ============================================================

    public void PauseJob(SftpDownloadJob job)
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

    public void ResumeJob(SftpDownloadJob job)
    {
        if (job.Status == SftpJobStatus.Completed ||
            job.Status == SftpJobStatus.Downloading)
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

    public void CancelJob(SftpDownloadJob job)
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

    public void RemoveJob(SftpDownloadJob job)
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

        Dispatcher.UIThread.Post(() =>
        {
            AllJobs.Remove(job);
            QueueChanged?.Invoke();
        });
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
                    SftpDownloadJob? job = null;
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

    private async Task RunJobAsync(SftpDownloadJob job, CancellationTokenSource cts)
    {
        Session? session = null;

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => job.Status = SftpJobStatus.Downloading);
            QueueChanged?.Invoke();

            session = new Session();

            long lastUiUpdate = 0;

            void OnFileTransferProgress(object? sender, FileTransferProgressEventArgs e)
            {
                var val = e.FileProgress;
                double percent = val > 1.0 ? val : val * 100.0;
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;

                var downloadedBytes = (long)(job.TotalBytes * (percent / 100.0));
                var speed = e.CPS;

                var now = Environment.TickCount64;
                if (now - lastUiUpdate < 100) return;
                lastUiUpdate = now;

                Dispatcher.UIThread.Post(() =>
                {
                    job.DownloadedBytes = downloadedBytes;
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

                var localDir = Path.GetDirectoryName(job.LocalPath);
                if (string.IsNullOrEmpty(localDir))
                    localDir = Directory.GetCurrentDirectory();

                Directory.CreateDirectory(localDir);

                var transferOptions = new TransferOptions
                {
                    ResumeSupport = new TransferResumeSupport
                    {
                        State = TransferResumeSupportState.On
                    },
                    TransferMode = TransferMode.Binary
                };

                // WinSCP: GetFileToDirectory scarica un singolo file SENZA interpretare
                // wildcard come [ ] * ?. Con GetFiles, nomi tipo 'film [m1080p].mp4'
                // vengono interpretati come mask e falliscono.
                var remoteFileName = Path.GetFileName(job.RemotePath.Replace('\\', '/'));
                var expectedFile = Path.Combine(localDir, remoteFileName);

                _logger.LogInformation(
                    "Job #{Id}: GetFileToDirectory remote='{Remote}' → dir='{Dir}' (atteso: '{Expected}')",
                    job.Id, job.RemotePath, localDir, expectedFile);

                TransferEventArgs result;

                try
                {
                    result = await Task.Run(() =>
                        session.GetFileToDirectory(job.RemotePath, localDir, false, transferOptions), cts.Token);
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

                // GetFileToDirectory lancia eccezione su errore: se siamo qui, il trasferimento è
                // riuscito. Nessun IsSuccess/Failures da controllare.
                // Verifica difensiva che il file esista davvero.
                if (!File.Exists(expectedFile))
                {
                    var filesInDir = Directory.Exists(localDir)
                        ? string.Join(", ", Directory.GetFiles(localDir).Select(Path.GetFileName).Take(10))
                        : "(cartella inesistente)";

                    throw new InvalidOperationException(
                        $"Download riportato come riuscito, ma '{expectedFile}' non esiste. " +
                        $"File presenti: {filesInDir}");
                }

                if (!File.Exists(expectedFile))
                {
                    var filesInDir = Directory.Exists(localDir)
                        ? string.Join(", ", Directory.GetFiles(localDir).Select(Path.GetFileName).Take(10))
                        : "(cartella inesistente)";

                    throw new InvalidOperationException(
                        $"Download riportato come riuscito, ma '{expectedFile}' non esiste. " +
                        $"File presenti: {filesInDir}");
                }

                if (!string.Equals(expectedFile, job.LocalPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(job.LocalPath))
                    {
                        try { File.Delete(job.LocalPath); } catch { }
                    }
                    File.Move(expectedFile, job.LocalPath);
                    _logger.LogInformation("Job #{Id}: rinominato '{From}' → '{To}'",
                        job.Id, expectedFile, job.LocalPath);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    job.DownloadedBytes = job.TotalBytes;
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

            _logger.LogError(ex, "Download fallito: {File}", job.RemotePath);
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
            options.SshHostKeyFingerprint = config.SshHostKeyFingerprint;
        else
            options.SshHostKeyPolicy = SshHostKeyPolicy.GiveUpSecurityAndAcceptAny;

        if (config.UseKeyAuth && !string.IsNullOrEmpty(config.PrivateKeyPath))
        {
            options.Password = string.Empty;
            options.PrivateKeyPassphrase = config.PrivateKeyPassphrase;
            options.SshPrivateKeyPath = config.PrivateKeyPath;
        }

        return options;
    }
}