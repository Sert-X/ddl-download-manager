using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Download;

/// <summary>
/// Risolve il percorso di ffmpeg.exe. Priorità:
///   1. %LOCALAPPDATA%\DDLDownloadManager\ffmpeg\ffmpeg.exe  (bundle copiato)
///   2. <exeDir>\ffmpeg\ffmpeg.exe                           (dev/publish)
///   3. PATH di sistema
/// </summary>
public class FfmpegLocator
{
    private readonly ILogger<FfmpegLocator> _logger;
    private string? _cachedPath;

    public FfmpegLocator(ILogger<FfmpegLocator> logger)
    {
        _logger = logger;
    }

    public string? GetFfmpegPath()
    {
        if (_cachedPath != null && File.Exists(_cachedPath)) return _cachedPath;

        // 1. %LOCALAPPDATA% (bundle copiato al primo avvio)
        try
        {
            var appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DDLDownloadManager",
                "ffmpeg");
            var appDataFfmpeg = Path.Combine(appData, "ffmpeg.exe");
            if (File.Exists(appDataFfmpeg))
            {
                _cachedPath = appDataFfmpeg;
                return _cachedPath;
            }
        }
        catch { }

        // 2. Accanto all'eseguibile
        var exeDir = AppContext.BaseDirectory;
        var devFfmpeg = Path.Combine(exeDir, "ffmpeg", "ffmpeg.exe");
        if (File.Exists(devFfmpeg))
        {
            _cachedPath = devFfmpeg;
            return _cachedPath;
        }

        // 3. PATH di sistema
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    _cachedPath = candidate;
                    _logger.LogInformation("[FFMPEG] Trovato nel PATH: {Path}", candidate);
                    return _cachedPath;
                }
            }
            catch { }
        }

        _logger.LogWarning("[FFMPEG] ffmpeg.exe non trovato in nessun percorso");
        return null;
    }

    /// <summary>
    /// Copia ffmpeg.exe dal bundle in %LOCALAPPDATA% al primo avvio (o quando
    /// il bundle ha una versione più recente). Va chiamato una volta in App.axaml.cs.
    /// </summary>
    public static void EnsureBundledFfmpeg(ILogger logger)
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            var bundledPath = Path.Combine(exeDir, "ffmpeg", "ffmpeg.exe");

            if (!File.Exists(bundledPath))
            {
                logger.LogInformation("[FFMPEG] Nessun ffmpeg nel bundle, skip copia");
                return;
            }

            var appDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DDLDownloadManager",
                "ffmpeg");
            Directory.CreateDirectory(appDataDir);

            var destPath = Path.Combine(appDataDir, "ffmpeg.exe");

            bool needsCopy = !File.Exists(destPath);
            if (!needsCopy)
            {
                var src = new FileInfo(bundledPath);
                var dst = new FileInfo(destPath);
                if (src.Length != dst.Length || src.LastWriteTimeUtc > dst.LastWriteTimeUtc)
                    needsCopy = true;
            }

            if (needsCopy)
            {
                logger.LogInformation("[FFMPEG] Copia ffmpeg.exe in {Path}", destPath);
                File.Copy(bundledPath, destPath, overwrite: true);
            }
            // Copia anche ffprobe.exe
            var bundledFfprobe = Path.Combine(exeDir, "ffmpeg", "ffprobe.exe");
            if (File.Exists(bundledFfprobe))
            {
                var destFfprobe = Path.Combine(appDataDir, "ffprobe.exe");
                bool copyProbe = !File.Exists(destFfprobe);
                if (!copyProbe)
                {
                    var srcP = new FileInfo(bundledFfprobe);
                    var dstP = new FileInfo(destFfprobe);
                    if (srcP.Length != dstP.Length || srcP.LastWriteTimeUtc > dstP.LastWriteTimeUtc)
                        copyProbe = true;
                }
                if (copyProbe)
                {
                    logger.LogInformation("[FFMPEG] Copia ffprobe.exe in {Path}", destFfprobe);
                    File.Copy(bundledFfprobe, destFfprobe, overwrite: true);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[FFMPEG] Errore durante la copia del bundle");
        }
    }
}