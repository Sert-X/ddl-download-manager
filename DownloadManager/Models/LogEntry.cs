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
    public string LevelIcon => Level switch
    {
        LogLevel.Warning => "⚠",
        LogLevel.Error => "❌",
        _ => "ℹ"
    };
}