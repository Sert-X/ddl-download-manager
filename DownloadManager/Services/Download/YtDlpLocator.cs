using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Download;

/// <summary>
/// Risolve il percorso di yt-dlp.exe. Priorità:
///   1. %LOCALAPPDATA%\DDLDownloadManager\yt-dlp\yt-dlp.exe   (bundle copiato)
///   2. <exeDir>\yt-dlp\yt-dlp.exe                            (dev/publish)
///   3. <exeDir>\Assets\yt-dlp\win-x64\yt-dlp.exe             (dev locale)
/// </summary>
public class YtDlpLocator
{
    private readonly ILogger<YtDlpLocator> _logger;
    private string? _cachedPath;

    public YtDlpLocator(ILogger<YtDlpLocator> logger)
    {
        _logger = logger;
    }

    public string? GetYtDlpPath()
    {
        if (_cachedPath != null && File.Exists(_cachedPath)) return _cachedPath;

        // 1) %LOCALAPPDATA% (bundle copiato)
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DDLDownloadManager",
                "yt-dlp");
            var appDataPath = Path.Combine(appData, "yt-dlp.exe");
            if (File.Exists(appDataPath))
            {
                _cachedPath = appDataPath;
                _logger.LogInformation("[YT-DLP] Trovato (appdata): {Path}", appDataPath);
                return _cachedPath;
            }
        }
        catch { }

        // 2) Accanto all'eseguibile pubblicato
        var exeDir = AppContext.BaseDirectory;
        var bundled = Path.Combine(exeDir, "yt-dlp", "yt-dlp.exe");
        if (File.Exists(bundled))
        {
            _cachedPath = bundled;
            _logger.LogInformation("[YT-DLP] Trovato (bundle): {Path}", bundled);
            return _cachedPath;
        }

        // 3) Sviluppo: <exeDir>\Assets\yt-dlp\win-x64\
        var dev = Path.Combine(exeDir, "Assets", "yt-dlp", "win-x64", "yt-dlp.exe");
        if (File.Exists(dev))
        {
            _cachedPath = dev;
            _logger.LogInformation("[YT-DLP] Trovato (dev): {Path}", dev);
            return _cachedPath;
        }

        _logger.LogWarning("[YT-DLP] yt-dlp.exe non trovato in nessun percorso");
        return null;
    }
}