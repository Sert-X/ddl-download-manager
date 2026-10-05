namespace DownloadManager.Services.Sftp;

/// <summary>
/// Limita il numero totale di sessioni SFTP aperte contemporaneamente
/// verso lo stesso server (browser + coda upload + coda download).
/// Ogni componente deve chiamare AcquireAsync prima di aprire una Session
/// e Release nel finally (o alla disconnessione).
/// </summary>
public sealed class SftpSessionLimiter : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private int _activeCount;

    public int MaxSessions { get; }
    public int ActiveCount => Volatile.Read(ref _activeCount);
    public int AvailableCount => MaxSessions - ActiveCount;

    public SftpSessionLimiter(int maxSessions = 8)
    {
        if (maxSessions < 1) maxSessions = 1;
        MaxSessions = maxSessions;
        _gate = new SemaphoreSlim(maxSessions, maxSessions);
    }

    /// <summary>Attende uno slot libero. Va SEMPRE seguito da <see cref="Release"/>.</summary>
    public Task AcquireAsync(CancellationToken ct = default) => AcquireInternalAsync(ct);

    private async Task AcquireInternalAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        Interlocked.Increment(ref _activeCount);
    }

    /// <summary>Rilascia uno slot precedentemente acquisito.</summary>
    public void Release()
    {
        Interlocked.Decrement(ref _activeCount);
        _gate.Release();
    }

    public void Dispose() => _gate.Dispose();
}