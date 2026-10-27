using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using DownloadManager.Models;

namespace DownloadManager.Services.Logging;

public class LogService
{
    private const int MaxEntries = 2000;

    /// <summary>
    /// Le righe nuove si raccolgono e si aggiungono al log tutte insieme al massimo ogni 250 ms.
    /// Con decine di download e upload arrivavano decine di righe al secondo, una alla volta: a
    /// log aperto l'interfaccia ricalcolava e scorreva l'elenco per ognuna (fino al 20% del tempo).
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private int _flushScheduled;

    public ObservableCollection<LogEntry> Logs { get; } = new();

    public void Add(LogEntry entry)
    {
        _pending.Enqueue(entry);
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0)
            Dispatcher.UIThread.Post(async () =>
            {
                await Task.Delay(FlushInterval);
                Flush();
            });
    }

    /// <summary>Aggiunge subito le righe in attesa (es. prima di copiare o esportare il log).</summary>
    public void Flush()
    {
        Volatile.Write(ref _flushScheduled, 0);
        while (_pending.TryDequeue(out var entry))
            Logs.Add(entry);
        while (Logs.Count > MaxEntries)
            Logs.RemoveAt(0);
    }

    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _pending.Clear();
            Logs.Clear();
        }
        else
            Dispatcher.UIThread.Post(() => { _pending.Clear(); Logs.Clear(); });
    }
}
