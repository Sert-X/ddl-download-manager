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

    public int TotalCount => Items.Count;
    public int CompletedCount => Items.Count(i => i.Status == DownloadStatus.Completed);
    public int ActiveCount => Items.Count(i => i.Status == DownloadStatus.Downloading);
    public int PendingCount => Items.Count(i =>
        i.Status == DownloadStatus.Pending ||
        i.Status == DownloadStatus.Paused);
    public int FailedCount => Items.Count(i => i.Status == DownloadStatus.Failed);

    public double OverallPercentage
    {
        get
        {
            if (TotalCount == 0) return 0;
            long totalBytes = Items.Sum(i => i.TotalBytes);
            long downloadedBytes = Items.Sum(i => i.DownloadedBytes);
            return totalBytes > 0 ? (double)downloadedBytes / totalBytes * 100 : 0;
        }
    }

    public long TotalBytes => Items.Sum(i => i.TotalBytes);
    public long TotalDownloadedBytes => Items.Sum(i => i.DownloadedBytes);

    public double TotalSpeedBytesPerSecond => Items.Sum(i => i.SpeedBytesPerSecond);

    public string TotalSpeedText => TotalSpeedBytesPerSecond > 0
        ? $"{TotalSpeedBytesPerSecond / 1024 / 1024:F2} MB/s"
        : "-";

    public string TotalSizeText => TotalBytes > 0
        ? $"{TotalDownloadedBytes / 1024 / 1024 / 1024:F2} / {TotalBytes / 1024 / 1024 / 1024:F2} GB"
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

    public string Header =>
        $"{SeriesName} — {CompletedCount}/{TotalCount} completati, {ActiveCount} attivi, {PendingCount} in attesa";

    public void RefreshStats()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(OverallPercentage));
        OnPropertyChanged(nameof(TotalSpeedText));
        OnPropertyChanged(nameof(TotalSizeText));
        OnPropertyChanged(nameof(OverallEtaText));
        OnPropertyChanged(nameof(Header));
    }
}