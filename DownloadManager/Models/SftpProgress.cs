namespace DownloadManager.Models;

public class SftpProgress
{
    public string CurrentFile { get; set; } = string.Empty;
    public int FilesCompleted { get; set; }
    public int FilesTotal { get; set; }
    public long TotalBytes { get; set; }
    public long UploadedBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public string Status { get; set; } = "pending";

    public double OverallPercentage => TotalBytes > 0
        ? Math.Min(100, (double)UploadedBytes / TotalBytes * 100)
        : 0;

    public string SpeedText => SpeedBytesPerSecond > 0
        ? $"{SpeedBytesPerSecond / 1024 / 1024:F2} MB/s"
        : "-";

    public string SizeText => TotalBytes > 0
        ? $"{UploadedBytes / 1024 / 1024} / {TotalBytes / 1024 / 1024} MB"
        : "-";
}