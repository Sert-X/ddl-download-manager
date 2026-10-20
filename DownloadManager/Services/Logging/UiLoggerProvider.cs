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

        /// <summary>
        /// Nome del logger (namespace + classe) → sezione mostrata nel log globale.
        /// L'ordine conta: le voci più specifiche vengono prima.
        /// </summary>
        private static readonly (string Fragment, string Category)[] CategoryMap =
        {
            (".AnimeWorld", "AnimeWorld"),
            (".AnimeSaturn", "AnimeSaturn"),
            (".StreamingCommunity", "Streaming"),
            ("StreamingTab", "Streaming"),
            ("DownloadTab", "Anime"),
            (".Services.Download", "Download"),
            (".FileOrganizer", "Organizza"),
            ("OrganizerTab", "Organizza"),
            ("ZeroByte", "SFTP"),
            (".Sftp", "SFTP"),
            ("SftpTab", "SFTP"),
            (".Proxy", "Proxy"),
            ("ProxyTab", "Proxy"),
            (".Domains", "Domini"),
            ("DomainsTab", "Domini"),
            (".Videobox", "Videobox"),
            ("VideoboxTab", "Videobox"),
            ("AccountTab", "Account"),
            (".Update", "Aggiornamenti"),
            (".Persistence", "Database"),
        };

        private static string MapCategory(string categoryName)
        {
            foreach (var (fragment, category) in CategoryMap)
                if (categoryName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return category;

            return "Generale";
        }
    }
}