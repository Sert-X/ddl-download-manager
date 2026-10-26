using System.Diagnostics;
using DownloadManager.Helpers;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Download;

/// <summary>
/// Converte un file video (es. .mkv) in .mp4 senza ricodificare il video: copia le tracce,
/// quindi è veloce e non perde qualità. Se audio o sottotitoli non sono ammessi nel contenitore
/// MP4, riprova con soluzioni via via più semplici.
/// </summary>
public static class Mp4Remuxer
{
    /// <summary>Audio che i lettori MP4 (Windows, TV, telefoni) riproducono: questi si copiano, gli altri diventano AAC.</summary>
    private static readonly HashSet<string> Mp4FriendlyAudio = new(StringComparer.OrdinalIgnoreCase)
        { "aac", "ac3", "eac3", "mp3", "alac" };

    private static readonly string[] AacAudio = { "-c:a", "aac", "-b:a", "384k" };

    /// <summary>Restituisce il percorso del .mp4 creato, oppure null (il file originale resta intatto).</summary>
    public static async Task<string?> ConvertAsync(string ffmpegPath, string source, ILogger logger, CancellationToken ct = default)
    {
        var target = Path.ChangeExtension(source, ".mp4");
        var temp = target + ".converting";
        var (videoCodec, audioCodecs) = await ProbeCodecsAsync(ffmpegPath, source, ct);
        var isHevc = videoCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase);
        var audioMap = AudioTracks.MapArgs(await AudioTracks.ItalianFirstOrderAsync(ffmpegPath, source, ct));

        // DTS, TrueHD, Vorbis, FLAC, PCM...: ffmpeg li mette nell'MP4 ma molti lettori non li riproducono
        bool copyAudio = audioCodecs.All(Mp4FriendlyAudio.Contains);
        var audio = copyAudio ? new[] { "-c:a", "copy" } : AacAudio;
        if (!copyAudio)
            logger.LogInformation("[MP4] Audio {Codecs} non adatto all'MP4: verrà convertito in AAC", string.Join(", ", audioCodecs));

        // In ordine: tutto con sottotitoli testuali (mov_text); senza sottotitoli (PGS/VobSub non
        // convertibili); audio in AAC. "0:V" esclude le copertine allegate, che fanno fallire la copia.
        // L'audio italiano va per primo (vedi AudioTracks).
        var attempts = new (string Name, string[] Args)[]
        {
            ("copia completa", new[] { "-map", "0:V" }.Concat(audioMap).Concat(new[] { "-map", "0:s?", "-c:v", "copy", "-c:s", "mov_text" }).Concat(audio).ToArray()),
            ("senza sottotitoli", new[] { "-map", "0:V" }.Concat(audioMap).Concat(new[] { "-c:v", "copy", "-sn" }).Concat(audio).ToArray()),
            ("audio in AAC", new[] { "-map", "0:V" }.Concat(audioMap).Concat(new[] { "-c:v", "copy", "-sn" }).Concat(AacAudio).ToArray()),
        };

        foreach (var (name, attemptArgs) in attempts)
        {
            ct.ThrowIfCancellationRequested();
            var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", source };
            args.AddRange(attemptArgs);
            // HEVC con tag hvc1: altrimenti il lettore di Windows e i dispositivi Apple non lo aprono
            if (isHevc) args.AddRange(new[] { "-tag:v", "hvc1" });
            args.AddRange(new[] { "-map_metadata", "0", "-movflags", "+faststart", "-f", "mp4", temp });

            var sw = Stopwatch.StartNew();
            var (code, err) = await RunAsync(ffmpegPath, args, ct);
            if (code == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0)
            {
                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
                File.Delete(source);
                logger.LogInformation("[MP4] {File} convertito in MP4 ({Mode}) in {S:F0}s",
                    Path.GetFileName(source), name, sw.Elapsed.TotalSeconds);
                return target;
            }

            TryDelete(temp);
            logger.LogInformation("[MP4] Conversione '{Mode}' non riuscita per {File}: {Err}",
                name, Path.GetFileName(source), LastLines(err));
        }

        logger.LogWarning("[MP4] Impossibile convertire {File} in MP4: resta nel formato originale.", Path.GetFileName(source));
        return null;
    }

    /// <summary>
    /// Riscrive un .mp4 sul posto copiando le tracce (nessuna ricodifica): indice in testa
    /// (faststart), tag hvc1 per l'HEVC e audio italiano per primo. True se riuscito; altrimenti il file resta com'era.
    /// </summary>
    public static async Task<bool> RewriteAsync(string ffmpegPath, string file, ILogger logger, CancellationToken ct = default)
    {
        var temp = file + ".rewrite.mp4";
        var (videoCodec, _) = await ProbeCodecsAsync(ffmpegPath, file, ct);
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", file, "-map", "0:V" };
        args.AddRange(AudioTracks.MapArgs(await AudioTracks.ItalianFirstOrderAsync(ffmpegPath, file, ct)));
        args.AddRange(new[] { "-map", "0:s?", "-c", "copy", "-c:s", "mov_text" });
        if (videoCodec.Equals("hevc", StringComparison.OrdinalIgnoreCase)) args.AddRange(new[] { "-tag:v", "hvc1" });
        args.AddRange(new[] { "-map_metadata", "0", "-movflags", "+faststart", "-f", "mp4", temp });

        var sw = Stopwatch.StartNew();
        var (code, err) = await RunAsync(ffmpegPath, args, ct);
        if (code == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0)
        {
            File.Move(temp, file, overwrite: true);
            logger.LogInformation("[MP4] {File} riscritto in {S:F0}s", Path.GetFileName(file), sw.Elapsed.TotalSeconds);
            return true;
        }
        TryDelete(temp);
        logger.LogInformation("[MP4] Riscrittura non riuscita per {File}: {Err}", Path.GetFileName(file), LastLines(err));
        return false;
    }

    /// <summary>Codec del video principale e di tutte le tracce audio ("" e lista vuota se ffprobe manca).</summary>
    private static async Task<(string Video, List<string> Audio)> ProbeCodecsAsync(string ffmpegPath, string file, CancellationToken ct)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
        if (!File.Exists(ffprobe)) return ("", new List<string>());

        var (code, output) = await RunAsync(ffprobe,
            new[] { "-v", "error", "-show_entries", "stream=codec_type,codec_name:stream_disposition=attached_pic", "-of", "csv=p=0", file },
            ct, captureStdout: true);
        if (code != 0) return ("", new List<string>());

        string video = "";
        var audio = new List<string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // righe "codec_name,codec_type,attached_pic"
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            if (parts[1] == "video" && video.Length == 0 && !(parts.Length > 2 && parts[2] == "1")) video = parts[0];
            else if (parts[1] == "audio") audio.Add(parts[0]);
        }
        return (video, audio);
    }

    private static async Task<(int Code, string Output)> RunAsync(string exe, IEnumerable<string> args, CancellationToken ct, bool captureStdout = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        ChildProcessJob.Add(proc);

        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return (proc.ExitCode, captureStdout ? await stdout : await stderr);
    }

    private static string LastLines(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", lines.TakeLast(2));
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
