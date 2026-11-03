using DownloadManager.Helpers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Proxy;
using DownloadManager.Services.Streaming.StreamingCommunity;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Download;

public partial class DownloadService : IDownloadService
{
    private readonly ILogger<DownloadService> _logger;
    private readonly DownloadRepository _repository;
    private readonly HttpClient _http;
    private readonly FfmpegLocator _ffmpegLocator;
    private readonly YtDlpLocator _ytDlpLocator;
    private readonly IProxyProvider _proxyProvider;
    private readonly ProxyService _proxyService;
    private readonly IServiceProvider _services;
    private readonly DownloadManager.Persistence.SettingsService _settings;

    private const long ChunkSize = 10 * 1024 * 1024;
    private const int ParallelChunksPerFile = 4;
    private const long SaveThreshold = 5 * 1024 * 1024;
    private const int UiThrottleMs = 500;
    private const long EstimatedBitrateBytesPerSecond = 150_000;

    public DownloadService(
        ILogger<DownloadService> logger,
        DownloadRepository repository,
        IProxyProvider proxyProvider,
        ProxyService proxyService,
        FfmpegLocator ffmpegLocator,
        YtDlpLocator ytDlpLocator,
        IServiceProvider services,
        DownloadManager.Persistence.SettingsService settings)
    {
        _services = services;
        _settings = settings;
        _logger = logger;
        _repository = repository;
        _proxyService = proxyService;
        _proxyProvider = proxyProvider;
        _ffmpegLocator = ffmpegLocator;
        _ytDlpLocator = ytDlpLocator;

        var handler = new HttpClientHandler
        {
            MaxConnectionsPerServer = 16,
            AutomaticDecompression = DecompressionMethods.All,
            Proxy = new DynamicWebProxy(proxyProvider),
            UseProxy = true
        };

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36");
    }

    private static void SetUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    // ============================================================
    //  WRAPPER CON RETRY PROXY
    // ============================================================

    /// <summary>Item in download in questo momento: lo stesso episodio non deve mai girare due volte.</summary>
    private static readonly HashSet<int> _runningItems = new();

    public async Task DownloadItemAsync(DownloadItem item, CancellationToken ct = default)
    {
        lock (_runningItems)
        {
            if (!_runningItems.Add(item.Id))
                throw new InvalidOperationException("Questo download è già in corso.");
        }
        try
        {
            await DownloadItemWithRetryAsync(item, ct);
        }
        finally
        {
            lock (_runningItems) _runningItems.Remove(item.Id);
        }
    }

