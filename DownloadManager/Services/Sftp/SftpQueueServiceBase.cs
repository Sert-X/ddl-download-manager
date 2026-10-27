using System.Collections.ObjectModel;
using Avalonia.Threading;
using DownloadManager.Models;
using Microsoft.Extensions.Logging;
using WinSCP;

namespace DownloadManager.Services.Sftp;

/// <summary>
/// Base comune per le code di trasferimento SFTP (upload/download).
/// Gestisce: pending/active, dispatch parallelo, pausa/annulla, retry con backoff,
/// progress update, eventi QueueChanged. I figli forniscono solo la logica
/// specifica di trasferimento + le etichette per lo stato attivo e i log.
/// </summary>
public abstract class SftpQueueServiceBase<TJob> where TJob : TransferJobBase
{
    protected readonly ILogger _logger;

    // Lista (non coda): si prende il primo job il cui server ha ancora posti liberi
    private readonly List<TJob> _pending = new();
    private readonly HashSet<int> _pendingIds = new();
    private readonly Dictionary<int, CancellationTokenSource> _active = new();
    private readonly Dictionary<int, Session> _activeSessions = new();
    /// <summary>Server di ogni job in corso (id job → chiave server), per il limite per server.</summary>
    private readonly Dictionary<int, string> _activeServers = new();
    /// <summary>Limiti di sessioni dei server diversi da quello principale (uno per server).</summary>
    private readonly Dictionary<string, SftpSessionLimiter> _otherLimiters = new(StringComparer.OrdinalIgnoreCase);
    protected readonly object _queueLock = new();

    private readonly SemaphoreSlim _dispatchGate = new(1, 1);

    private SftpConfig? _currentConfig;
    private int _maxConcurrency = 6;
    private bool _isPaused = false;
    private bool _isRunning = true;
    private int _nextJobId = 1;

    // ---------- Hook astratti per i figli ----------

    /// <summary>Stato del job quando è in corso (Uploading/Downloading).</summary>
    protected abstract SftpJobStatus ActiveStatus { get; }

    /// <summary>Etichetta per i log (es. "Upload" / "Download").</summary>
    protected abstract string LogCategory { get; }

    /// <summary>Etichetta per il log di MaxConcurrency (es. "SFTP" / "SFTP download").</summary>
    protected abstract string ConcurrencyLogLabel { get; }

    /// <summary>Imposta la property di progress specifica del figlio (UploadedBytes/DownloadedBytes).</summary>
    protected abstract void SetProgressBytes(TJob job, long bytes);

    /// <summary>Imposta la property di progress a TotalBytes al termine.</summary>
    protected abstract void SetCompletedBytes(TJob job);

    /// <summary>Descrizione del job per il log di errore (path sorgente).</summary>
    protected abstract string GetJobLogDescription(TJob job);

    /// <summary>Esegue il trasferimento vero e proprio (PutFiles / GetFileToDirectory + verifica/rename).</summary>
    protected abstract Task ExecuteTransferAsync(Session session, TJob job, CancellationToken ct);

    /// <summary>Server a cui si collega la sessione del job: il suo, altrimenti quello configurato.</summary>
    protected virtual SftpConfig? GetSessionConfig(TJob job) => job.Server ?? _currentConfig;

    /// <summary>Chiave del server del job: il limite di trasferimenti in parallelo vale per server.</summary>
    protected virtual string ServerKeyOf(TJob job) => GetSessionConfig(job)?.ServerKey ?? "";

    /// <summary>
    /// Limite di sessioni per il job: il server principale usa quello condiviso con il browser
    /// della tab SFTP, ogni altro server ne ha uno suo (non si tolgono posti a vicenda).
    /// </summary>
    protected virtual SftpSessionLimiter LimiterFor(TJob job)
    {
        var key = ServerKeyOf(job);
        if (_currentConfig == null || string.Equals(key, _currentConfig.ServerKey, StringComparison.OrdinalIgnoreCase))
            return _sessionLimiter;
        lock (_otherLimiters)
        {
            if (!_otherLimiters.TryGetValue(key, out var limiter))
                _otherLimiters[key] = limiter = new SftpSessionLimiter(8);
            return limiter;
        }
    }

