using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class DownloadItem : ObservableObject
{
    public int Id { get; set; }
    public string Url { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public string SeriesName { get; set; } = string.Empty;
    public int EpisodeNumber { get; set; }
    public int SeasonNumber { get; set; }
    public string EpisodeTitle { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;

    public string Category { get; set; } = "anime";
    public int ProxyRetryCount { get; set; }
    public string AudioUrl { get; set; } = string.Empty;

    public string ProviderKey { get; set; } = "";
    public string QualityName { get; set; } = "";
    public string SessionCookies { get; set; } = "";

    // === NUOVI: necessari per ricostruire gli extractor al restore ===
    public string ContentLink { get; set; } = "";  // es. https://streamingcommunityz.rip/it/titles/123-slug
    public int EpisodeId { get; set; }              // ID episodio streamingcommunity (0 = film)
    public bool IsSeries { get; set; }              // true se è una serie

    // === Tracce scelte nell'analisi (Film/Serie) ===
    /// <summary>Lingue audio da includere (codice LANGUAGE della playlist o nome traccia), separate da '|'. Vuoto = automatico (italiano).</summary>
    public string AudioLanguages { get; set; } = "";
    /// <summary>Lingue dei sottotitoli da includere, separate da '|'. Vuoto = nessuno.</summary>
    public string SubtitleLanguages { get; set; } = "";
    /// <summary>Se true il primo sottotitolo scelto viene impresso nel video (richiede ricodifica).</summary>
    public bool BurnSubtitles { get; set; }

    public Func<CancellationToken, Task<string?>>? UrlExtractor { get; set; }
    public Func<CancellationToken, Task<string?>>? AudioExtractor { get; set; }
    public Func<CancellationToken, Task<string?>>? CookieExtractor { get; set; }

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

    public bool CanResume => Status == DownloadStatus.Paused;
    public bool CanPause => Status == DownloadStatus.Downloading || Status == DownloadStatus.Pending;
    public bool CanCancel => Status != DownloadStatus.Completed && Status != DownloadStatus.Cancelled;
    public bool CanRetry => Status == DownloadStatus.Failed || Status == DownloadStatus.Cancelled;
    public bool CanForce => Status != DownloadStatus.Completed && !IsPriority;

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
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(CanForce));
    }

    partial void OnIsPriorityChanged(bool value)
    {
        OnPropertyChanged(nameof(CanForce));
    }
}