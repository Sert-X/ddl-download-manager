using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class SeriesGroup : ObservableObject
{
    [ObservableProperty]
    private string _seriesName = string.Empty;

    [ObservableProperty]
    private bool _isExpanded = false;

    public ObservableCollection<DownloadItem> Items { get; } = new();

    /// <summary>
    /// Gli item solo a gruppo espanso. La lista nella UI si lega a questa: così i
    /// controlli delle righe vengono creati solo quando servono, e accodare centinaia
    /// di episodi in un gruppo chiuso non costa nulla alla UI.
    /// </summary>
    public ObservableCollection<DownloadItem>? ExpandedItems => IsExpanded ? Items : null;

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ExpandedItems));

    // Totali calcolati in un solo passaggio sugli episodi e tenuti finché qualcosa non cambia.
    // Prima ogni proprietà rileggeva tutti gli episodi (una quindicina di passaggi a ogni cambio
    // di stato): con serie lunghe e "Riscarica" su centinaia di episodi l'app quasi si bloccava.
    private bool _statsDirty = true;
    private int _completed, _active, _pending, _paused, _failed, _cancelled;
    private long _totalBytes, _downloadedBytes;
    private double _speed;

    private void EnsureStats()
    {
        if (!_statsDirty) return;
        _statsDirty = false;
        _completed = _active = _pending = _paused = _failed = _cancelled = 0;
        _totalBytes = _downloadedBytes = 0;
        _speed = 0;
        foreach (var i in Items)
        {
            switch (i.Status)
            {
                case DownloadStatus.Completed: _completed++; break;
                case DownloadStatus.Downloading: _active++; break;
                case DownloadStatus.Pending: _pending++; break;
                case DownloadStatus.Paused: _paused++; break;
                case DownloadStatus.Failed: _failed++; break;
                case DownloadStatus.Cancelled: _cancelled++; break;
            }
            _totalBytes += i.TotalBytes;
            _downloadedBytes += i.DownloadedBytes;
            _speed += i.SpeedBytesPerSecond;
        }
    }

    public SeriesGroup()
    {
        Items.CollectionChanged += (_, _) => _statsDirty = true;
    }

    public int TotalCount => Items.Count;
    public int CompletedCount { get { EnsureStats(); return _completed; } }
    public int ActiveCount { get { EnsureStats(); return _active; } }
    public int PendingCount { get { EnsureStats(); return _pending; } }
    public int PausedCount { get { EnsureStats(); return _paused; } }
    public int FailedCount { get { EnsureStats(); return _failed; } }
    public int CancelledCount { get { EnsureStats(); return _cancelled; } }
    public bool HasFailed => FailedCount > 0;
    public string FailedBadge => FailedCount == 1 ? "1 fallito" : $"{FailedCount} {(FailedCount == 1 ? "fallito" : "falliti")}";
    public bool HasCancelled => CancelledCount > 0;
    public bool IsAllCompleted => TotalCount > 0 && CompletedCount == TotalCount;
    public bool IsAllPaused => PausedCount > 0 && ActiveCount == 0 && PendingCount == 0;

    public double OverallPercentage
    {
        get
        {
            if (TotalCount == 0) return 0;
            EnsureStats();
            return _totalBytes > 0 ? (double)_downloadedBytes / _totalBytes * 100 : 0;
        }
    }

    public long TotalBytes { get { EnsureStats(); return _totalBytes; } }
    public long TotalDownloadedBytes { get { EnsureStats(); return _downloadedBytes; } }

    public double TotalSpeedBytesPerSecond { get { EnsureStats(); return _speed; } }

    public string TotalSpeedText => TotalSpeedBytesPerSecond > 0
        ? $"{TotalSpeedBytesPerSecond / 1024 / 1024:F2} MB/s"
        : "-";

    // Divisione in virgola mobile: con i long "1,8 GB" diventava "1,00"
    public string TotalSizeText => TotalBytes > 0
        ? $"{TotalDownloadedBytes / 1073741824.0:F2} / {TotalBytes / 1073741824.0:F2} GB"
        : "-";

    public string OverallEtaText
    {
        get
        {
            if (TotalSpeedBytesPerSecond <= 0) return "-";

            var remaining = TotalBytes - TotalDownloadedBytes;
            if (remaining <= 0) return "0s";

            var seconds = remaining / TotalSpeedBytesPerSecond;

            if (seconds < 1) return "<1s";
            if (seconds < 60) return $"{(int)seconds}s";
            if (seconds < 3600)
                return $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s";
            return $"{(int)(seconds / 3600)}h {(int)((seconds % 3600) / 60)}m";
        }
    }

    public string Header
    {
        get
        {
            var parts = new List<string>
            {
                $"{CompletedCount}/{TotalCount} completati",
                $"{ActiveCount} attivi"
            };

            if (PendingCount > 0) parts.Add($"{PendingCount} in attesa");
            if (PausedCount > 0) parts.Add($"{PausedCount} in pausa");
            if (CancelledCount > 0) parts.Add($"{CancelledCount} {(CancelledCount == 1 ? "annullato" : "annullati")}");

            return $"{SeriesName} — {string.Join(", ", parts)}";
        }
    }

    private bool _refreshScheduled;

    /// <summary>
    /// Ricalcolo raggruppato: tanti cambi di stato ravvicinati (es. "Riscarica" su centinaia di
    /// episodi) producono un solo aggiornamento della UI invece di uno per episodio.
    /// </summary>
    public void ScheduleRefresh()
    {
        _statsDirty = true;
        if (_refreshScheduled) return;
        _refreshScheduled = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _refreshScheduled = false;
            RefreshStats();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void RefreshStats()
    {
        _statsDirty = true;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PausedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(CancelledCount));
        OnPropertyChanged(nameof(OverallPercentage));
        OnPropertyChanged(nameof(TotalSpeedText));
        OnPropertyChanged(nameof(TotalSizeText));
        OnPropertyChanged(nameof(OverallEtaText));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(HasFailed));
        OnPropertyChanged(nameof(FailedBadge));
        OnPropertyChanged(nameof(HasCancelled));
        OnPropertyChanged(nameof(IsAllCompleted));
        OnPropertyChanged(nameof(IsAllPaused));
    }
}