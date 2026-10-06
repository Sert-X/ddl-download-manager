using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Proxy;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Download;

public class DownloadService : IDownloadService
{
    private readonly ILogger<DownloadService> _logger;
    private readonly DownloadRepository _repository;
    private readonly HttpClient _http;
    private readonly FfmpegLocator _ffmpegLocator;

    private const long ChunkSize = 10 * 1024 * 1024;
    private const int ParallelChunksPerFile = 4;
    private const long SaveThreshold = 5 * 1024 * 1024;
    private const int UiThrottleMs = 150;

    public DownloadService(
        ILogger<DownloadService> logger,
        DownloadRepository repository,
        IProxyProvider proxyProvider,
        FfmpegLocator ffmpegLocator)
    {
        _logger = logger;
        _repository = repository;
        _ffmpegLocator = ffmpegLocator;

        var handler = new HttpClientHandler
        {
            MaxConnectionsPerServer = 16,
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = new DynamicWebProxy(proxyProvider),
            UseProxy = true
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    private static void SetUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    public async Task DownloadItemAsync(DownloadItem item, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(item.DestinationPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        SetUi(() =>
        {
            item.Status = DownloadStatus.Downloading;
            item.ErrorMessage = string.Empty;
        });

        // ============================================================
        //  RILEVA HLS (.m3u8) E DELEGA A FFMPEG
        // ============================================================
        if (IsHlsUrl(item.Url))
        {
            await DownloadHlsAsync(item, ct);
            await _repository.ClearChunksAsync(item.Id);
            return;
        }

        var head = await _http.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, item.Url), ct);
        head.EnsureSuccessStatusCode();

        var totalSize = head.Content.Headers.ContentLength
            ?? throw new InvalidOperationException("Dimensione file sconosciuta.");
        SetUi(() => item.TotalBytes = totalSize);

        bool supportsRange = head.Headers.AcceptRanges.Contains("bytes");
        _logger.LogInformation(
            "Download avviato: {Url} → {Dest} ({Size} bytes, range={Range})",
            item.Url, item.DestinationPath, totalSize, supportsRange);

        var tempPath = item.DestinationPath + ".part";

        if (File.Exists(tempPath) && File.Exists(item.DestinationPath))
        {
            _logger.LogInformation("File già presente, salto download: {Path}", item.DestinationPath);
            SetUi(() =>
            {
                item.Status = DownloadStatus.Completed;
                item.DownloadedBytes = totalSize;
                item.SpeedBytesPerSecond = 0;
            });
            await _repository.ClearChunksAsync(item.Id);
            return;
        }

        var sw = Stopwatch.StartNew();

        if (!supportsRange)
            await DownloadSequentialAsync(item, totalSize, ct);
        else
            await DownloadParallelAsync(item, totalSize, sw, ct);

        if (!File.Exists(tempPath))
        {
            throw new InvalidOperationException(
                $"Download incompleto: il file temporaneo '{tempPath}' non esiste.");
        }

        var actualSize = new FileInfo(tempPath).Length;
        if (actualSize != totalSize)
        {
            throw new InvalidOperationException(
                $"Download incompleto: il file scaricato ha {actualSize} bytes invece di {totalSize}.");
        }

        if (File.Exists(item.DestinationPath))
            File.Delete(item.DestinationPath);
        File.Move(tempPath, item.DestinationPath);

        SetUi(() =>
        {
            item.Status = DownloadStatus.Completed;
            item.DownloadedBytes = totalSize;
            item.SpeedBytesPerSecond = 0;
        });
        await _repository.ClearChunksAsync(item.Id);

        _logger.LogInformation("Download completato: {Path}", item.DestinationPath);
    }

    private async Task DownloadSequentialAsync(DownloadItem item, long totalSize, CancellationToken ct)
    {
        var tempPath = item.DestinationPath + ".part";

        using var response = await _http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        int read;
        long totalRead = 0;
        long lastUiUpdate = 0;
        var sw = Stopwatch.StartNew();

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;

            var now = Environment.TickCount64;
            if (now - lastUiUpdate < UiThrottleMs) continue;
            lastUiUpdate = now;

            var bytes = totalRead;
            var speed = totalRead / Math.Max(1, sw.Elapsed.TotalSeconds);
            SetUi(() =>
            {
                item.DownloadedBytes = bytes;
                item.SpeedBytesPerSecond = speed;
            });
        }
    }

