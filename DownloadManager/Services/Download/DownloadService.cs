using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
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

    private const long ChunkSize = 10 * 1024 * 1024;
    private const int ParallelChunksPerFile = 4;
    private const long SaveThreshold = 5 * 1024 * 1024;
    private const int UiThrottleMs = 150;

    public DownloadService(
        ILogger<DownloadService> logger,
        DownloadRepository repository,
        IProxyProvider proxyProvider)
    {
        _logger = logger;
        _repository = repository;

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
}