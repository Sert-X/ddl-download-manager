using System.Collections.ObjectModel;
using Avalonia.Threading;
using DownloadManager.Models;

namespace DownloadManager.Services.Logging;

public class LogService
{
    private const int MaxEntries = 2000;

    public ObservableCollection<LogEntry> Logs { get; } = new();

    public void Add(LogEntry entry)
    {
        if (Dispatcher.UIThread.CheckAccess())
            Append(entry);
        else
            Dispatcher.UIThread.Post(() => Append(entry));
    }

    public void Clear()
    {
        if (Dispatcher.UIThread.CheckAccess())
            Logs.Clear();
        else
            Dispatcher.UIThread.Post(() => Logs.Clear());
    }

    private void Append(LogEntry entry)
    {
        Logs.Add(entry);
        while (Logs.Count > MaxEntries)
            Logs.RemoveAt(0);
    }
}