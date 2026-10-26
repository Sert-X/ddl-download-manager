namespace DownloadManager.Models;

public enum LogLevel
{
    Info,
    Warning,
    Error
}

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; } = LogLevel.Info;
    public string Category { get; set; } = "Generale";
    public string Message { get; set; } = string.Empty;

    public bool IsError => Level == LogLevel.Error;
    public bool IsWarning => Level == LogLevel.Warning;

    public string TimestampText => Timestamp.ToString("HH:mm:ss");
    public bool IsInfo => !IsError && !IsWarning;

    /// <summary>Livello in testo, per copia ed esportazione del log.</summary>
    public string LevelText => Level switch
    {
        LogLevel.Warning => "AVVISO",
        LogLevel.Error => "ERRORE",
        _ => "INFO"
    };
}