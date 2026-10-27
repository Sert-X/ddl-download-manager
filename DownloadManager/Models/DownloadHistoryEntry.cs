namespace DownloadManager.Models;

/// <summary>Un download completato, nella cronologia (resta anche dopo aver pulito la coda).</summary>
public class DownloadHistoryEntry
{
    public long Id { get; set; }
    public string Category { get; set; } = "anime";
    public string SeriesName { get; set; } = "";
    public long EpisodeNumber { get; set; }
    public long SeasonNumber { get; set; }
    public string FilePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public string CompletedAt { get; set; } = "";

    public string FileName => Path.GetFileName(FilePath);
    public string Folder => Path.GetDirectoryName(FilePath) ?? "";
    public string CategoryText => Category == "anime" ? "Anime" : "Film/Serie";
    public string EpisodeText => EpisodeNumber > 0
        ? (SeasonNumber > 0 ? $"S{SeasonNumber:D2}E{EpisodeNumber:D2}" : EpisodeNumber.ToString())
        : "";
    public string SizeText => SizeBytes <= 0 ? "-"
        : SizeBytes >= 1L << 30 ? $"{SizeBytes / (double)(1L << 30):F2} GB" : $"{SizeBytes / (double)(1L << 20):F0} MB";
    public string DateText => DateTime.TryParse(CompletedAt, out var d) ? d.ToString("dd/MM/yyyy HH:mm") : CompletedAt;
    public bool FileExists => File.Exists(FilePath);
}
