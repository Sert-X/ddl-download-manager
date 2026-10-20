using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DownloadManager.Helpers;
using DownloadManager.Models;
using DownloadManager.Services.Streaming.StreamingCommunity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Download;

/// <summary>
/// Download HLS con yt-dlp, traccia per traccia.
///
/// Per StreamingCommunity la playlist master viene letta dall'app (che passa Cloudflare):
/// si scaricano esattamente la variante video della qualità scelta e le tracce audio
/// delle lingue scelte (in parallelo), poi i sottotitoli, e ffmpeg unisce tutto in locale.
/// Prima si lasciava scegliere a yt-dlp con un filtro sulla lingua che, se non
/// riconosciuto, ripiegava su una traccia qualsiasi (es. inglese).
///
/// L'avanzamento si misura dai byte scritti su disco e la dimensione si stima da
/// banda × durata della playlist: le stime di yt-dlp con i frammenti paralleli
/// erano inaffidabili (anche decine di GB per un file da 800 MB).
/// </summary>
public partial class DownloadService
{
    /// <summary>Banda stimata di una traccia audio AAC quando la playlist non la dichiara.</summary>
    private const long AudioTrackBitsPerSecond = 160_000;

    /// <summary>Segmenti paralleli per ogni traccia audio (sono piccole: il grosso va al video).</summary>
    private const int AudioTrackFragments = 4;

    /// <summary>Tentativi per traccia: a ogni errore si riparte con metà dei segmenti.</summary>
    private const int TrackAttempts = 3;

    private sealed class TrackJob
    {
        public required string Label { get; init; }
        public required string Url { get; init; }
        /// <summary>Prefisso dei file in tempDir ("video", "audio_0", ...).</summary>
        public required string OutBase { get; init; }
        public string? Format { get; init; }
        public MediaTrack? Track { get; init; }
        /// <summary>Dimensione attesa (banda × durata), 0 se sconosciuta.</summary>
        public long PlannedBytes { get; init; }
        /// <summary>Segmenti scaricati in parallelo (yt-dlp --concurrent-fragments).</summary>
        public int Fragments { get; set; } = 4;

        public long Downloaded;
        public long FragIndex;
        public long FragCount;
        public string? OutputFile;

        public long EstimatedTotal
        {
            get
            {
                // Dopo un po' di frammenti la proporzione è affidabile; prima si usa il piano.
                long fragEstimate = FragCount > 0 && FragIndex >= 20
                    ? (long)((double)Downloaded / FragIndex * FragCount)
                    : 0;
                long best = fragEstimate > 0 ? fragEstimate : PlannedBytes;
                return Math.Max(best, Downloaded);
            }
        }
    }

