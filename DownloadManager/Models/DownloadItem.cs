using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class DownloadItem : ObservableObject
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string SeriesName { get; set; } = string.Empty;
    public int EpisodeNumber { get; set; }
    public string EpisodeTitle { get; set; } = string.Empty;

    [ObservableProperty] private bool _isPriority;
    [ObservableProperty] private DownloadStatus _status = DownloadStatus.Pending;
    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private long _downloadedBytes;
    [ObservableProperty] private double _speedBytesPerSecond;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public CancellationTokenSource? CancellationTokenSource { get; set; }
    public bool PauseRequested { get; set; }

    public string FileName => Path.GetFileName(DestinationPath);

    public double Percentage => TotalBytes > 0
        ? Math.Min(100, (double)DownloadedBytes / TotalBytes * 100)
        : 0;

    public string SpeedText => SpeedBytesPerSecond > 0
        ? $"{SpeedBytesPerSecond / 1024 / 1024:F2} MB/s"
        : "-";

    public string SizeText => TotalBytes > 0
        ? $"{SftpRemoteEntry.FormatSize(DownloadedBytes)} / {SftpRemoteEntry.FormatSize(TotalBytes)}"
        : "-";

    public string EtaText
    {
        get
        {
            if (Status != DownloadStatus.Downloading || SpeedBytesPerSecond <= 0) return "-";

            var remaining = TotalBytes - DownloadedBytes;
            if (remaining <= 0) return "0s";

            var seconds = remaining / SpeedBytesPerSecond;
            return FormatEta(seconds);
        }
    }

    public static string FormatEta(double seconds)
    {
        if (seconds < 1) return "<1s";
        if (seconds < 60) return $"{(int)seconds}s";
        if (seconds < 3600)
            return $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s";
        return $"{(int)(seconds / 3600)}h {(int)((seconds % 3600) / 60)}m";
    }

    partial void OnDownloadedBytesChanged(long value)
    {
        OnPropertyChanged(nameof(Percentage));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(EtaText));
    }

    partial void OnTotalBytesChanged(long value)
    {
        OnPropertyChanged(nameof(Percentage));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(EtaText));
    }

    partial void OnSpeedBytesPerSecondChanged(double value)
    {
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
    }

    partial void OnStatusChanged(DownloadStatus value)
    {
        OnPropertyChanged(nameof(Percentage));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(EtaText));
    }
}