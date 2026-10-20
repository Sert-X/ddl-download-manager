using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Helpers;

/// <summary>
/// All'avvio chiude i yt-dlp / ffmpeg rimasti vivi da una sessione precedente terminata
/// male (prima che esistesse <see cref="ChildProcessJob"/>, o dopo un crash).
/// Tocca solo eseguibili che stanno nelle cartelle dell'app: un ffmpeg di sistema che
/// l'utente usa per altro non viene chiuso.
/// </summary>
public static class OrphanProcessCleaner
{
    private static readonly string[] Names = { "yt-dlp", "ffmpeg", "ffprobe" };

    public static void KillOrphans(ILogger logger)
    {
        var ownDirs = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDLDownloadManager"),
            AppContext.BaseDirectory
        }.Select(d => Path.GetFullPath(d).TrimEnd('\\') + "\\").ToArray();

        foreach (var name in Names)
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try
                {
                    var exe = proc.MainModule?.FileName;
                    if (exe == null || !ownDirs.Any(d => exe.StartsWith(d, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    proc.Kill(entireProcessTree: true);
                    logger.LogWarning("Chiuso processo orfano di una sessione precedente: {Exe} (PID {Pid})", exe, proc.Id);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Impossibile controllare il processo {Name} (PID {Pid})", name, proc.Id);
                }
                finally { proc.Dispose(); }
            }
        }
    }
}
