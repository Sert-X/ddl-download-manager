using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Logging;

public class UiLoggerProvider : ILoggerProvider
{
    private readonly LogService _logService;

    public UiLoggerProvider(LogService logService)
    {
        _logService = logService;
    }

    public ILogger CreateLogger(string categoryName) => new UiLogger(categoryName, _logService);

    public void Dispose() { }

    private class UiLogger : ILogger
    {
        private readonly LogService _logService;
        private readonly string _category;

        public UiLogger(string category, LogService logService)
        {
            _logService = logService;
            _category = MapCategory(category);
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message)) return;

            var entry = new Models.LogEntry
            {
                Timestamp = DateTime.Now,
                Level = MapLevel(logLevel),
                Category = _category,
                Message = exception != null
                    ? $"{message} — {exception.Message}"
                    : message
            };

            _logService.Add(entry);
        }

        private static Models.LogLevel MapLevel(Microsoft.Extensions.Logging.LogLevel level) =>
            level switch
            {
                Microsoft.Extensions.Logging.LogLevel.Warning => Models.LogLevel.Warning,
                Microsoft.Extensions.Logging.LogLevel.Error => Models.LogLevel.Error,
                Microsoft.Extensions.Logging.LogLevel.Critical => Models.LogLevel.Error,
                _ => Models.LogLevel.Info
            };

        private static string MapCategory(string categoryName)
        {
            if (categoryName.Contains(".AnimeWorld", StringComparison.OrdinalIgnoreCase))
                return "Download";

            if (categoryName.Contains(".Services.Download", StringComparison.OrdinalIgnoreCase))
                return "Download";

            if (categoryName.Contains(".FileOrganizer", StringComparison.OrdinalIgnoreCase))
                return "Organizza";

            if (categoryName.Contains(".Sftp", StringComparison.OrdinalIgnoreCase))
                return "SFTP";

            return "General";
        }
    }
}