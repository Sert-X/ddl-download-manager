using System.Collections.Concurrent;
using DownloadManager.Models;
using DownloadManager.Services.Anime;
using DownloadManager.Services.Proxy;
using DownloadManager.Services.Domains;
using ManagedCode.Playwright.Stealth;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace DownloadManager.Services.Anime.AnimeSaturn;

public class AnimeSaturnService : IAnimeSaturnService
{
    public string Name => "AnimeSaturn";
    public string BaseUrl => _domainResolver.GetBaseUrl(Name);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private readonly DomainResolver _domainResolver;
    private readonly ILogger<AnimeSaturnService> _logger;
    private readonly IProxyProvider _proxyProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private string _currentProxyKey = "(none)";

    // === Cache ricerca: evita di rifare tutto se l'utente ricercha la stessa cosa ===
    private readonly ConcurrentDictionary<string, (DateTime At, List<AnimeSearchResult> Results)> _searchCache = new();
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);

    public AnimeSaturnService(ILogger<AnimeSaturnService> logger, IProxyProvider proxyProvider, DomainResolver domainResolver)
    {
        _logger = logger;
        _proxyProvider = proxyProvider;
        _domainResolver = domainResolver;
    }

    public async Task NotifyProxyChangedAsync()
    {
        if (_context == null) return;
        _logger.LogInformation("[SATURN] Proxy cambiato: reset browser");
        await DisposeAsync();
    }

    public async Task InitializeAsync(bool headless = true)
    {
        var active = _proxyProvider.GetActive(ProviderKeys.AnimeWorld);
        var proxyKey = active == null
            ? "(none)"
            : $"{active.Protocol}|{active.Host}|{active.Port}|{active.Username}";

        if (_context != null && _currentProxyKey != proxyKey)
        {
            _logger.LogInformation("[SATURN] Proxy cambiato ({Old} → {New}): re-init",
                _currentProxyKey, proxyKey);
            await DisposeAsync();
        }

        if (_context != null) return;

        await _gate.WaitAsync();
        try
        {
            if (_context != null) return;

            _logger.LogInformation("[SATURN] Init Playwright (proxy: {Proxy})...", proxyKey);

            _playwright = await Playwright.CreateAsync();
            var (browser, defaultContext) = await _playwright.Chromium.LaunchStealthAsync();
            _browser = browser;

            try { await defaultContext.CloseAsync(); } catch { }
            try { await defaultContext.DisposeAsync(); } catch { }

            var contextOptions = new BrowserNewContextOptions
            {
                UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                            "AppleWebKit/537.36 (KHTML, like Gecko) " +
                            "Chrome/120.0.0.0 Safari/537.36",
                ViewportSize = new ViewportSize { Width = 1920, Height = 1080 },
                Locale = "it-IT",
                TimezoneId = "Europe/Rome"
            };

            if (active != null)
            {
                var scheme = active.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
                var proxy = new Microsoft.Playwright.Proxy
                {
                    Server = $"{scheme}://{active.Host}:{active.Port}"
                };
                if (active.Auth == ProxyAuthType.UserPassword)
                {
                    proxy.Username = active.Username;
                    proxy.Password = active.Password;
                }
                contextOptions.Proxy = proxy;
            }

            _context = await _browser.NewContextAsync(contextOptions);
            _currentProxyKey = proxyKey;

            _logger.LogInformation("[SATURN] Playwright inizializzato.");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ============================================================
    //  RICERCA — con paginazione + cache
    // ============================================================

    public async Task<List<AnimeSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        // === Cache hit? ===
        var cacheKey = query.Trim().ToLowerInvariant();
        if (_searchCache.TryGetValue(cacheKey, out var cached) &&
            DateTime.UtcNow - cached.At < SearchCacheTtl)
        {
            _logger.LogInformation("[SATURN] Ricerca '{Query}' da cache ({N} risultati)",
                query, cached.Results.Count);
            // Clone per evitare mutazioni dall'UI
            return cached.Results
                .Select(r => new AnimeSearchResult { Name = r.Name, Link = r.Link })
                .ToList();
        }

        var results = new List<AnimeSearchResult>();
        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");

        var page = await _context.NewPageAsync();
        try
        {
            int pageNum = 1;
            const int maxPages = 5;

            while (pageNum <= maxPages)
            {
                ct.ThrowIfCancellationRequested();

                var searchUrl = pageNum == 1
                    ? $"{BaseUrl}/filter?key={Uri.EscapeDataString(query)}"
                    : $"{BaseUrl}/filter?key={Uri.EscapeDataString(query)}&page={pageNum}";

                _logger.LogInformation("[SATURN] Ricerca pagina {Page}: {Url}", pageNum, searchUrl);

                // === FIX: DOMContentLoaded invece di NetworkIdle ===
                var navResponse = await page.GotoAsync(searchUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 20000
                });

                if (navResponse != null && navResponse.Status >= 400)
                {
                    throw new InvalidOperationException(
                        $"HTTP {navResponse.Status} {navResponse.StatusText}");
                }

                // Il selettore è già reattivo (WaitForSelectorAsync)
                try
                {
                    await page.WaitForSelectorAsync("a.ac",
                        new PageWaitForSelectorOptions { Timeout = 8000 });
                }
                catch
                {
                    _logger.LogWarning("[SATURN] Nessun risultato su pagina {Page}", pageNum);
                    break;
                }

                // Rimosso WaitForTimeoutAsync(500): non necessario, i dati sono già nel DOM

                var anchors = await page.QuerySelectorAllAsync("a.ac");
                _logger.LogInformation("[SATURN] Pagina {Page}: trovati {Count} anchor",
                    pageNum, anchors?.Count ?? 0);

                if (anchors == null || anchors.Count == 0) break;

                int addedThisPage = 0;

                foreach (var a in anchors)
                {
                    try
                    {
                        var href = await a.GetAttributeAsync("href") ?? "";
                        if (string.IsNullOrEmpty(href)) continue;
                        if (href.StartsWith("/")) href = BaseUrl + href;

                        var cleanHref = href.Split('?')[0];
                        if (!cleanHref.Contains("/anime/", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!seenLinks.Add(cleanHref)) continue;

                        var titleEl = await a.QuerySelectorAsync("h3.ac__title");
                        var name = titleEl != null
                            ? (await titleEl.InnerTextAsync()).Trim()
                            : "";

                        if (string.IsNullOrEmpty(name)) continue;

                        // === ESTRAZIONE POSTER ===
                        string? posterUrl = null;
                        try
                        {
                            var img = await a.QuerySelectorAsync("img");
                            if (img == null)
                            {
                                // Cerca nel contenitore padre (a volte il layout ha img fuori dall'anchor)
                                var parent = await a.EvaluateHandleAsync("el => el.closest('.col-6') || el.closest('.card') || el.parentElement");
                                if (parent != null)
                                {
                                    var parentEl = parent.AsElement();
                                    if (parentEl != null)
                                        img = await parentEl.QuerySelectorAsync("img");
                                }
                            }

                            if (img != null)
                            {
                                posterUrl = await img.GetAttributeAsync("data-src")
                                        ?? await img.GetAttributeAsync("src");

                                if (!string.IsNullOrEmpty(posterUrl) && posterUrl.StartsWith("/"))
                                    posterUrl = BaseUrl + posterUrl;

                                if (!string.IsNullOrEmpty(posterUrl) &&
                                    (posterUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                                    posterUrl.Contains("placeholder", StringComparison.OrdinalIgnoreCase)))
                                    posterUrl = null;
                            }
                        }
                        catch { }

                        results.Add(new AnimeSearchResult
                        {
                            Name = name,
                            Link = cleanHref,
                            PosterUrl = posterUrl
                        });
                    }
                    catch { }
                }

                _logger.LogInformation("[SATURN] Pagina {Page}: aggiunti {Added} risultati (totale {Total})",
                    pageNum, addedThisPage, results.Count);

                if (addedThisPage == 0) break;

                var hasNext = await page.QuerySelectorAsync("a[rel='next'], a.next, li.next a");
                if (hasNext == null) break;

                pageNum++;
            }

            _logger.LogInformation("[SATURN] Ricerca completata: {Count} risultati", results.Count);

            // Salva in cache
            _searchCache[cacheKey] = (DateTime.UtcNow, results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SATURN] Errore ricerca '{Query}'", query);
            throw;
        }
        finally
        {
            try { await page.CloseAsync(); } catch { }
        }
        return results;
    }

    // ============================================================
    //  EPISODI
    // ============================================================

    public async Task<List<Episode>> GetEpisodesAsync(string animeUrl, CancellationToken ct = default)
    {
        var episodes = new List<Episode>();
        if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");

        var page = await _context.NewPageAsync();
        try
        {
            _logger.LogInformation("[SATURN] Episodi da: {Url}", animeUrl);

            // === FIX: DOMContentLoaded invece di NetworkIdle ===
            await page.GotoAsync(animeUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20000
            });

            try
            {
                await page.WaitForSelectorAsync("a.ep-tile",
                    new PageWaitForSelectorOptions { Timeout = 8000 });
            }
            catch
            {
                _logger.LogWarning("[SATURN] Nessun episodio trovato in {Url}", animeUrl);
                return episodes;
            }

            // Rimosso WaitForTimeoutAsync(500)

            var episodeElements = await page.QuerySelectorAllAsync("a.ep-tile");
            _logger.LogInformation("[SATURN] Trovati {Count} link episodi", episodeElements?.Count ?? 0);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (episodeElements != null)
            {
                foreach (var el in episodeElements)
                {
                    try
                    {
                        var href = await el.GetAttributeAsync("href") ?? "";
                        if (string.IsNullOrEmpty(href)) continue;
                        if (href.StartsWith("/")) href = BaseUrl + href;

                        var cleanHref = href.Split('?')[0];

                        bool isEpisode = System.Text.RegularExpressions.Regex.IsMatch(
                            cleanHref, @"/ep-\d+$")
                            || cleanHref.Contains("/episode/", StringComparison.OrdinalIgnoreCase);

                        if (!isEpisode) continue;
                        if (!seen.Add(cleanHref)) continue;

                        var titleAttr = await el.GetAttributeAsync("title") ?? "";

                        var epNum = "";
                        var match = System.Text.RegularExpressions.Regex.Match(
                            titleAttr, @"Episodio\s+(\d+)");
                        if (match.Success)
                            epNum = match.Groups[1].Value;
                        else
                        {
                            var m2 = System.Text.RegularExpressions.Regex.Match(
                                cleanHref, @"/ep-(\d+)$");
                            if (m2.Success) epNum = m2.Groups[1].Value;
                        }

                        episodes.Add(new Episode
                        {
                            Number = epNum,
                            Title = string.IsNullOrEmpty(titleAttr) ? $"Episodio {epNum}" : titleAttr,
                            Link = cleanHref
                        });
                    }
                    catch { }
                }
            }

            episodes = episodes
                .OrderBy(e => int.TryParse(e.Number, out var n) ? n : int.MaxValue)
                .ToList();
        }
        finally
        {
            await page.CloseAsync();
        }

        return episodes;
    }

    // ============================================================
    //  URL VIDEO (singolo)
    // ============================================================

    public async Task<string?> GetVideoUrlAsync(string episodePageUrl, CancellationToken ct = default)
    {
        if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");

        var playerUrl = episodePageUrl.Replace("/episode/", "/anime/", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("[SATURN] Video da (player): {Url}", playerUrl);

        var page = await _context.NewPageAsync();
        try
        {
            var videoTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnResponse(object? _, IResponse response)
            {
                try
                {
                    var url = response.Url;
                    var lower = url.ToLowerInvariant();
                    if (IsTrackerOrAd(lower)) return;

                    var contentType = response.Headers.TryGetValue("content-type", out var ct2) ? ct2 : "";
                    bool isVideoContentType = contentType.Contains("video/", StringComparison.OrdinalIgnoreCase)
                        || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                        || contentType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase);

                    if (IsVideoUrl(lower) || isVideoContentType)
                    {
                        if (videoTcs.TrySetResult(url))
                            _logger.LogInformation("[SATURN] ✅ Video intercettato: {Url}", url);
                    }
                }
                catch { }
            }

            page.Response += OnResponse;

            await page.GotoAsync(playerUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 15000
            });

            // 1) <video> già nel DOM?
            var immediate = await page.EvaluateAsync<string?>(@"
                () => {
                    let s = document.querySelector('video[src]');
                    if (s && s.src) return s.src;
                    s = document.querySelector('video source[src]');
                    if (s && s.src) return s.src;
                    return null;
                }
            ");

            if (!string.IsNullOrEmpty(immediate))
            {
                page.Response -= OnResponse;
                _logger.LogInformation("[SATURN] ✅ Video trovato (DOM main): {Url}", immediate);
                return immediate;
            }

            // 2) Aspetta iframe in modo reattivo (max 4s)
            var iframeSrc = await WaitForIframeAsync(page, 4000);
            if (string.IsNullOrEmpty(iframeSrc))
            {
                var raced = await Task.WhenAny(videoTcs.Task, Task.Delay(4000, ct));
                page.Response -= OnResponse;
                if (raced == videoTcs.Task && videoTcs.Task.IsCompletedSuccessfully)
                    return videoTcs.Task.Result;
                _logger.LogWarning("[SATURN] ❌ Nessun video trovato (no iframe)");
                return null;
            }

            _logger.LogInformation("[SATURN] Iframe trovato: {Src}", iframeSrc);

            // 3) Apri iframe e cattura video
            var iframePage = await _context.NewPageAsync();
            try
            {
                var iframeTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnIframeResponse(object? _, IResponse response)
                {
                    try
                    {
                        var url = response.Url;
                        var lower = url.ToLowerInvariant();
                        if (IsTrackerOrAd(lower)) return;

                        var contentType = response.Headers.TryGetValue("content-type", out var ct3) ? ct3 : "";
                        bool isVideoContentType = contentType.Contains("video/", StringComparison.OrdinalIgnoreCase)
                            || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
                            || contentType.Contains("dash+xml", StringComparison.OrdinalIgnoreCase);

                        if (IsVideoUrl(lower) || isVideoContentType)
                        {
                            if (iframeTcs.TrySetResult(url))
                                _logger.LogInformation("[SATURN] ✅ Video intercettato (iframe): {Url}", url);
                        }
                    }
                    catch { }
                }

                iframePage.Response += OnIframeResponse;

                await iframePage.GotoAsync(iframeSrc, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 15000
                });

                // Timeout ridotto: 6s invece di 8s
                var raced = await Task.WhenAny(iframeTcs.Task, Task.Delay(6000, ct));
                if (raced == iframeTcs.Task && iframeTcs.Task.IsCompletedSuccessfully
                    && !string.IsNullOrEmpty(iframeTcs.Task.Result))
                {
                    iframePage.Response -= OnIframeResponse;
                    return iframeTcs.Task.Result;
                }

                // Fallback: <video> nel DOM
                var domUrl = await iframePage.EvaluateAsync<string?>(@"
                    () => {
                        let s = document.querySelector('video[src]');
                        if (s && s.src) return s.src;
                        s = document.querySelector('video source[src]');
                        if (s && s.src) return s.src;
                        s = document.querySelector('source[src*=""mp4""]');
                        if (s && s.src) return s.src;
                        s = document.querySelector('source[src*=""m3u8""]');
                        if (s && s.src) return s.src;
                        return null;
                    }
                ");

                iframePage.Response -= OnIframeResponse;
                if (!string.IsNullOrEmpty(domUrl))
                {
                    _logger.LogInformation("[SATURN] ✅ Video trovato (DOM iframe): {Url}", domUrl);
                    return domUrl;
                }

                _logger.LogWarning("[SATURN] ❌ Nessun video trovato nell'iframe");
                return null;
            }
            finally
            {
                page.Response -= OnResponse;
                await iframePage.CloseAsync();
            }
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Attende l'iframe con WaitForSelectorAsync nativo (reattivo, no polling).
    /// </summary>
    private static async Task<string?> WaitForIframeAsync(IPage page, int timeoutMs)
    {
        try
        {
            await page.WaitForSelectorAsync(
                "iframe#watch-iframe, iframe[src*='play.saturncdn.net'], iframe[src*='/embed/']",
                new PageWaitForSelectorOptions { Timeout = timeoutMs });
        }
        catch
        {
            return null;
        }

        return await page.EvaluateAsync<string?>(@"
            () => {
                let f = document.querySelector('iframe#watch-iframe');
                if (f && f.src) return f.src;
                const iframes = document.querySelectorAll('iframe[src]');
                for (const fr of iframes) {
                    if (fr.src && (fr.src.includes('play.saturncdn.net') || fr.src.includes('/embed/')))
                        return fr.src;
                }
                return null;
            }
        ");
    }

    // ============================================================
    //  HELPER
    // ============================================================

    private static bool IsTrackerOrAd(string lowerUrl)
    {
        return lowerUrl.Contains("googletagmanager")
            || lowerUrl.Contains("google-analytics")
            || lowerUrl.Contains("doubleclick")
            || lowerUrl.Contains("sponsorarrange")
            || lowerUrl.Contains("facebook.com")
            || lowerUrl.Contains("fbcdn")
            || lowerUrl.Contains("taboola")
            || lowerUrl.Contains("outbrain")
            || lowerUrl.Contains("criteo")
            || lowerUrl.Contains("adservice")
            || lowerUrl.Contains("adsystem")
            || lowerUrl.Contains("googlesyndication")
            || lowerUrl.Contains("gstatic")
            || lowerUrl.Contains("a-ads.com")
            || lowerUrl.Contains("acceptable.a-ads");
    }

    private static bool IsVideoUrl(string lowerUrl)
    {
        if (lowerUrl.Contains(".m3u8") || lowerUrl.Contains(".mpd"))
            return true;

        if (lowerUrl.Contains(".mp4")
            || lowerUrl.Contains(".webm")
            || lowerUrl.Contains(".mkv")
            || lowerUrl.Contains(".m4v")
            || lowerUrl.Contains(".mov"))
            return true;

        if (lowerUrl.Contains("videoplayback"))
            return true;

        return false;
    }

    // ============================================================
    //  URL VIDEO (batch)
    // ============================================================

    public async Task<Dictionary<string, string>> GetVideoUrlsBatchAsync(
        List<Episode> episodes, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        if (episodes.Count == 0) return result;

        // === FIX: 5 paralleli invece di 3 ===
        const int parallelism = 5;

        _logger.LogInformation("[SATURN] Batch: {Count} episodi ({P} paralleli)",
            episodes.Count, parallelism);

        var gate = new SemaphoreSlim(parallelism);
        var syncLock = new object();
        int completed = 0;

        var tasks = episodes.Select(async ep =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested) return;

                var url = await GetVideoUrlAsync(ep.Link, ct);
                if (!string.IsNullOrEmpty(url))
                {
                    lock (syncLock) result[ep.Link] = url;
                }
                Interlocked.Increment(ref completed);
                _logger.LogInformation("[SATURN] Batch: {Done}/{Total}",
                    Volatile.Read(ref completed), episodes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SATURN] Errore batch su {Link}", ep.Link);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);

        _logger.LogInformation("[SATURN] Batch completato: {Found}/{Total}", result.Count, episodes.Count);
        return result;
    }

    // ============================================================
    //  DISPOSE
    // ============================================================

    public async Task DisposeAsync()
    {
        if (_disposed && _context == null) return;

        await _gate.WaitAsync();
        try
        {
            try { if (_context != null) await _context.DisposeAsync(); } catch { }
            try { if (_browser != null) await _browser.DisposeAsync(); } catch { }
            try { _playwright?.Dispose(); } catch { }

            _context = null;
            _browser = null;
            _playwright = null;
            _disposed = false;

            // Invalida cache ricerca (i risultati potrebbero non essere più raggiungibili)
            _searchCache.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }
}