    /// <summary>
    /// La coda può partire. Ogni job porta il suo server (o usa quello configurato): i job senza
    /// nessuno dei due restano in attesa, gli altri partono anche senza connessione principale.
    /// </summary>
    protected virtual bool IsConfigured => true;

    /// <summary>
    /// true: l'avanzamento arriva dagli eventi di WinSCP della sessione. false: lo
    /// aggiorna il figlio (es. copia tra server in streaming, dove WinSCP non lo conosce).
    /// </summary>
    protected virtual bool UsesSessionProgress => true;

    protected readonly SftpSessionLimiter _sessionLimiter;

    protected SftpQueueServiceBase(ILogger logger, SftpSessionLimiter sessionLimiter)
    {
        _logger = logger;
        _sessionLimiter = sessionLimiter;
    }

    // ---------- Stato condiviso ----------

    public ObservableCollection<TJob> AllJobs { get; } = new();

    protected SftpConfig? CurrentConfig => _currentConfig;

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            if (value < 1) value = 1;
            if (value > 24) value = 24;
            _maxConcurrency = value;
            _logger.LogInformation("Max concurrency {Label}: {Value}", ConcurrencyLogLabel, value);
            TryDispatch();
        }
    }

    public int ActiveCount { get { lock (_queueLock) return _active.Count; } }
    public int PendingCount { get { lock (_queueLock) return _pending.Count; } }
    public bool IsPaused => _isPaused;

    public event Action? QueueChanged;

    /// <summary>Sollevato quando un job termina con successo.</summary>
    public event Action<TJob>? JobCompleted;

    /// <summary>Imposta la config SFTP. I figli possono override per pulire cache ausiliarie.</summary>
    public virtual void Configure(SftpConfig config)
    {
        _currentConfig = config;
    }

    // ============================================================
    //  ENQUEUE
    // ============================================================

    public async Task EnqueueJobAsync(TJob job)
    {
        if (job.Id == 0)
            job.Id = _nextJobId++;

        await Dispatcher.UIThread.InvokeAsync(() => AllJobs.Add(job));

        lock (_queueLock)
        {
            if (_pendingIds.Add(job.Id))
                _pending.Add(job);
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
                        _pending.Add(job);
                }
            }
        }

        QueueChanged?.Invoke();
        TryDispatch();
    }

    public void PauseAll()
    {
        _isPaused = true;

        List<TJob> toPause;
        lock (_queueLock)
        {
            toPause = _active.Keys
                .Select(id => AllJobs.FirstOrDefault(j => j.Id == id))
                .Where(j => j != null)
                .Cast<TJob>()
                .ToList();

            foreach (var job in _pending)
            {
                _pendingIds.Remove(job.Id);
                if (job.Status == SftpJobStatus.Pending)
                    job.Status = SftpJobStatus.Paused;
            }
            _pending.Clear();
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

    /// <summary>Marca come Cancelled anche Pending, Paused e Failed.</summary>
    public void CancelAll()
    {
        List<int> activeIds;
        List<TJob> toCancel;

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

    /// <summary>Ferma tutto e svuota la lista (i file già trasferiti restano dove sono).</summary>
    public void RemoveAll()
    {
        CancelAll();
        Dispatcher.UIThread.Post(() =>
        {
            AllJobs.Clear();
            QueueChanged?.Invoke();
        });
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

    public void PauseJob(TJob job)
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

    public void ResumeJob(TJob job)
    {
        if (job.Status == SftpJobStatus.Completed || job.Status == ActiveStatus)
            return;

        job.Status = SftpJobStatus.Pending;
        job.PauseRequested = false;
        job.ErrorMessage = string.Empty;

        lock (_queueLock)
        {
            if (_pendingIds.Add(job.Id))
                _pending.Add(job);
        }

        _isPaused = false;
        QueueChanged?.Invoke();
        TryDispatch();
    }

    public void CancelJob(TJob job)
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

    public void RemoveJob(TJob job)
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
                    TJob? job = null;
                    CancellationTokenSource? cts = null;

                    lock (_queueLock)
                    {
                        if (!_isRunning) return;
                        if (_isPaused) return;
                        if (!IsConfigured) return;

                        // Primo job in attesa il cui server ha ancora posti (MaxConcurrency per server)
                        int index = -1;
                        string key = "";
                        for (int i = 0; i < _pending.Count; i++)
                        {
                            var candidate = _pending[i];
                            if (candidate.Status is SftpJobStatus.Cancelled or SftpJobStatus.Completed or SftpJobStatus.Paused)
                            { index = i; break; }   // si toglie dalla lista qui sotto
                            if (GetSessionConfig(candidate) == null) continue;
                            var k = ServerKeyOf(candidate);
                            if (_activeServers.Values.Count(v => v == k) >= _maxConcurrency) continue;
                            index = i; key = k; break;
                        }
                        if (index < 0) return;
                        job = _pending[index];
                        _pending.RemoveAt(index);

                        _pendingIds.Remove(job.Id);

                        if (job.Status == SftpJobStatus.Cancelled ||
                            job.Status == SftpJobStatus.Completed ||
                            job.Status == SftpJobStatus.Paused)
                        {
                            continue;
                        }

                        cts = new CancellationTokenSource();
                        _active[job.Id] = cts;
                        _activeServers[job.Id] = key;
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
    //  RUN JOB (boilerplate comune)
    // ============================================================

    private async Task RunJobAsync(TJob job, CancellationTokenSource cts)
    {
        Session? session = null;
        bool holdsSlot = false;
        var limiter = LimiterFor(job);

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => job.Status = ActiveStatus);
            QueueChanged?.Invoke();

            session = new Session();

            long lastUiUpdate = 0;

            void OnFileTransferProgress(object? sender, FileTransferProgressEventArgs e)
            {
                // WinSCP a volte dà 0-1, a volte 0-100. Gestiamo entrambi.
                var val = e.FileProgress;
                double percent = val > 1.0 ? val : val * 100.0;
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;

                var bytes = (long)(job.TotalBytes * (percent / 100.0));
                var speed = e.CPS;

                var now = Environment.TickCount64;
                if (now - lastUiUpdate < 100) return;
                lastUiUpdate = now;

                Dispatcher.UIThread.Post(() =>
                {
                    SetProgressBytes(job, bytes);
                    job.SpeedBytesPerSecond = speed;
                });
            }

            if (UsesSessionProgress) session.FileTransferProgress += OnFileTransferProgress;

            lock (_queueLock) { _activeSessions[job.Id] = session; }

            try
            {
                // Acquisisci slot PRIMA di aprire la sessione
                await limiter.AcquireAsync(cts.Token);
                holdsSlot = true;

                var config = GetSessionConfig(job)
                             ?? throw new InvalidOperationException("Nessuna configurazione SFTP impostata.");
                var options = CreateSessionOptions(config);
                var opened = await TryOpenWithRetryAsync(session, options, cts.Token);
                if (!opened)
                    throw new InvalidOperationException("Impossibile connettersi al server dopo diversi tentativi.");

                await ExecuteTransferAsync(session, job, cts.Token);

                if (cts.IsCancellationRequested)
                {
                    if (job.PauseRequested)
                        await Dispatcher.UIThread.InvokeAsync(() => job.Status = SftpJobStatus.Paused);
                    else
                        await Dispatcher.UIThread.InvokeAsync(() => job.Status = SftpJobStatus.Cancelled);
                    return;
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    SetCompletedBytes(job);
                    job.Status = SftpJobStatus.Completed;
                    job.SpeedBytesPerSecond = 0;
                });

                JobCompleted?.Invoke(job);
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

            _logger.LogError(ex, "{Category} fallito: {File}", LogCategory, GetJobLogDescription(job));
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

            // Rilascia lo slot DOPO aver chiuso la sessione
            if (holdsSlot)
            {
                limiter.Release();
                holdsSlot = false;
            }

            job.PauseRequested = false;

            lock (_queueLock) { _active.Remove(job.Id); _activeServers.Remove(job.Id); }

            QueueChanged?.Invoke();
            TryDispatch();
        }
    }

    // ============================================================
    //  RETRY / SESSION HELPERS (condivisi)
    // ============================================================

    protected async Task<bool> TryOpenWithRetryAsync(
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

    protected static SessionOptions CreateSessionOptions(SftpConfig config)
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
}