namespace DownloadManager.Models;

public class VideoQuality
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public long Bandwidth { get; set; }
    /// <summary>AVERAGE-BANDWIDTH della playlist (più vicina al reale di BANDWIDTH, che è il picco). 0 se assente.</summary>
    public long AverageBandwidth { get; set; }

    public string DisplayName =>
        Bandwidth > 1_000_000 && Bandwidth < long.MaxValue / 2
            ? $"{Name} ({Bandwidth / 1_000_000.0:F1} Mbps)"
            : Name;
}