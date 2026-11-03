namespace DownloadManager.Services.Download;

/// <summary>
/// Coda delle conversioni: una alla volta, in ordine d'arrivo, ma gli episodi forzati (fulmine)
/// passano davanti a quelli in attesa. La priorità si guarda al momento di scegliere il prossimo:
/// premere il fulmine su un episodio già in coda di conversione lo porta in cima. Quella in corso
/// non viene interrotta.
/// </summary>
public static class ConversionQueue
{
    private sealed class Waiter
    {
        public required TaskCompletionSource Ready { get; init; }
        public required Func<bool> IsPriority { get; init; }
        public required long Order { get; init; }
    }

    private static readonly object Lock = new();
    private static readonly List<Waiter> Waiting = new();
    private static bool _busy;
    private static long _nextOrder;

    /// <summary>Aspetta il proprio turno. Va sempre seguito da <see cref="Release"/>.</summary>
    /// <param name="isPriority">Letto ogni volta che si sceglie il prossimo (il fulmine può arrivare dopo).</param>
    /// <param name="order">Ordine nella lista di download: chi ha il numero più basso passa prima (a parità di priorità).</param>
    public static async Task EnterAsync(Func<bool> isPriority, CancellationToken ct, long order = 0)
    {
        Waiter waiter;
        lock (Lock)
        {
            if (!_busy)
            {
                _busy = true;
                return;
            }
            waiter = new Waiter
            {
                Ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
                IsPriority = isPriority,
                Order = order != 0 ? order : _nextOrder++
            };
            Waiting.Add(waiter);
        }

        using var reg = ct.Register(() =>
        {
            bool removed;
            lock (Lock) removed = Waiting.Remove(waiter);
            if (removed) waiter.Ready.TrySetCanceled(ct);
        });
        await waiter.Ready.Task;
    }

    /// <summary>Fine della conversione: parte la prossima (prima i forzati, poi in ordine d'arrivo).</summary>
    public static void Release()
    {
        Waiter? next;
        lock (Lock)
        {
            next = Waiting
                .OrderByDescending(w => SafePriority(w))
                .ThenBy(w => w.Order)
                .FirstOrDefault();
            if (next == null)
            {
                _busy = false;
                return;
            }
            Waiting.Remove(next);
        }
        next.Ready.TrySetResult();
    }

    private static bool SafePriority(Waiter w)
    {
        try { return w.IsPriority(); } catch { return false; }
    }
}
