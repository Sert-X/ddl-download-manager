using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

/// <summary>
/// Base comune per i job di trasferimento SFTP (upload/download).
/// Contiene: identità, stato, progresso, velocità, ETA, formattazione.
/// Le differenze (nome proprietà di progress, cartelle, stato attivo)
/// sono astratte e implementate dai figli.
/// </summary>
public abstract partial class TransferJobBase : ObservableObject
{
    public int Id { get; set; }
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;

    /// <summary>
    /// Server del trasferimento (scheda del pannello remoto da cui è partito). null = il server
    /// configurato nella coda (connessione principale).
    /// </summary>
    public SftpConfig? Server { get; set; }

    /// <summary>Nome del server, per la colonna "Server" delle code.</summary>
    public string ServerName => Server?.Name ?? "";

    [ObservableProperty] private long _totalBytes;
    [ObservableProperty] private SftpJobStatus _status = SftpJobStatus.Pending;
    [ObservableProperty] private double _speedBytesPerSecond;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public CancellationTokenSource? CancellationTokenSource { get; set; }
    public bool PauseRequested { get; set; }

    /// <summary>Byte trasferiti finora. Implementato dai figli come alias
    /// di UploadedBytes/DownloadedBytes per preservare i binding XAML.</summary>
    protected abstract long ProgressBytes { get; }

    /// <summary>Stato "in corso" di questo job (Uploading o Downloading),
    /// usato per decidere se mostrare l'ETA.</summary>
    protected abstract SftpJobStatus ActiveStatus { get; }

    public double Percentage => TotalBytes > 0
        ? Math.Min(100, (double)ProgressBytes / TotalBytes * 100)
        : 0;

    public string SpeedText => SpeedBytesPerSecond > 0
        ? $"{SpeedBytesPerSecond / 1024 / 1024:F2} MB/s"
        : "-";

    public string SizeText => TotalBytes > 0
        ? $"{SftpRemoteEntry.FormatSize(ProgressBytes)} / {SftpRemoteEntry.FormatSize(TotalBytes)}"
        : "-";

    public string EtaText
    {
        get
        {
            if (Status != ActiveStatus || SpeedBytesPerSecond <= 0) return "-";

            var remaining = TotalBytes - ProgressBytes;
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

    /// <summary>Estrae la cartella genitore da un path remoto POSIX-style.</summary>
    public static string GetRemoteParent(string remotePath)
    {
        if (string.IsNullOrEmpty(remotePath)) return "/";
        var trimmed = remotePath.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed.Substring(0, lastSlash);
    }

    /// <summary>Notifica le property derivate dal progress. Da chiamare
    /// nei figli dentro OnUploadedBytesChanged / OnDownloadedBytesChanged.</summary>
    protected void NotifyProgressDependents()
    {
        OnPropertyChanged(nameof(Percentage));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(EtaText));
    }

    partial void OnTotalBytesChanged(long value) => NotifyProgressDependents();

    partial void OnSpeedBytesPerSecondChanged(double value)
    {
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
    }

    // Pulsanti della riga (come nelle code Anime e Film/Serie)
    public bool IsActive => Status is SftpJobStatus.Uploading or SftpJobStatus.Downloading or SftpJobStatus.Transferring;
    public bool CanResume => Status == SftpJobStatus.Paused;
    public bool CanPause => Status == SftpJobStatus.Pending || IsActive;
    public bool CanCancel => Status is not (SftpJobStatus.Completed or SftpJobStatus.Cancelled);
    public bool CanRetry => Status is SftpJobStatus.Failed or SftpJobStatus.Cancelled;

    partial void OnStatusChanged(SftpJobStatus value)
    {
        NotifyProgressDependents();
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
    }
}