    private async Task DownloadParallelAsync(DownloadItem item, long totalSize, Stopwatch sw, CancellationToken ct)
    {
        var allRanges = CalculateRanges(totalSize);
        var doneChunks = await _repository.GetChunksAsync(item.Id);

        var todoRanges = allRanges
            .Where(r => !doneChunks.Any(d => d.Start <= r.Start && d.End >= r.End))
            .ToList();

        long alreadyDownloaded = doneChunks.Sum(c => c.End - c.Start + 1);
        SetUi(() => item.DownloadedBytes = alreadyDownloaded);

        _logger.LogInformation(
            "Chunk da scaricare: {Count}/{Total} (già fatti: {Done} MB)",
            todoRanges.Count, allRanges.Count, alreadyDownloaded / 1024 / 1024);

        var tempPath = item.DestinationPath + ".part";

        await using var fs = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.Write, 81920, true);
        fs.SetLength(totalSize);

        var semaphore = new SemaphoreSlim(ParallelChunksPerFile);

        var progressLock = new object();
        long localDownloaded = alreadyDownloaded;
        long lastUiUpdate = Environment.TickCount64;
        long lastSavedBytesLocal = alreadyDownloaded;

        var tasks = todoRanges.Select(async range =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                await DownloadChunkAsync(item.Url, fs, range, bytes =>
                {
                    long toReport = -1;
                    long speedToReport = 0;
                    bool doSave = false;

                    lock (progressLock)
                    {
                        localDownloaded += bytes;
                        var now = Environment.TickCount64;

                        if (now - lastUiUpdate >= UiThrottleMs)
                        {
                            lastUiUpdate = now;
                            toReport = localDownloaded;
                            speedToReport = (long)(localDownloaded / Math.Max(1, sw.Elapsed.TotalSeconds));
                        }

                        if (localDownloaded - lastSavedBytesLocal >= SaveThreshold)
                        {
                            lastSavedBytesLocal = localDownloaded;
                            doSave = true;
                        }
                    }

                    if (toReport >= 0)
                    {
                        var r = toReport;
                        var s = speedToReport;
                        SetUi(() =>
                        {
                            item.DownloadedBytes = r;
                            item.SpeedBytesPerSecond = s;
                        });
                    }

                    if (doSave)
                    {
                        _ = _repository.UpdateAsync(item);
                    }
                }, ct);

                await _repository.SaveChunkAsync(item.Id, range.Start, range.End);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        var finalBytes = localDownloaded;
        SetUi(() => item.DownloadedBytes = finalBytes);
    }