    private async Task DownloadItemWithRetryAsync(DownloadItem item, CancellationToken ct)
    {
        const int maxAttempts = 3;
        const string providerKeyDefault = "Downloads";

        var providerKey = string.IsNullOrEmpty(item.ProviderKey)
            ? providerKeyDefault
            : item.ProviderKey;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                await DownloadItemInternalAsync(item, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                !ct.IsCancellationRequested &&
                IsRetryableNetworkError(ex) &&
                attempt < maxAttempts - 1)
            {
                var proxy = _proxyProvider.GetActive(providerKey);

                if (proxy == null)
                {
                    _logger.LogWarning(ex,
                        "[RETRY] Errore di rete ma nessun proxy attivo per {P}. Abort.",
                        providerKey);
                    throw;
                }

                _logger.LogWarning(ex,
                    "[RETRY] Errore di rete (tentativo {N}/{Max}). " +
                    "Marco {Host}:{Port} come failed e ruoto per {P}...",
                    attempt + 1, maxAttempts, proxy.Host, proxy.Port, providerKey);

                try
                {
                    await _proxyService.MarkFailedAndRotateAsync(proxy, providerKey);
                }
                catch (Exception rotateEx)
                {
                    _logger.LogWarning(rotateEx, "[RETRY] Errore durante rotazione proxy");
                }

                SetUi(() =>
                {
                    item.ErrorMessage = $"Retry {attempt + 1}/{maxAttempts}: cambio proxy...";
                    item.ProxyRetryCount = attempt + 1;
                });

                await Task.Delay(2000, ct);
            }
        }
    }

    // ============================================================
    //  DOWNLOAD VERO E PROPRIO
    // ============================================================

    private async Task DownloadItemInternalAsync(DownloadItem item, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) throw new OperationCanceledException();

        // App chiusa durante la lavorazione (copia in MP4, conversione...): il file è già scaricato,
        // si riprende da lì invece di riscaricare (e di richiedere di nuovo il link al sito)
        if (FindFinishedDownload(item) is { } finished)
        {
            _logger.LogInformation("[RIPRESA] {File}: download già finito, riprendo la lavorazione", Path.GetFileName(finished));
            DeleteProcessingLeftovers(finished);
            SetUi(() =>
            {
                item.Status = DownloadStatus.Downloading;
                item.ErrorMessage = "Ripresa della lavorazione...";
                item.DestinationPath = finished;
                item.DownloadedBytes = item.TotalBytes;
                item.SpeedBytesPerSecond = 0;
            });
            long size = item.TotalBytes;
            try { size = new FileInfo(finished).Length; } catch { }
            await PostProcessAsync(item, finished, size, ct);
            return;
        }

        if (item.UrlExtractor != null && ShouldReextractUrl(item.Url))
        {
            SetUi(() =>
            {
                item.Status = DownloadStatus.Downloading;
                item.ErrorMessage = "Estrazione URL in corso...";
            });

            try
            {
                var fresh = await item.UrlExtractor(ct);
                if (string.IsNullOrEmpty(fresh))
                    throw new InvalidOperationException("UrlExtractor vuoto.");
                item.Url = fresh;

                if (item.Category == "anime" || item.ProviderKey == "CB01")
                {
                    var names = _services.GetService(typeof(FileNameBuilder)) as FileNameBuilder;
                    if (names != null)
                    {
                        // CB01 arriva dalla tab Film/Serie: senza override userebbe cartella e pattern Anime
                        string? pattern = item.ProviderKey == "CB01"
                            ? (item.IsSeries
                                ? _settings.Current.Cb01SeriesNamingPattern
                                : _settings.Current.Cb01MovieNamingPattern)
                            : null;
                        var path = names.BuildDestinationPath(item, fresh, item.EpisodeTitle, pattern);
                        SetUi(() => item.DestinationPath = path);
                    }
                }

                if (item.AudioExtractor != null)
                {
                    try
                    {
                        var audioUrl = await item.AudioExtractor(ct);
                        if (!string.IsNullOrEmpty(audioUrl))
                            item.AudioUrl = audioUrl;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "AudioExtractor fallito");
                    }
                }

                SetUi(() => item.ErrorMessage = string.Empty);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Errore estrazione URL: {ex.Message}", ex);
            }
        }

        if (item.CookieExtractor != null && string.IsNullOrEmpty(item.SessionCookies))
        {
            try
            {
                var cookies = await item.CookieExtractor(ct);
                if (!string.IsNullOrEmpty(cookies))
                    item.SessionCookies = cookies;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CookieExtractor fallito, procedo senza cookie");
            }
        }

        var directory = Path.GetDirectoryName(item.DestinationPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        SetUi(() =>
        {
            item.Status = DownloadStatus.Downloading;
            item.ErrorMessage = string.Empty;
        });

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

        var tempPath = item.DestinationPath + ".part";

        if (File.Exists(tempPath) && File.Exists(item.DestinationPath))
        {
            var existingSize = new FileInfo(item.DestinationPath).Length;   // può essere già convertito
            SetUi(() =>
            {
                item.TotalBytes = existingSize;
                item.Status = DownloadStatus.Completed;
                item.DownloadedBytes = existingSize;
                item.SpeedBytesPerSecond = 0;
            });
            await _repository.ClearChunksAsync(item.Id);
            return;
        }

        var sw = Stopwatch.StartNew();

        if (!supportsRange) await DownloadSequentialAsync(item, ct);
        else await DownloadParallelAsync(item, totalSize, sw, ct);

        if (!File.Exists(tempPath))
            throw new InvalidOperationException("Download incompleto: file .part mancante.");

        var actualSize = new FileInfo(tempPath).Length;
        if (actualSize != totalSize)
            throw new InvalidOperationException(
                $"File scaricato {actualSize} byte invece di {totalSize}.");

        if (File.Exists(item.DestinationPath)) File.Delete(item.DestinationPath);
        File.Move(tempPath, item.DestinationPath);

        await PostProcessAsync(item, item.DestinationPath, totalSize, ct);
    }


    /// <summary>
    /// Lavorazione del file a fine download (CB01): copia in MP4, controllo e riparazione, audio
    /// italiano per primo, conversione scelta. Si riprende da qui anche dopo un riavvio dell'app.
    /// </summary>
    /// <param name="downloadedPath">File scaricato (o il suo .mp4, se la copia era già fatta).</param>
    /// <param name="totalSize">Dimensione scaricata (si usa se il file non si può leggere).</param>
    private async Task PostProcessAsync(DownloadItem item, string downloadedPath, long totalSize, CancellationToken ct)
    {
        // CB01: "Originale" consegna il file così com'è; tutte le altre scelte partono da un .mp4
        var finalPath = downloadedPath;
        var conversion = item.Conversion.FileProcessing();   // Maxstream HD → come Copia in MP4 / audio AAC
        var processCb01 = item.ProviderKey == "CB01" && conversion != VideoConversion.Original;
        var rewritten = false;   // file già riscritto da ffmpeg (audio italiano già per primo)
        if (processCb01 &&
            !finalPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            var ffmpeg = _ffmpegLocator.GetFfmpegPath();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                _logger.LogWarning("[MP4] ffmpeg non trovato: {File} resta nel formato originale", finalPath);
            }
            else
            {
                SetUi(() => item.ErrorMessage = "Copia in MP4...");
                // "Copia in MP4": solo cambio di contenitore, nessuna ricodifica (l'audio AAC è "MP4 + audio AAC")
                var mp4 = await Mp4Remuxer.ConvertAsync(ffmpeg, finalPath, _logger, ct,
                    copyOnly: conversion == VideoConversion.Mp4Copy);
                if (mp4 != null)
                {
                    finalPath = mp4;
                    rewritten = true;
                    SetUi(() => item.DestinationPath = mp4);
                }
                SetUi(() => item.ErrorMessage = string.Empty);
            }
        }

        // CB01: alcuni video HEVC (sia .mp4 originali sia .mkv convertiti) hanno la configurazione
        // del codec vuota e nessun lettore li apre. Si ricostruisce, senza ricodificare, e si riordina il file.
        if (processCb01 &&
            finalPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            SetUi(() => item.ErrorMessage = "Controllo del file video...");
            var pathToCheck = finalPath;
            var repaired = await Task.Run(() => HevcConfigRepair.TryRepair(pathToCheck, _logger), ct);
            if (repaired)
            {
                var ffmpegPath = _ffmpegLocator.GetFfmpegPath();
                if (!string.IsNullOrEmpty(ffmpegPath))
                {
                    SetUi(() => item.ErrorMessage = "Riparazione del video...");
                    rewritten = await Mp4Remuxer.RewriteAsync(ffmpegPath, pathToCheck, _logger, ct);
                }
            }

            // "Copia in MP4" su un .mp4: si riscrive solo se l'italiano non è la prima traccia audio.
            // Le tracce si copiano così come sono (nessuna ricodifica: per l'audio AAC c'è "MP4 + audio AAC").
            var ffmpegForOrder = _ffmpegLocator.GetFfmpegPath();
            if (conversion == VideoConversion.Mp4Copy && !string.IsNullOrEmpty(ffmpegForOrder))
            {
                bool needsOrder = !rewritten && await AudioTracks.NeedsReorderAsync(ffmpegForOrder, pathToCheck, ct);
                if (needsOrder)
                {
                    SetUi(() => item.ErrorMessage = "Audio italiano per primo...");
                    await Mp4Remuxer.RewriteAsync(ffmpegForOrder, pathToCheck, _logger, ct);
                }
            }
            SetUi(() => item.ErrorMessage = string.Empty);
        }

        // CB01: conversione scelta dall'utente (audio AAC o H.264 per i player web), anche per i .mp4
        if (processCb01 && conversion != VideoConversion.Mp4Copy &&
            finalPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            var ffmpegPath = _ffmpegLocator.GetFfmpegPath();
            if (string.IsNullOrEmpty(ffmpegPath))
                _logger.LogWarning("[CONVERSIONE] ffmpeg non trovato: {File} resta com'è", finalPath);
            else
                await VideoTranscoder.ConvertAsync(ffmpegPath, finalPath, conversion, _logger,
                    msg => SetUi(() => item.ErrorMessage = msg), ct, () => item.IsPriority, item.Id);
            SetUi(() => item.ErrorMessage = string.Empty);
        }

        // Dimensione vera del file finito: conversioni e riscritture (audio italiano per primo,
        // AAC/H.264) la cambiano rispetto a quella scaricata
        long finalSize = totalSize;
        try { if (File.Exists(finalPath)) finalSize = new FileInfo(finalPath).Length; } catch { }
        SetUi(() =>
        {
            item.TotalBytes = finalSize;
            item.Status = DownloadStatus.Completed;
            item.DownloadedBytes = finalSize;
            item.SpeedBytesPerSecond = 0;
        });
        await _repository.ClearChunksAsync(item.Id);
    }

    /// <summary>
    /// Download CB01 già finito in una sessione precedente (app chiusa durante la lavorazione):
    /// nessun file parziale ".part" e il file completo presente, così com'è o già copiato in .mp4.
    /// Restituisce il file da cui riprendere la lavorazione, oppure null (si scarica normalmente).
    /// </summary>
    private static string? FindFinishedDownload(DownloadItem item)
    {
        if (item.ProviderKey != "CB01" || item.Conversion.FileProcessing() == VideoConversion.Original) return null;
        var dest = item.DestinationPath;
        // TotalBytes > 0: il download era partito (un episodio nuovo con un file omonimo non conta)
        if (string.IsNullOrEmpty(dest) || item.TotalBytes <= 0 || File.Exists(dest + ".part")) return null;
        if (File.Exists(dest)) return dest;
        var mp4 = Path.ChangeExtension(dest, ".mp4");
        return File.Exists(mp4) ? mp4 : null;
    }

    /// <summary>File temporanei di una copia, riscrittura o conversione interrotta.</summary>
    private void DeleteProcessingLeftovers(string file)
    {
        var mp4 = Path.ChangeExtension(file, ".mp4");
        foreach (var leftover in new[] { mp4 + ".converting", mp4 + ".rewrite.mp4", mp4 + ".converting.mp4" })
        {
            try
            {
                if (!File.Exists(leftover)) continue;
                File.Delete(leftover);
                _logger.LogInformation("[RIPRESA] Tolto il file temporaneo di una lavorazione interrotta: {File}", Path.GetFileName(leftover));
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[RIPRESA] File temporaneo non eliminato: {File}", leftover); }
        }
    }

    // ============================================================
    //  HELPER RETRY
    // ============================================================

    private static bool IsRetryableNetworkError(Exception ex)
    {
        var msg = ex.Message;

        if (msg.Contains("FFmpeg terminato con codice -", StringComparison.OrdinalIgnoreCase))
            return true;
            
        if (msg.Contains("yt-dlp terminato con codice -", StringComparison.OrdinalIgnoreCase))
            return true;

        if (ex is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode.HasValue)
            {
                var code = (int)httpEx.StatusCode.Value;
                if (code == 403 || code == 407 || code == 429 ||
                    code == 502 || code == 503 || code == 504)
                    return true;
            }
            if (httpEx.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                httpEx.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
                httpEx.Message.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
                httpEx.Message.Contains("name resolution", StringComparison.OrdinalIgnoreCase) ||
                httpEx.Message.Contains("proxy", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (ex is TaskCanceledException) return true;

        if (msg.Contains("FFmpeg è terminato", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("FFmpeg terminato", StringComparison.OrdinalIgnoreCase))
        {
            if (msg.Contains("403") || msg.Contains("407") ||
                msg.Contains("429") || msg.Contains("503") ||
                msg.Contains("timeout", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (msg.Contains("yt-dlp", StringComparison.OrdinalIgnoreCase) &&
            (msg.Contains("403") || msg.Contains("407") ||
             msg.Contains("429") || msg.Contains("503") ||
             msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("HTTP Error", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (msg.Contains("proxy", StringComparison.OrdinalIgnoreCase) &&
            (msg.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("refus", StringComparison.OrdinalIgnoreCase) ||
             msg.Contains("timeout", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    // ============================================================
    //  SEQUENTIAL / PARALLEL (HTTP non-HLS)
    // ============================================================

    private async Task DownloadSequentialAsync(DownloadItem item, CancellationToken ct)
    {
        var tempPath = item.DestinationPath + ".part";
        using var response = await _http.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 81920, true);

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
        var tempPath = item.DestinationPath + ".part";

        // I pezzi segnati come fatti valgono solo se il file parziale c'è ed è della dimensione giusta:
        // altrimenti si riparte da zero (prima il file veniva ricreato vuoto e quei pezzi restavano a zeri)
        bool resumable = File.Exists(tempPath) && new FileInfo(tempPath).Length == totalSize;
        if (doneChunks.Count > 0 && !resumable)
        {
            _logger.LogWarning("[DOWNLOAD] {File}: file parziale mancante o diverso, riparto da zero", Path.GetFileName(item.DestinationPath));
            await _repository.ClearChunksAsync(item.Id);
            doneChunks = await _repository.GetChunksAsync(item.Id);
        }

        var todoRanges = allRanges
            .Where(r => !doneChunks.Any(d => d.Start <= r.Start && d.End >= r.End))
            .ToList();

        long alreadyDownloaded = doneChunks.Sum(c => c.End - c.Start + 1);
        SetUi(() => item.DownloadedBytes = alreadyDownloaded);

        // OpenOrCreate: riprendendo, i pezzi già scaricati restano nel file
        await using var fs = new FileStream(tempPath, doneChunks.Count > 0 ? FileMode.OpenOrCreate : FileMode.Create,
            FileAccess.Write, FileShare.Write, 81920, true);
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

                    if (doSave) _ = _repository.UpdateAsync(item);
                }, ct);

                await _repository.SaveChunkAsync(item.Id, range.Start, range.End);
            }
            finally { semaphore.Release(); }
        }).ToList();

        await Task.WhenAll(tasks);

        var finalBytes = localDownloaded;
        SetUi(() => item.DownloadedBytes = finalBytes);
    }

    /// <summary>Un pezzo senza dati per questo tempo si considera bloccato e si riprende.</summary>
    private static readonly TimeSpan ChunkIdleTimeout = TimeSpan.FromSeconds(60);
    private const int ChunkAttempts = 4;

    /// <summary>
    /// Scarica un pezzo del file. Se la connessione si blocca (il server smette di mandare dati senza
    /// chiuderla: con molti download insieme da Maxstream capitava e il download restava fermo per
    /// sempre) o si interrompe, si riprende dal punto raggiunto, fino a <see cref="ChunkAttempts"/> volte.
    /// </summary>
    private async Task DownloadChunkAsync(string url, FileStream fs,
        (long Start, long End) range, Action<long> onBytes, CancellationToken ct)
    {
        // Primo byte non ancora scritto: lo aggiorna ogni lettura, anche se poi il tentativo fallisce
        var pos = new System.Runtime.CompilerServices.StrongBox<long>(range.Start);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadRangeOnceAsync(url, fs, pos, range.End, onBytes, ct);
                if (pos.Value > range.End) return;
                throw new IOException($"Pezzo incompleto: ricevuti {pos.Value - range.Start} byte su {range.End - range.Start + 1}.");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < ChunkAttempts && IsTransientChunkError(ex))
            {
                _logger.LogInformation("[DOWNLOAD] Pezzo {Start}-{End}: {Msg}. Riprendo da {Offset} (tentativo {N}/{Max})",
                    range.Start, range.End, ex.Message, pos.Value, attempt + 1, ChunkAttempts);
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    private static bool IsTransientChunkError(Exception ex) => ex switch
    {
        TimeoutException or IOException => true,
        HttpRequestException h => h.StatusCode is null or HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout
                                  || (int)h.StatusCode >= 500,
        _ => false
    };

    /// <summary>Una richiesta per il tratto [pos, end]; pos avanza con ogni blocco scritto.</summary>
    private async Task DownloadRangeOnceAsync(string url, FileStream fs, System.Runtime.CompilerServices.StrongBox<long> pos, long end,
        Action<long> onBytes, CancellationToken ct)
    {
        long from = pos.Value;
        // Annullato dall'utente oppure dal "cane da guardia": nessun dato per ChunkIdleTimeout
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(ChunkIdleTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(from, end);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);

            // Serve proprio il tratto chiesto (206): un 200 è il file intero dall'inizio, che scritto
            // a metà file lo rovinerebbe. Si accetta solo se il tratto parte da 0.
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
                throw new HttpRequestException($"Server ha risposto {(int)response.StatusCode} {response.StatusCode}.", null, response.StatusCode);
            if (response.StatusCode != HttpStatusCode.PartialContent &&
                !(response.StatusCode == HttpStatusCode.OK && from == 0))
                throw new InvalidOperationException(
                    $"Server ha risposto {response.StatusCode}, previsto 206.");
            if (response.StatusCode == HttpStatusCode.PartialContent &&
                response.Content.Headers.ContentRange is { From: { } start } && start != from)
                throw new InvalidOperationException($"Il server ha mandato il tratto da {start} invece che da {from}.");

            await using var source = await response.Content.ReadAsStreamAsync(idle.Token);

            var buffer = new byte[81920];
            int read;
            while (pos.Value <= end &&
                   (read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - pos.Value + 1)), idle.Token)) > 0)
            {
                lock (fs)
                {
                    fs.Seek(pos.Value, SeekOrigin.Begin);
                    fs.Write(buffer, 0, read);
                }
                pos.Value += read;
                onBytes(read);
                idle.CancelAfter(ChunkIdleTimeout);   // arrivano dati: il conto riparte
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Non l'ha chiesto l'utente: la connessione è rimasta ferma troppo a lungo
            throw new TimeoutException($"nessun dato da {ChunkIdleTimeout.TotalSeconds:0} s");
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
    //  HLS — Router yt-dlp → FFmpeg
    // ============================================================

    /// <summary>
    /// Decide se l'URL corrente è ancora valido o va ri-estratto.
    /// - Se è vuoto → ri-estrai
    /// - Se è un URL vixcloud con token `expires=` in scadenza (<60s) → ri-estrai
    /// - Altrimenti → riusa (resume)
    /// </summary>
    private static bool ShouldReextractUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return true;

        if (url.Contains("vixcloud", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("host-cdn", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(url, @"[?&]expires=(\d+)");
            if (m.Success && long.TryParse(m.Groups[1].Value, out var exp))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // Considera scaduto se mancano meno di 60 secondi
                if (exp - now < 60) return true;
            }
        }

        return false;
    }

    private static bool IsHlsUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return true;
        return Regex.IsMatch(url, @"vixcloud\.[a-z]+/playlist/\d+", RegexOptions.IgnoreCase);
    }

    private async Task DownloadHlsAsync(DownloadItem item, CancellationToken ct)
    {
        var isVixcloud = item.Url.Contains("vixcloud", StringComparison.OrdinalIgnoreCase);

        var ytDlpPath = _ytDlpLocator.GetYtDlpPath();
        if (!string.IsNullOrEmpty(ytDlpPath))
        {
            try
            {
                await DownloadHlsWithYtDlpAsync(item, ytDlpPath, ct);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[HLS] yt-dlp fallito");

                if (isVixcloud)
                {
                    _logger.LogError(
                        "[HLS] vixcloud: FFmpeg non può bypassare Cloudflare, " +
                        "salto il fallback e propago l'errore");
                    throw;
                }
            }
        }
        else
        {
            _logger.LogWarning("[HLS] yt-dlp non trovato, uso FFmpeg");
        }

        await DownloadHlsWithFfmpegAsync(item, ct);
    }

    // ============================================================
    //  HLS via yt-dlp
    // ============================================================

    private static async Task<bool> ProbeHasAudioAndVideoAsync(
        string filePath, string ffmpegDir, CancellationToken ct)
    {
        try
        {
            var ffprobe = Path.Combine(ffmpegDir, "ffprobe.exe");
            if (!File.Exists(ffprobe)) return false;

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
            psi.ArgumentList.Add("stream=codec_type");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("csv=p=0");
            psi.ArgumentList.Add(filePath);

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            ChildProcessJob.Add(proc);

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);

            var hasVideo = output.Contains("video", StringComparison.OrdinalIgnoreCase);
            var hasAudio = output.Contains("audio", StringComparison.OrdinalIgnoreCase);

            return hasVideo && hasAudio;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildYtDlpFormat(string qualityName)
    {
        if (string.IsNullOrEmpty(qualityName) ||
            qualityName.StartsWith("Auto", StringComparison.OrdinalIgnoreCase))
        {
            return "bv*+ba[language=ita]/bv*+ba/b";
        }

        var m = Regex.Match(qualityName, @"(\d{3,4})p");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var h) && h > 0)
        {
            return
                $"bv*[height<={h}]+ba[language=ita]/" +
                $"bv*[height<={h}]+ba/" +
                $"b[height<={h}]/b";
        }

        return "bv*+ba[language=ita]/bv*+ba/b";
    }

    // ============================================================
    //  HLS via FFmpeg (fallback)
    // ============================================================

    private async Task DownloadHlsWithFfmpegAsync(DownloadItem item, CancellationToken ct)
    {
        var ffmpegPath = _ffmpegLocator.GetFfmpegPath();
        if (string.IsNullOrEmpty(ffmpegPath))
            throw new InvalidOperationException("FFmpeg non trovato.");

        bool hasSeparateAudio = !string.IsNullOrEmpty(item.AudioUrl);

        _logger.LogInformation("[HLS-FFMPEG] Download: video={V}, audio={A}",
            item.Url, hasSeparateAudio ? item.AudioUrl : "(nel master)");

        var totalDuration = await ProbeDurationAsync(item.Url, ct);
        if (totalDuration.HasValue)
        {
            var estimatedBytes = (long)(totalDuration.Value.TotalSeconds * EstimatedBitrateBytesPerSecond);
            SetUi(() => item.TotalBytes = estimatedBytes);
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

        bool isVixcloud = item.Url.Contains("vixcloud", StringComparison.OrdinalIgnoreCase)
            || (item.AudioUrl?.Contains("vixcloud", StringComparison.OrdinalIgnoreCase) ?? false);

        var providerKey = string.IsNullOrEmpty(item.ProviderKey)
            ? "Downloads"
            : item.ProviderKey;

        var proxy = _proxyProvider.GetActive(providerKey);

        if (proxy != null && !isVixcloud)
        {
            var proxyUrl = BuildFfmpegProxyUrl(proxy);
            psi.ArgumentList.Add("-http_proxy");
            psi.ArgumentList.Add(proxyUrl);
        }

        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostats");
        psi.ArgumentList.Add("-progress");
        psi.ArgumentList.Add("pipe:1");

        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(item.Url);

        if (hasSeparateAudio)
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(item.AudioUrl ?? "");
        }

        if (hasSeparateAudio)
        {
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("0:v:0");
            psi.ArgumentList.Add("-map");
            psi.ArgumentList.Add("1:a:0");
        }

        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("copy");
        psi.ArgumentList.Add("-bsf:a");
        psi.ArgumentList.Add("aac_adtstoasc");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(tempPath);

        using var process = new Process { StartInfo = psi };
        var startTime = DateTime.UtcNow;
        var stderrBuffer = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            if (e.Data.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                var valStr = e.Data.Substring("out_time_us=".Length);
                if (long.TryParse(valStr, out var microSeconds) && microSeconds > 0)
                {
                    var seconds = microSeconds / 1_000_000.0;
                    var est = (long)(seconds * EstimatedBitrateBytesPerSecond);
                    var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                    var speed = elapsed > 0 ? (long)(est / elapsed) : 0;
                    SetUi(() =>
                    {
                        item.DownloadedBytes = est;
                        item.SpeedBytesPerSecond = speed;
                    });
                }
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;

            lock (stderrBuffer)
            {
                if (stderrBuffer.Length < 4000)
                    stderrBuffer.AppendLine(e.Data);
            }

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
                    var est = (long)(dur.TotalSeconds * EstimatedBitrateBytesPerSecond);
                    SetUi(() => item.TotalBytes = est);
                }
            }
        };

        process.Start();
        ChildProcessJob.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        });

        await process.WaitForExitAsync(CancellationToken.None);

        if (ct.IsCancellationRequested) throw new OperationCanceledException();

        if (process.ExitCode != 0)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

            var err = stderrBuffer.ToString();
            if (err.Length > 2000) err = err.Substring(err.Length - 2000);

            _logger.LogError("[HLS-FFMPEG] EXIT {Code}. Ultime righe:\n{Err}",
                process.ExitCode, err);

            throw new InvalidOperationException(
                $"FFmpeg terminato con codice {process.ExitCode}.");
        }

        if (!File.Exists(tempPath))
            throw new InvalidOperationException("FFmpeg non ha prodotto l'output.");

        if (File.Exists(item.DestinationPath)) File.Delete(item.DestinationPath);
        File.Move(tempPath, item.DestinationPath);

        var finalSize = new FileInfo(item.DestinationPath).Length;
        SetUi(() =>
        {
            item.TotalBytes = finalSize;
            item.DownloadedBytes = finalSize;
            item.SpeedBytesPerSecond = 0;
            item.Status = DownloadStatus.Completed;
        });
    }

    // ============================================================
    //  HELPER FFMPEG
    // ============================================================

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
            ChildProcessJob.Add(proc);

            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0) return null;

            if (double.TryParse(output.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(seconds);
        }
        catch { }
        return null;
    }

    private static readonly Regex DurationRegex = new(
        @"Duration:\s*(\d+):(\d+):(\d+)\.(\d+)",
        RegexOptions.Compiled);

    private static string BuildFfmpegProxyUrl(ProxyConfig p)
    {
        var scheme = p.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";

        string auth = "";
        if (p.Auth == ProxyAuthType.UserPassword && !string.IsNullOrEmpty(p.Username))
        {
            var u = Uri.EscapeDataString(p.Username);
            var pw = Uri.EscapeDataString(p.Password ?? "");
            auth = $"{u}:{pw}@";
        }

        return $"{scheme}://{auth}{p.Host}:{p.Port}";
    }

    // ============================================================
    //  HELPER COOKIE
    // ============================================================

    private static string SanitizeNetscapeCookies(string raw)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Netscape HTTP Cookie File");

        if (string.IsNullOrEmpty(raw)) return sb.ToString();

        foreach (var rawLine in raw.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ', '\t');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("#") && !line.StartsWith("#HttpOnly_")) continue;

            var work = line;
            if (work.StartsWith("#HttpOnly_"))
                work = work.Substring("#HttpOnly_".Length);

            var parts = work.Split('\t');
            if (parts.Length < 7) continue;

            var domain = parts[0].Trim().TrimStart(':').Trim();
            if (string.IsNullOrEmpty(domain)) continue;
            if (domain.StartsWith(":")) continue;
            if (!domain.StartsWith(".")) domain = "." + domain;

            var path = parts[2].Trim();
            if (string.IsNullOrEmpty(path) || !path.StartsWith("/")) path = "/";

            var secure = parts[3].Trim().ToUpperInvariant();
            if (secure != "TRUE" && secure != "FALSE") secure = "TRUE";

            var expiryRaw = parts[4].Trim();
            var expiry = long.TryParse(expiryRaw, out var e) && e > 0
                ? e
                : DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds();

            var name = parts[5].Trim();
            var value = parts[6];
            if (string.IsNullOrEmpty(name)) continue;

            sb.AppendLine($"{domain}\tTRUE\t{path}\t{secure}\t{expiry}\t{name}\t{value}");
        }

        return sb.ToString();
    }
}