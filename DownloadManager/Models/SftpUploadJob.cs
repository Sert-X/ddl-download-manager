using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public enum SftpJobStatus
{
    Pending,
    Uploading,
    Downloading,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public partial class SftpUploadJob : ObservableObject
{
    public int Id { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;

    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private SftpJobStatus _status = SftpJobStatus.Pending;
    [ObservableProperty] private long _uploadedBytes;
    [ObservableProperty] private double _speedBytesPerSecond;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public CancellationTokenSource? CancellationTokenSource { get; set; }
    public bool PauseRequested { get; set; }

    public string FileName => Path.GetFileName(LocalPath);

    /// <summary>Cartella locale da cui viene caricato il file.</summary>
    public string SourceFolder => Path.GetDirectoryName(LocalPath) ?? "";

    /// <summary>Cartella remota di destinazione.</summary>
    public string DestinationFolder => GetRemoteParent(RemotePath);

    public double Percentage => TotalBytes > 0
        ? Math.Min(100, (double)UploadedBytes / TotalBytes * 100)
        : 0;

    public string SpeedText => SpeedBytesPerSecond > 0
        ? $"{SpeedBytesPerSecond / 1024 / 1024:F2} MB/s"
        : "-";

    public string SizeText => TotalBytes > 0
        ? $"{SftpRemoteEntry.FormatSize(UploadedBytes)} / {SftpRemoteEntry.FormatSize(TotalBytes)}"
        : "-";

    public string EtaText
    {
        get
        {
            if (Status != SftpJobStatus.Uploading || SpeedBytesPerSecond <= 0) return "-";

            var remaining = TotalBytes - UploadedBytes;
            if (remaining <= 0) return "0s";

            var seconds = remaining / SpeedBytesPerSecond;
            return SftpDownloadJob.FormatEta(seconds);
        }
    }

    private static string GetRemoteParent(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) return "/";
        var trimmed = remotePath.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed.Substring(0, lastSlash);
    }

    partial void OnUploadedBytesChanged(long value)
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