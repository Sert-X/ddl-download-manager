namespace DownloadManager.Models;

/// <summary>
/// Stato del download di un file.
/// </summary>
public class DownloadProgress
{
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double Percentage => TotalBytes > 0 ? (double)DownloadedBytes / TotalBytes * 100 : 0;
    public double SpeedBytesPerSecond { get; set; }
    public TimeSpan Elapsed { get; set; }
    public TimeSpan Eta { get; set; }
    public string Status { get; set; } = "pending"; // pending, downloading, completed, failed, cancelled
    public string FileName { get; set; } = string.Empty;
}