    private async Task DownloadHlsWithYtDlpAsync(DownloadItem item, string ytDlpPath, CancellationToken ct)
    {
        var ffmpegPath = _ffmpegLocator.GetFfmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath))
            throw new InvalidOperationException("FFmpeg non trovato (serve a yt-dlp e per unire le tracce).");
        var ffmpegDir = Path.GetDirectoryName(ffmpegPath) ?? "";

        var tempDir = Path.Combine(Path.GetTempPath(), "DDLDownloadManager", "ytdlp", item.Id.ToString());
        Directory.CreateDirectory(tempDir);
        var finalMp4 = Path.Combine(tempDir, "final.mp4");

        // === File finale già pronto da un tentativo precedente? ===
        if (File.Exists(finalMp4) && new FileInfo(finalMp4).Length > 0 &&
            await ProbeHasAudioAndVideoAsync(finalMp4, ffmpegDir, ct))
        {
            _logger.LogWarning("[YT-DLP] File finale già pronto ({MB} MB), salto il download",
                new FileInfo(finalMp4).Length / 1024 / 1024);
            CompleteDownload(item, finalMp4, tempDir);
            return;
        }

        PrepareTempDir(tempDir);
        var cookiesFile = await WriteCookiesFileAsync(item, tempDir, ct);

        // === Piano delle tracce ===
        var jobs = new List<TrackJob>();
        var subtitles = new List<MediaTrack>();
        bool burnSubtitles = false;
        double? duration = null;

        var sc = item.ProviderKey == "StreamingCommunity"
            ? _services.GetService<IStreamingCommunityService>()
            : null;
        MasterTracks? master = null;
        if (sc != null)
        {
            SetUi(() => item.ErrorMessage = "Analisi tracce...");
            try { master = await sc.GetMasterTracksAsync(item.Url, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[TRACKS] Lettura playlist master fallita"); }
        }

        if (sc != null && master != null && master.Videos.Count > 0)
        {
            var video = PickVideo(master.Videos, item.QualityName);
            var audios = PickAudios(master.Audios, item.AudioLanguages);
            subtitles = PickSubtitles(master.Subtitles, item.SubtitleLanguages);
            burnSubtitles = item.BurnSubtitles && subtitles.Count > 0;

            duration = await sc.GetPlaylistDurationAsync(video.Url, ct);
            long Planned(long bitsPerSecond) => duration is > 0 ? (long)(bitsPerSecond / 8.0 * duration.Value) : 0;

            var videoBps = video.AverageBandwidth > 0 ? video.AverageBandwidth : video.Bandwidth;
            jobs.Add(new TrackJob { Label = $"video {video.Name}", Url = video.Url, OutBase = "video", PlannedBytes = Planned(videoBps) });
            for (int i = 0; i < audios.Count; i++)
            {
                jobs.Add(new TrackJob
                {
                    Label = $"audio {audios[i].DisplayName}",
                    Url = audios[i].Url,
                    OutBase = $"audio_{i}",
                    Track = audios[i],
                    PlannedBytes = Planned(AudioTrackBitsPerSecond)
                });
            }

            _logger.LogInformation(
                "[TRACKS] {Title}: video {Video}, audio [{Audio}], sottotitoli [{Subs}]{Burn}, durata {Dur}",
                Path.GetFileName(item.DestinationPath), video.Name,
                string.Join(", ", audios.Select(a => a.DisplayName)),
                string.Join(", ", subtitles.Select(s => s.DisplayName)),
                burnSubtitles ? " (impressi)" : "",
                duration is > 0 ? TimeSpan.FromSeconds(duration.Value).ToString(@"h\:mm\:ss") : "?");
        }
        else
        {
            // Nessuna playlist leggibile (o provider diverso): yt-dlp sceglie dal master.
            jobs.Add(new TrackJob { Label = "video+audio", Url = item.Url, OutBase = "video", Format = BuildYtDlpFormat(item.QualityName) });
        }

        // === Budget di segmenti paralleli ===
        // Il cursore indica il totale per l'intero download: il server limita le connessioni
        // per IP e con 32 segmenti per OGNI traccia rispondeva 503 (traccia audio persa).
        int budget = Math.Clamp(_settings.Current.StreamingConcurrentFragments, 4, 64);
        int audioCount = jobs.Count - 1;
        jobs[0].Fragments = Math.Max(4, budget - AudioTrackFragments * audioCount);
        foreach (var j in jobs.Skip(1)) j.Fragments = AudioTrackFragments;

        // === Download delle tracce (in parallelo) con avanzamento dal disco ===
        SetUi(() => item.ErrorMessage = string.Empty);
        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = TrackProgressAsync(item, jobs, tempDir, progressCts.Token);
        try
        {
            await Task.WhenAll(jobs.Select(j => RunTrackWithRetriesAsync(j, ytDlpPath, ffmpegDir, tempDir, cookiesFile, item, ct)));
        }
        finally
        {
            progressCts.Cancel();
            try { await progressTask; } catch (OperationCanceledException) { }
        }

        // === Sottotitoli ===
        var subtitleFiles = new List<(MediaTrack Track, string File)>();
        foreach (var sub in subtitles)
        {
            ct.ThrowIfCancellationRequested();
            SetUi(() => item.ErrorMessage = $"Sottotitoli {sub.DisplayName}...");
            var vtt = await sc!.DownloadSubtitleAsync(sub.Url, ct);
            if (string.IsNullOrWhiteSpace(vtt))
            {
                _logger.LogWarning("[TRACKS] Sottotitoli {Name} non scaricabili, li salto", sub.DisplayName);
                continue;
            }
            var file = Path.Combine(tempDir, $"sub_{subtitleFiles.Count}.vtt");
            await File.WriteAllTextAsync(file, vtt, new UTF8Encoding(false), ct);
            subtitleFiles.Add((sub, file));
        }
        if (burnSubtitles && subtitleFiles.Count == 0) burnSubtitles = false;

        // === Unione ===
        string output;
        if (jobs.Count == 1 && subtitleFiles.Count == 0)
        {
            output = jobs[0].OutputFile!;
        }
        else
        {
            SetUi(() => item.ErrorMessage = burnSubtitles ? "Imprimo i sottotitoli nel video..." : "Unione delle tracce...");
            await MuxTracksAsync(item, ffmpegPath, tempDir, jobs, subtitleFiles, burnSubtitles, duration, finalMp4, ct);
            output = finalMp4;
        }

        if (!await ProbeHasAudioAndVideoAsync(output, ffmpegDir, ct))
            _logger.LogWarning("[YT-DLP] File finale senza traccia audio o video: {Path}", output);

        CompleteDownload(item, output, tempDir);
    }

    // ------------------------------------------------------------------
    //  Scelta delle tracce
    // ------------------------------------------------------------------

    private static VideoQuality PickVideo(List<VideoQuality> videos, string qualityName)
    {
        var byName = videos.FirstOrDefault(v => string.Equals(v.Name, qualityName, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;

        var m = Regex.Match(qualityName ?? "", @"(\d{3,4})p");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var h))
        {
            var fitting = videos
                .Select(v => (v, height: int.TryParse(Regex.Match(v.Name, @"(\d{3,4})p").Groups[1].Value, out var vh) ? vh : 0))
                .Where(x => x.height > 0 && x.height <= h)
                .OrderByDescending(x => x.height).ThenByDescending(x => x.v.Bandwidth)
                .Select(x => x.v).FirstOrDefault();
            if (fitting != null) return fitting;
        }
        return videos.OrderByDescending(v => v.Bandwidth).First();   // Auto = migliore
    }

    private static List<string> SplitKeys(string keys) =>
        (keys ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static List<MediaTrack> PickAudios(List<MediaTrack> audios, string wanted)
    {
        if (audios.Count == 0) return new();
        var keys = SplitKeys(wanted);
        var chosen = keys.Count > 0
            ? keys.Select(k => audios.FirstOrDefault(a => a.Matches(k))).Where(a => a != null).Distinct().ToList()!
            : new List<MediaTrack?>();

        // Niente scelta (o lingue non presenti): italiano, altrimenti la traccia predefinita.
        if (chosen.Count == 0)
            chosen.Add(audios.FirstOrDefault(a => a.IsItalian) ?? audios.FirstOrDefault(a => a.IsDefault) ?? audios[0]);
        return chosen!;
    }

    private static List<MediaTrack> PickSubtitles(List<MediaTrack> subs, string wanted)
    {
        var keys = SplitKeys(wanted);
        return keys.Select(k => subs.FirstOrDefault(s => s.Matches(k))).Where(s => s != null).Distinct().ToList()!;
    }

    // ------------------------------------------------------------------
    //  yt-dlp per una singola traccia
    // ------------------------------------------------------------------

    /// <summary>
    /// Scarica una traccia riprovando se fallisce (es. 503 del server): a ogni tentativo
    /// si cancellano i suoi file e si dimezzano i segmenti paralleli. Le altre tracce
    /// intanto continuano, quindi un errore sull'audio non fa ripartire il video.
    /// </summary>
    private async Task RunTrackWithRetriesAsync(TrackJob job, string ytDlpPath, string ffmpegDir, string tempDir,
        string? cookiesFile, DownloadItem item, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await RunYtDlpTrackAsync(job, ytDlpPath, ffmpegDir, tempDir, cookiesFile, item, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < TrackAttempts)
            {
                job.Fragments = Math.Max(2, job.Fragments / 2);
                _logger.LogWarning("[YT-DLP] {Label}: tentativo {N}/{Max} fallito ({Err}). Riprovo con {F} segmenti paralleli...",
                    job.Label, attempt, TrackAttempts, ex.Message, job.Fragments);

                foreach (var f in Directory.GetFiles(tempDir, job.OutBase + ".*"))
                {
                    try { File.Delete(f); } catch { /* verrà sovrascritto */ }
                }
                Interlocked.Exchange(ref job.FragIndex, 0);
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }

    private async Task RunYtDlpTrackAsync(TrackJob job, string ytDlpPath, string ffmpegDir, string tempDir,
        string? cookiesFile, DownloadItem item, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = tempDir
        };
        var a = psi.ArgumentList;
        a.Add("--impersonate"); a.Add("chrome");
        a.Add("-o"); a.Add(Path.Combine(tempDir, job.OutBase + ".%(ext)s"));
        a.Add("--ffmpeg-location"); a.Add(ffmpegDir);
        if (cookiesFile != null) { a.Add("--cookies"); a.Add(cookiesFile); }
        a.Add("--add-header"); a.Add("Referer:https://streamingcommunityz.rip/");
        a.Add("--add-header"); a.Add($"sec-ch-ua:{StreamingCommunityService.SecChUa}");
        a.Add("--add-header"); a.Add("sec-ch-ua-mobile:?0");
        a.Add("--add-header"); a.Add("sec-ch-ua-platform:\"Windows\"");
        a.Add("--user-agent"); a.Add(StreamingCommunityService.RealisticUserAgent);
        if (job.Format != null)
        {
            a.Add("-f"); a.Add(job.Format);
            a.Add("--merge-output-format"); a.Add("mp4");
        }

        var proxy = _proxyProvider.GetActive(item.ProviderKey);
        if (proxy != null) { a.Add("--proxy"); a.Add(BuildFfmpegProxyUrl(proxy)); }

        a.Add("--no-playlist");
        a.Add("--newline");
        a.Add("--no-warnings");
        a.Add("--progress-template");
        a.Add("download:__PROGRESS__ %(progress.fragment_index)s|%(progress.fragment_count)s");
        a.Add("--concurrent-fragments"); a.Add(job.Fragments.ToString(CultureInfo.InvariantCulture));
        a.Add("--retries"); a.Add("10");
        a.Add("--fragment-retries"); a.Add("20");
        a.Add("--retry-sleep"); a.Add("fragment:exp=1:20");
        a.Add("--socket-timeout"); a.Add("30");
        a.Add("--force-ipv4");
        a.Add(job.Url);

        _logger.LogInformation("[YT-DLP] Avvio {Label} ({F} segmenti paralleli)", job.Label, job.Fragments);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        long lastProgressTick = Environment.TickCount64;

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            foreach (var raw in e.Data.Split('\r'))
            {
                var line = raw.Trim();
                if (line.StartsWith("__PROGRESS__", StringComparison.Ordinal))
                {
                    var parts = line.Substring("__PROGRESS__".Length).Trim().Split('|');
                    if (parts.Length >= 2 && long.TryParse(parts[0], out var idx) && long.TryParse(parts[1], out var count))
                    {
                        Interlocked.Exchange(ref job.FragCount, count);
                        // Con i frammenti paralleli arrivano anche indici "vecchi": si tiene il massimo
                        long seen;
                        while (idx > (seen = Interlocked.Read(ref job.FragIndex)) &&
                               Interlocked.CompareExchange(ref job.FragIndex, idx, seen) != seen) { }
                    }
                    Interlocked.Exchange(ref lastProgressTick, Environment.TickCount64);
                }
                else if (line.Contains("ERROR", StringComparison.Ordinal) || line.StartsWith("[info]", StringComparison.Ordinal))
                {
                    _logger.LogInformation("[YT-DLP] {Label}: {Line}", job.Label, line);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            lock (stderr) { if (stderr.Length < 4000) stderr.AppendLine(e.Data); }
        };

        process.Start();
        ChildProcessJob.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var killOnCancel = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });

        // Watchdog: 2 minuti senza avanzamento → si chiude il processo
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(5000, watchdogCts.Token);
                    if (Environment.TickCount64 - Interlocked.Read(ref lastProgressTick) > 120_000)
                    {
                        _logger.LogError("[YT-DLP] {Label}: nessun progresso da 2 minuti, chiudo il processo", job.Label);
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        });

        await process.WaitForExitAsync(CancellationToken.None);
        watchdogCts.Cancel();
        try { await watchdog; } catch { }
        ct.ThrowIfCancellationRequested();

        string err;
        lock (stderr) err = stderr.ToString();

        job.OutputFile = FindTrackOutput(tempDir, job.OutBase);

        // Download finito ma rinomina del .part fallita (antivirus che tiene aperto il file)
        if (job.OutputFile == null && err.Contains("Unable to rename file", StringComparison.OrdinalIgnoreCase))
            job.OutputFile = await RetryRenamePartAsync(tempDir, job.OutBase, ct);

        if (job.OutputFile == null)
        {
            _logger.LogError("[YT-DLP] {Label}: uscita {Code}. STDERR:\n{Err}", job.Label, process.ExitCode, err);
            var firstError = err.Split('\n').FirstOrDefault(l => l.Contains("ERROR"))?.Trim();
            throw new InvalidOperationException(
                $"yt-dlp ({job.Label}) terminato con codice {process.ExitCode}" + (firstError != null ? $": {firstError}" : "."));
        }

        job.Downloaded = new FileInfo(job.OutputFile).Length;
        _logger.LogInformation("[YT-DLP] Completato {Label}: {MB} MB", job.Label, job.Downloaded / 1024 / 1024);
    }

    private static bool IsTemporaryName(string name) =>
        name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase) ||
        name.Contains(".part-Frag", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(name, @"\.f[\w-]+\.\w+$");   // formati separati non ancora uniti

    private static string? FindTrackOutput(string tempDir, string outBase) =>
        Directory.GetFiles(tempDir, outBase + ".*")
            .Where(f => !IsTemporaryName(Path.GetFileName(f)))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();

    private async Task<string?> RetryRenamePartAsync(string tempDir, string outBase, CancellationToken ct)
    {
        var part = Directory.GetFiles(tempDir, outBase + ".*.part").FirstOrDefault(f => !f.Contains("-Frag"));
        if (part == null) return null;
        var target = part[..^".part".Length];

        _logger.LogWarning("[YT-DLP] Rinomina finale di {File} fallita (file bloccato), riprovo dall'app...", Path.GetFileName(part));
        for (int attempt = 1; attempt <= 15; attempt++)
        {
            try
            {
                File.Move(part, target, overwrite: true);
                _logger.LogInformation("[YT-DLP] Rinomina riuscita al tentativo {N}", attempt);
                return target;
            }
            catch (IOException) { await Task.Delay(2000, ct); }
        }
        return null;
    }

    // ------------------------------------------------------------------
    //  Avanzamento
    // ------------------------------------------------------------------

    private async Task TrackProgressAsync(DownloadItem item, List<TrackJob> jobs, string tempDir, CancellationToken ct)
    {
        long lastBytes = 0;
        long lastTick = Environment.TickCount64;
        double speed = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(700, ct);

                // Byte reali su disco per ogni traccia (file finale, .part e frammenti in corso)
                foreach (var job in jobs)
                {
                    if (job.OutputFile != null) continue;
                    long bytes = 0;
                    foreach (var f in Directory.EnumerateFiles(tempDir, job.OutBase + ".*"))
                    {
                        try { bytes += new FileInfo(f).Length; } catch { /* file appena spostato */ }
                    }
                    job.Downloaded = bytes;
                }

                long done = jobs.Sum(j => j.Downloaded);
                long total = jobs.Sum(j => j.EstimatedTotal);

                var now = Environment.TickCount64;
                var elapsed = (now - lastTick) / 1000.0;
                if (elapsed > 0)
                {
                    var instant = Math.Max(0, done - lastBytes) / elapsed;
                    speed = speed <= 0 ? instant : speed * 0.7 + instant * 0.3;   // media mobile
                }
                lastBytes = done;
                lastTick = now;

                var capturedSpeed = (long)speed;
                SetUi(() =>
                {
                    item.Status = DownloadStatus.Downloading;
                    item.DownloadedBytes = Math.Min(done, total);
                    if (total > 0) item.TotalBytes = total;
                    item.SpeedBytesPerSecond = capturedSpeed;
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------
    //  Unione con ffmpeg
    // ------------------------------------------------------------------

    private async Task MuxTracksAsync(DownloadItem item, string ffmpegPath, string tempDir, List<TrackJob> jobs,
        List<(MediaTrack Track, string File)> subtitles, bool burn, double? duration, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // Directory di lavoro = tempDir: il filtro "subtitles" usa un nome relativo
            // (i percorsi Windows con "C:" andrebbero escapati nella sintassi dei filtri).
            WorkingDirectory = tempDir
        };
        var a = psi.ArgumentList;
        a.Add("-hide_banner"); a.Add("-nostats");
        a.Add("-progress"); a.Add("pipe:1");
        a.Add("-y");

        var video = jobs[0];
        var audioJobs = jobs.Skip(1).ToList();
        a.Add("-i"); a.Add(video.OutputFile!);
        foreach (var j in audioJobs) { a.Add("-i"); a.Add(j.OutputFile!); }

        // Il sottotitolo impresso non viene aggiunto anche come traccia
        var softSubs = burn ? subtitles.Skip(1).ToList() : subtitles;
        foreach (var s in softSubs) { a.Add("-i"); a.Add(s.File); }

        a.Add("-map"); a.Add("0:v:0");
        if (audioJobs.Count == 0) { a.Add("-map"); a.Add("0:a?"); }
        for (int i = 0; i < audioJobs.Count; i++) { a.Add("-map"); a.Add($"{i + 1}:a:0"); }
        for (int i = 0; i < softSubs.Count; i++) { a.Add("-map"); a.Add($"{audioJobs.Count + 1 + i}:0"); }

        if (burn)
        {
            a.Add("-vf"); a.Add($"subtitles={Path.GetFileName(subtitles[0].File)}");
            a.Add("-c:v"); a.Add("libx264");
            a.Add("-preset"); a.Add("veryfast");
            a.Add("-crf"); a.Add("20");
        }
        else
        {
            a.Add("-c:v"); a.Add("copy");
        }
        a.Add("-c:a"); a.Add("copy");
        if (softSubs.Count > 0) { a.Add("-c:s"); a.Add("mov_text"); }

        // Lingue e titoli delle tracce; la prima audio è quella predefinita
        for (int i = 0; i < audioJobs.Count; i++)
        {
            var t = audioJobs[i].Track;
            if (t == null) continue;
            if (!string.IsNullOrWhiteSpace(t.Language)) { a.Add($"-metadata:s:a:{i}"); a.Add($"language={t.Language}"); }
            if (!string.IsNullOrWhiteSpace(t.Name)) { a.Add($"-metadata:s:a:{i}"); a.Add($"title={t.Name}"); }
            a.Add($"-disposition:a:{i}"); a.Add(i == 0 ? "default" : "0");
        }
        for (int i = 0; i < softSubs.Count; i++)
        {
            var t = softSubs[i].Track;
            if (!string.IsNullOrWhiteSpace(t.Language)) { a.Add($"-metadata:s:s:{i}"); a.Add($"language={t.Language}"); }
            if (!string.IsNullOrWhiteSpace(t.Name)) { a.Add($"-metadata:s:s:{i}"); a.Add($"title={t.Name}"); }
            a.Add($"-disposition:s:{i}"); a.Add(t.IsForced ? "forced" : "0");
        }

        a.Add("-movflags"); a.Add("+faststart");
        a.Add(output);

        _logger.LogInformation("[MUX] ffmpeg {Args}", string.Join(' ', a.Select(x => x.Contains(' ') ? $"\"{x}\"" : x)));

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            lock (stderr) { if (stderr.Length > 6000) stderr.Remove(0, 2000); stderr.AppendLine(e.Data); }
        };
        process.OutputDataReceived += (_, e) =>
        {
            // Percentuale (utile soprattutto quando si imprimono i sottotitoli, che è lento)
            if (duration is not > 0 || e.Data == null || !e.Data.StartsWith("out_time_us=", StringComparison.Ordinal)) return;
            if (!long.TryParse(e.Data.AsSpan("out_time_us=".Length), out var us)) return;
            var pct = Math.Clamp(us / 1_000_000.0 / duration.Value * 100, 0, 100);
            var label = burn ? "Imprimo i sottotitoli nel video" : "Unione delle tracce";
            SetUi(() => item.ErrorMessage = $"{label}: {pct:F0}%");
        };

        process.Start();
        ChildProcessJob.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var killOnCancel = ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });

        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();

        if (process.ExitCode != 0 || !File.Exists(output))
        {
            string err;
            lock (stderr) err = stderr.ToString();
            _logger.LogError("[MUX] ffmpeg uscita {Code}:\n{Err}", process.ExitCode, err);
            throw new InvalidOperationException($"Unione delle tracce fallita (ffmpeg codice {process.ExitCode}).");
        }
    }

    // ------------------------------------------------------------------
    //  Utilità
    // ------------------------------------------------------------------

    /// <summary>
    /// Svuota la cartella temporanea prima di (ri)partire: yt-dlp non sa riprendere gli HLS
    /// di vixcloud (il token cambia). Un file bloccato vuol dire che un altro processo sta
    /// già scaricando questo episodio: si interrompe invece di scrivere in due.
    /// </summary>
    private void PrepareTempDir(string tempDir)
    {
        int removed = 0;
        foreach (var f in Directory.GetFiles(tempDir))
        {
            try { File.Delete(f); removed++; }
            catch (IOException)
            {
                throw new InvalidOperationException(
                    "Questo download è già in corso in un altro processo (forse un'altra finestra dell'app). Chiudila e riprova.");
            }
            catch { /* accesso negato: si prova comunque */ }
        }
        if (removed > 0)
            _logger.LogInformation("[YT-DLP] Cancellati {N} file di un tentativo precedente", removed);
    }

    private async Task<string?> WriteCookiesFileAsync(DownloadItem item, string tempDir, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.SessionCookies)) return null;
        try
        {
            var file = Path.Combine(tempDir, "cookies.txt");
            await File.WriteAllTextAsync(file, SanitizeNetscapeCookies(item.SessionCookies), ct);
            return file;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[YT-DLP] Impossibile scrivere cookies.txt");
            return null;
        }
    }

    private void CompleteDownload(DownloadItem item, string producedFile, string tempDir)
    {
        var destDir = Path.GetDirectoryName(item.DestinationPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        File.Move(producedFile, item.DestinationPath, overwrite: true);
        try { Directory.Delete(tempDir, true); } catch { }

        var finalSize = new FileInfo(item.DestinationPath).Length;
        SetUi(() =>
        {
            item.TotalBytes = finalSize;
            item.DownloadedBytes = finalSize;
            item.SpeedBytesPerSecond = 0;
            item.ErrorMessage = string.Empty;
            item.Status = DownloadStatus.Completed;
        });
        _logger.LogInformation("[YT-DLP] Completato: {Path} ({MB} MB)", item.DestinationPath, finalSize / 1024 / 1024);
    }
}
