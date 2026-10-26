using System.Diagnostics;
using System.Globalization;
using DownloadManager.Helpers;
using DownloadManager.Models;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Download;

/// <summary>
/// Conversioni scelte dall'utente a fine download: audio AAC, oppure H.264 + AAC per i player web
/// (con il processore o con la scheda video). Una conversione alla volta: farne di più insieme non
/// accorcia il tempo totale (la potenza si divide) e rallenterebbe il resto del PC.
/// </summary>
public static class VideoTranscoder
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>
    /// Stereo con i dialoghi (canale centrale) pieni e volume normalizzato a -16 LUFS, come i servizi
    /// di streaming: il downmix standard abbassava il centrale e il volume risultava più basso.
    /// Funziona con qualunque disposizione dei canali (2.0, 5.1, 7.1...).
    /// </summary>
    private const string AudioFilter =
        "aresample=ochl=stereo:clev=1.0:slev=0.707:lfe_mix_level=0.5,loudnorm=I=-16:TP=-2:LRA=11,aresample=48000";

    private static readonly string[] AacAudio = { "-c:a", "aac", "-b:a", "192k", "-af", AudioFilter };

    /// <summary>
    /// Converte il .mp4 sul posto. <paramref name="progress"/> riceve messaggi già pronti per la coda
    /// ("In attesa...", "Conversione H.264: 37%"). True se riuscito; altrimenti il file resta com'era.
    /// </summary>
    public static async Task<bool> ConvertAsync(string ffmpeg, string file, VideoConversion mode,
        ILogger logger, Action<string> progress, CancellationToken ct)
    {
        if (mode is VideoConversion.Original or VideoConversion.Mp4Copy) return true;

        progress("In coda per conversione");
        await OneAtATime.WaitAsync(ct);
        try
        {
            var duration = await ProbeDurationAsync(ffmpeg, file, ct);
            var audioMap = AudioTracks.MapArgs(await AudioTracks.ItalianFirstOrderAsync(ffmpeg, file, ct));

            // Tentativi in ordine: con i sottotitoli, poi senza (alcuni formati non entrano nell'MP4)
            foreach (var videoArgs in VideoAttempts(mode))
            foreach (var withSubs in new[] { true, false })
            {
                ct.ThrowIfCancellationRequested();
                var temp = file + ".converting.mp4";
                var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-nostats", "-progress", "pipe:1" };
                if (videoArgs.HwDecode) args.AddRange(new[] { "-hwaccel", "auto" });
                args.AddRange(new[] { "-i", file, "-map", "0:V:0" });
                args.AddRange(audioMap);   // tutte le tracce, italiano per primo
                if (withSubs) args.AddRange(new[] { "-map", "0:s?", "-c:s", "mov_text" }); else args.Add("-sn");
                args.AddRange(videoArgs.Args);
                args.AddRange(AacAudio);
                args.AddRange(new[] { "-map_metadata", "0", "-movflags", "+faststart", "-f", "mp4", temp });

                var sw = Stopwatch.StartNew();
                var (code, err) = await RunWithProgressAsync(ffmpeg, args, duration,
                    pct => progress($"Conversione {pct}%"), ct);
                if (code == 0 && File.Exists(temp) && new FileInfo(temp).Length > 0)
                {
                    File.Move(temp, file, overwrite: true);
                    logger.LogInformation("[CONVERSIONE] {File}: {Mode} con {Encoder} in {M:F1} min",
                        Path.GetFileName(file), mode, videoArgs.Name, sw.Elapsed.TotalMinutes);
                    return true;
                }
                TryDelete(temp);
                logger.LogInformation("[CONVERSIONE] {Encoder}{Subs} non riuscito per {File}: {Err}",
                    videoArgs.Name, withSubs ? "" : " (senza sottotitoli)", Path.GetFileName(file), LastLines(err));
            }

            logger.LogWarning("[CONVERSIONE] Impossibile convertire {File}: resta il file originale.", Path.GetFileName(file));
            return false;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private record VideoArgs(string Name, string[] Args, bool HwDecode = false);

    /// <summary>Codificatori da provare, nell'ordine. La scheda video ripiega su processore se non disponibile.</summary>
    private static IEnumerable<VideoArgs> VideoAttempts(VideoConversion mode)
    {
        string[] h264Common = { "-profile:v", "high", "-level:v", "4.1", "-pix_fmt", "yuv420p" };
        var x264 = new VideoArgs("processore", new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "21" }.Concat(h264Common).ToArray());

        switch (mode)
        {
            case VideoConversion.AacAudio:
                yield return new VideoArgs("video copiato", new[] { "-c:v", "copy", "-tag:v", "hvc1" });
                yield return new VideoArgs("video copiato", new[] { "-c:v", "copy" });
                break;
            case VideoConversion.H264Gpu:
                yield return new VideoArgs("scheda NVIDIA", new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", "21", "-b:v", "0" }.Concat(h264Common).ToArray(), HwDecode: true);
                yield return new VideoArgs("scheda Intel", new[] { "-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "22" }.Concat(h264Common).ToArray());
                yield return new VideoArgs("scheda AMD", new[] { "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "21", "-qp_p", "23" }.Concat(h264Common).ToArray());
                yield return x264;
                break;
            default:
                yield return x264;
                break;
        }
    }

    private static async Task<double> ProbeDurationAsync(string ffmpeg, string file, CancellationToken ct)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe");
        if (!File.Exists(ffprobe)) return 0;
        var psi = new ProcessStartInfo { FileName = ffprobe, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", file }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = await p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    /// <summary>ffmpeg con "-progress pipe:1": legge out_time_us e riporta la percentuale.</summary>
    private static async Task<(int Code, string Err)> RunWithProgressAsync(string exe, IEnumerable<string> args,
        double durationSeconds, Action<int> onPercent, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        ChildProcessJob.Add(proc);
        try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }   // il PC resta reattivo

        var stderr = proc.StandardError.ReadToEndAsync(ct);
        int last = -1;
        try
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            {
                if (durationSeconds <= 0 || !line.StartsWith("out_time_us=")) continue;
                if (!long.TryParse(line["out_time_us=".Length..], out var us)) continue;
                int pct = (int)Math.Clamp(us / 1_000_000.0 / durationSeconds * 100, 0, 99);
                if (pct != last) { last = pct; onPercent(pct); }
            }
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return (proc.ExitCode, await stderr);
    }

    private static string LastLines(string text) =>
        string.Join(" | ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(2));

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