    private async Task DownloadChunkAsync(
        string url,
        FileStream fs,
        (long Start, long End) range,
        Action<long> onBytes,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(range.Start, range.End);

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode != HttpStatusCode.PartialContent &&
            response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"Il server ha risposto con {response.StatusCode}, previsto 206.");
        }

        if (response.StatusCode == HttpStatusCode.OK && range.Start > 0)
        {
            _logger.LogWarning(
                "Server ha risposto 200 invece di 206 per range {Start}-{End} di {Url}. " +
                "Il file potrebbe risultare corrotto.",
                range.Start, range.End, url);
        }

        await using var source = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[81920];
        int read;
        long offset = range.Start;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            lock (fs)
            {
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Write(buffer, 0, read);
            }
            offset += read;
            onBytes(read);
        }
    }

    private static List<(long Start, long End)> CalculateRanges(long totalSize)
    {
        var ranges = new List<(long, long)>();
        long pos = 0;
        while (pos < totalSize)
        {
            long end = Math.Min(pos + ChunkSize - 1, totalSize - 1);
            ranges.Add((pos, end));
            pos = end + 1;
        }
        return ranges;
    }
    // ============================================================
    //  HLS / M3U8 → FFmpeg
    // ============================================================

    private static bool IsHlsUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        var lower = url.ToLowerInvariant();
        return lower.Contains(".m3u8");
    }

    // Bitrate tipico anime streaming (Sub ITA 1080p): ~1.2 Mbps = 150 KB/s.
    // Usato per stimare la dimensione reale del file durante il download HLS
    // (ffmpeg non conosce la dimensione finale in anticipo per HLS).
    private const long EstimatedBitrateBytesPerSecond = 150_000;

    private async Task DownloadHlsAsync(DownloadItem item, CancellationToken ct)
    {
        var ffmpegPath = _ffmpegLocator.GetFfmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath))
        {
            throw new InvalidOperationException(
                "FFmpeg non trovato. Non è possibile scaricare stream HLS (.m3u8).");
        }

        _logger.LogInformation(
            "[HLS] Download via FFmpeg: {Url} → {Dest}", item.Url, item.DestinationPath);

        // 1. Durata via ffprobe
        var totalDuration = await ProbeDurationAsync(item.Url, ct);
        if (totalDuration.HasValue)
        {
            // Stima dimensione finale = durata × bitrate stimato
            var estimatedBytes = (long)(totalDuration.Value.TotalSeconds * EstimatedBitrateBytesPerSecond);
            SetUi(() => item.TotalBytes = estimatedBytes);
            _logger.LogInformation(
                "[HLS] Durata: {Dur} → dimensione stimata: {MB} MB",
                totalDuration.Value, estimatedBytes / 1024 / 1024);
        }
        else
        {
            _logger.LogWarning("[HLS] ffprobe non disponibile: la barra partirà solo dopo il primo progress");
        }

        var tempPath = item.DestinationPath + ".part.mp4";
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(item.Url);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-bsf:a");
        psi.ArgumentList.Add("aac_adtstoasc");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(tempPath);

        using var process = new Process { StartInfo = psi };
        var startTime = DateTime.UtcNow;

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            // out_time_us=N → ffmpeg ha processato N microsecondi di video
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                var valStr = e.Data.Substring("out_time_us=".Length);
                if (long.TryParse(valStr, out var microSeconds) && microSeconds > 0)
                {
                    // Converti microsecondi → secondi → byte stimati
                    var seconds = microSeconds / 1_000_000.0;
                    var estimatedDownloadedBytes = (long)(seconds * EstimatedBitrateBytesPerSecond);

                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    var speed = elapsed > 0 ? (long)(estimatedDownloadedBytes / elapsed) : 0;

                    SetUi(() =>
                    {
                        item.DownloadedBytes = estimatedDownloadedBytes;
                        item.SpeedBytesPerSecond = speed;
                    });
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            // Fallback: se ffprobe non ha dato la durata
            if (!totalDuration.HasValue)
            {
                var dm = DurationRegex.Match(e.Data);
                if (dm.Success)
                {
                    var dur = new TimeSpan(
                        int.Parse(dm.Groups[1].Value),
                        int.Parse(dm.Groups[2].Value),
                        int.Parse(dm.Groups[3].Value));
                    totalDuration = dur;

                    var estimatedBytes = (long)(dur.TotalSeconds * EstimatedBitrateBytesPerSecond);
                    SetUi(() => item.TotalBytes = estimatedBytes);
                    _logger.LogInformation("[HLS] Durata (stderr): {Dur} → stimata: {MB} MB",
                        dur, estimatedBytes / 1024 / 1024);
                }
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });

        await process.WaitForExitAsync(CancellationToken.None);

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException();

        if (process.ExitCode != 0)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw new InvalidOperationException(
                $"FFmpeg è terminato con codice {process.ExitCode}.");
        }

        if (!File.Exists(tempPath))
            throw new InvalidOperationException("FFmpeg non ha prodotto il file di output.");

        if (File.Exists(item.DestinationPath))
            File.Delete(item.DestinationPath);
        File.Move(tempPath, item.DestinationPath);

        // Alla fine: aggiorno alla dimensione reale (piccolo aggiustamento vs stima)
        var finalSize = new FileInfo(item.DestinationPath).Length;
        SetUi(() =>
        {
            item.TotalBytes = finalSize;
            item.DownloadedBytes = finalSize;
            item.SpeedBytesPerSecond = 0;
            item.Status = DownloadStatus.Completed;
        });

        _logger.LogInformation(
            "[HLS] Completato: {Path} ({Size} MB, stimato era {Est} MB)",
            item.DestinationPath, finalSize / 1024 / 1024,
            (long)(totalDuration?.TotalSeconds * EstimatedBitrateBytesPerSecond ?? 0) / 1024 / 1024);
    }

    private async Task<TimeSpan?> ProbeDurationAsync(string url, CancellationToken ct)
    {
        var ffmpegPath = _ffmpegLocator.GetFfmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath)) return null;

        var dir = Path.GetDirectoryName(ffmpegPath);
        var ffprobe = Path.Combine(dir ?? "", "ffprobe.exe");
        if (!File.Exists(ffprobe)) return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            psi.ArgumentList.Add(url);

            using var proc = new Process { StartInfo = psi };
            proc.Start();

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0) return null;

            if (double.TryParse(output.Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        catch { }
        return null;
    }

    private static readonly Regex DurationRegex = new(
        @"Duration:\s*(\d+):(\d+):(\d+)\.(\d+)",
        RegexOptions.Compiled);

}