using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class SftpDownloadJob : ObservableObject
{
    public int Id { get; set; }
    public string RemotePath { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;

    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private SftpJobStatus _status = SftpJobStatus.Pending;
    [ObservableProperty] private long _downloadedBytes;
    [ObservableProperty] private double _speedBytesPerSecond;
    [ObservableProperty] private string _errorMessage = string.Empty;

    private DateTime _startTime = DateTime.UtcNow;

    public CancellationTokenSource? CancellationTokenSource { get; set; }
    public bool PauseRequested { get; set; }

    public string FileName => Path.GetFileName(RemotePath);

    /// <summary>Cartella remota da cui viene scaricato il file.</summary>
    public string SourceFolder => GetRemoteParent(RemotePath);

    /// <summary>Cartella locale di destinazione.</summary>
    public string DestinationFolder => Path.GetDirectoryName(LocalPath) ?? "";

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
            if (Status != SftpJobStatus.Downloading || SpeedBytesPerSecond <= 0) return "-";

            var remaining = TotalBytes - DownloadedBytes;
            if (remaining <= 0) return "0s";

            var seconds = remaining / SpeedBytesPerSecond;
            return FormatEta(seconds);
        }
    }

    private static string GetRemoteParent(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) return "/";
        var trimmed = remotePath.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed.Substring(0, lastSlash);
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

    partial void OnStatusChanged(SftpJobStatus value)
    {
        OnPropertyChanged(nameof(Percentage));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(EtaText));
    }
}