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

    // Il sito risponde 429 "troppe richieste" (e poi 403 per qualche minuto) se riceve
    // molte richieste ravvicinate: al massimo 2 alla volta, distanziate, e dopo un rifiuto
    // tutte aspettano. Il metodo col browser (pesante) lavora un episodio alla volta.
    private readonly SemaphoreSlim _requestGate = new(2, 2);
    private readonly SemaphoreSlim _browserGate = new(1, 1);
    private readonly object _paceLock = new();
    private long _nextRequestAt;
    private const int RequestSpacingMs = 350;
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
        var active = _proxyProvider.GetActive(ProviderKeys.AnimeSaturn);   // prima usava per errore il proxy di AnimeWorld
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
                .Select(r => new AnimeSearchResult { Name = r.Name, Link = r.Link, PosterUrl = r.PosterUrl })
                .ToList();
        }

        var results = new List<AnimeSearchResult>();
        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await InitializeAsync();   // riapre il browser se è stato chiuso (es. cambio di proxy)
        if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");

        const int maxPages = 5;
        var firstUrl = $"{BaseUrl}/filter?key={Uri.EscapeDataString(query)}";

        try
        {
            // Pagina 1: risultati + paginazione (il sito usa /filter/2?key=...); le altre si caricano insieme
            var (first, lastPage, template) = await LoadSearchPageAsync(firstUrl, readPaging: true, ct);
            var pages = new List<List<string[]>> { first };

            var more = Math.Min(lastPage, maxPages);
            if (first.Count > 0 && more > 1 && template != null)
            {
                var rest = await Task.WhenAll(Enumerable.Range(2, more - 1)
                    .Select(n => LoadSearchPageAsync(template.Replace("{n}", n.ToString()), readPaging: false, ct)));
                pages.AddRange(rest.Select(r => r.Rows));
            }

            foreach (var rows in pages)
            foreach (var row in rows)
            {
                var href = row[0];
                var name = row[1];
                if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(name)) continue;
                if (href.StartsWith("/")) href = BaseUrl + href;

                var cleanHref = href.Split('?')[0];
                if (!cleanHref.Contains("/anime/", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seenLinks.Add(cleanHref)) continue;

                string? posterUrl = row[2];
                if (!string.IsNullOrEmpty(posterUrl) && posterUrl.StartsWith("/")) posterUrl = BaseUrl + posterUrl;
                if (string.IsNullOrEmpty(posterUrl) ||
                    posterUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    posterUrl.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
                    posterUrl = null;

                results.Add(new AnimeSearchResult { Name = name, Link = cleanHref, PosterUrl = posterUrl });
            }

            _logger.LogInformation("[SATURN] Ricerca completata: {Count} risultati da {Pages} pagine", results.Count, pages.Count);

            // Salva in cache
            _searchCache[cacheKey] = (DateTime.UtcNow, results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SATURN] Errore ricerca '{Query}'", query);
            throw;
        }
        return results;
    }

    /// <summary>
    /// Una pagina di risultati: [link, titolo, locandina] per ogni anime. Con <paramref name="readPaging"/>
    /// legge anche l'ultima pagina e un modello di indirizzo con "{n}" al posto del numero di pagina.
    /// </summary>
    private async Task<(List<string[]> Rows, int LastPage, string? Template)> LoadSearchPageAsync(string url, bool readPaging, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var page = await LightPage.OpenAsync(_context!);
        try
        {
            _logger.LogInformation("[SATURN] Ricerca: {Url}", url);
            var navResponse = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20000
            });
            if (navResponse != null && navResponse.Status >= 400)
                throw new InvalidOperationException($"HTTP {navResponse.Status} {navResponse.StatusText}");

            try
            {
                await page.WaitForSelectorAsync("a.ac",
                    new PageWaitForSelectorOptions { Timeout = readPaging ? 8000 : 4000, State = WaitForSelectorState.Attached });
            }
            catch
            {
                return (new List<string[]>(), 1, null);
            }

            var rows = await page.EvaluateAsync<string[][]>(@"() => [...document.querySelectorAll('a.ac')].map(a => {
                const t = a.querySelector('h3.ac__title');
                const box = a.closest('.col-6') || a.closest('.card') || a.parentElement;
                const img = a.querySelector('img') || (box ? box.querySelector('img') : null);
                return [a.getAttribute('href') || '',
                        t ? (t.innerText || t.textContent || '').trim() : '',
                        img ? (img.getAttribute('data-src') || img.getAttribute('src') || '') : ''];
            })");

            int last = 1;
            string? template = null;
            if (readPaging)
            {
                // Link di pagina: /filter/2?key=... (oppure ?page=2 se il sito cambia formato)
                var paging = await page.EvaluateAsync<string[]>(@"() => {
                    let max = 1, tpl = '';
                    const links = document.querySelectorAll('nav[aria-label=""Paginazione""] a[href], .pagination a[href], a.page-num[href]');
                    for (const a of links) {
                        const u = new URL(a.getAttribute('href'), location.href);
                        let m = u.pathname.match(/^(.*\/filter\/)(\d+)\/?$/), n = 0;
                        if (m) { n = parseInt(m[2], 10); if (!tpl) tpl = u.origin + m[1] + '{n}' + u.search; }
                        else if (u.searchParams.get('page')) {
                            n = parseInt(u.searchParams.get('page'), 10);
                            if (!tpl) { u.searchParams.set('page', '__N__'); tpl = u.href.replace('__N__', '{n}'); }
                        }
                        if (n > max) max = n;
                    }
                    return [String(max), tpl];
                }");
                if (paging.Length == 2)
                {
                    int.TryParse(paging[0], out last);
                    template = string.IsNullOrEmpty(paging[1]) ? null : paging[1];
                }
            }
            return (rows.ToList(), Math.Max(1, last), template);
        }
        finally
        {
            try { await page.CloseAsync(); } catch { }
        }
    }

    // ============================================================
    //  EPISODI
    // ============================================================

    public async Task<List<Episode>> GetEpisodesAsync(string animeUrl, CancellationToken ct = default)
    {
        var episodes = new List<Episode>();
        await InitializeAsync();   // riapre il browser se è stato chiuso (es. cambio di proxy)
        if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");

        var page = await LightPage.OpenAsync(_context);
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
                    new PageWaitForSelectorOptions { Timeout = 8000, State = WaitForSelectorState.Attached });
            }
            catch
            {
                _logger.LogWarning("[SATURN] Nessun episodio trovato in {Url}", animeUrl);
                return episodes;
            }

            // Rimosso WaitForTimeoutAsync(500)

            // Tutti gli episodi in una sola chiamata (prima: 2 chiamate per episodio)
            var rows = await page.EvalOnSelectorAllAsync<string[][]>("a.ep-tile",
                "els => els.map(e => [e.getAttribute('href') || '', e.getAttribute('title') || ''])");
            _logger.LogInformation("[SATURN] Trovati {Count} link episodi", rows.Length);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                var href = row[0];
                if (string.IsNullOrEmpty(href)) continue;
                if (href.StartsWith("/")) href = BaseUrl + href;

                var cleanHref = href.Split('?')[0];
                bool isEpisode = EpisodeLinkRegex.IsMatch(cleanHref)
                    || cleanHref.Contains("/episode/", StringComparison.OrdinalIgnoreCase);
                if (!isEpisode || !seen.Add(cleanHref)) continue;

                var titleAttr = row[1];
                var epNum = "";
                var match = EpisodeTitleRegex.Match(titleAttr);
                if (match.Success) epNum = match.Groups[1].Value;
                else
                {
                    var m2 = EpisodeLinkRegex.Match(cleanHref);
                    if (m2.Success) epNum = m2.Groups[1].Value;
                }

                episodes.Add(new Episode
                {
                    Number = epNum,
                    Title = string.IsNullOrEmpty(titleAttr) ? $"Episodio {epNum}" : titleAttr,
                    Link = cleanHref
                });
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
        await InitializeAsync();   // riapre il browser se è stato chiuso (es. cambio di proxy)
        if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");

        // Via veloce: due richieste leggere (pagina dell'episodio + playlist del player),
        // senza aprire pagine nel browser. Se il sito cambia, si ripiega sul browser.
        try
        {
            var fast = await GetVideoUrlFastAsync(episodePageUrl, ct);
            if (!string.IsNullOrEmpty(fast)) return fast;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("[SATURN] Via veloce non riuscita ({Msg}), uso il browser", ex.Message);
        }

        await _browserGate.WaitAsync(ct);
        try { return await GetVideoUrlViaBrowserAsync(episodePageUrl, ct); }
        finally { _browserGate.Release(); }
    }

    private static readonly System.Text.RegularExpressions.Regex EpisodeLinkRegex = new(@"/ep-(\d+)$",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex EpisodeTitleRegex = new(@"Episodio\s+(\d+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex EmbedRegex = new(
        @"(https?:)?//(?<host>[a-z0-9.\-]+)/embed/(?<id>\d+)\?token=(?<token>[A-Za-z0-9]+)&(?:amp;)?expires=(?<exp>\d+)",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Link del video senza browser: la pagina dell'episodio contiene l'indirizzo del player
    /// (id, token, scadenza); il player chiede a "/embed/{id}/playlist" il link, cifrato in
    /// base64 con XOR sul token. Le richieste passano dal contesto Playwright (stesso proxy,
    /// cookie e user agent).
    /// </summary>
    /// <summary>
    /// GET verso il sito rispettando il suo limite: poche richieste alla volta e distanziate;
    /// su 429/403/503 si aspetta (quanto chiede il sito, o sempre di più) e si riprova.
    /// </summary>
    private async Task<IAPIResponse?> ThrottledGetAsync(IAPIRequestContext api, string url, APIRequestContextOptions options, CancellationToken ct)
    {
        IAPIResponse? last = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            await _requestGate.WaitAsync(ct);
            try
            {
                await PaceAsync(ct);
                last = await api.GetAsync(url, options);
            }
            finally { _requestGate.Release(); }

            if (last.Status is not (429 or 403 or 503)) return last;

            var wait = TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, attempt - 1)));
            if (last.Headers.TryGetValue("retry-after", out var ra) && int.TryParse(ra, out var seconds) && seconds is > 0 and < 120)
                wait = TimeSpan.FromSeconds(seconds);
            _logger.LogInformation("[SATURN] Il sito chiede di rallentare (HTTP {Status}): attendo {Sec}s (tentativo {N}/5)",
                last.Status, (int)wait.TotalSeconds, attempt);

            // Rallentano tutte le richieste, non solo questa
            lock (_paceLock)
                _nextRequestAt = Math.Max(_nextRequestAt, Environment.TickCount64 + (long)wait.TotalMilliseconds);
            await Task.Delay(wait, ct);
        }
        return last;
    }

    private async Task PaceAsync(CancellationToken ct)
    {
        long wait;
        lock (_paceLock)
        {
            var now = Environment.TickCount64;
            var at = Math.Max(now, _nextRequestAt);
            _nextRequestAt = at + RequestSpacingMs;
            wait = at - now;
        }
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
    }

    private async Task<string?> GetVideoUrlFastAsync(string episodePageUrl, CancellationToken ct)
    {
        var playerUrl = episodePageUrl.Replace("/episode/", "/anime/", StringComparison.OrdinalIgnoreCase);
        var api = _context!.APIRequest;

        var pageResponse = await ThrottledGetAsync(api, playerUrl, new APIRequestContextOptions { Timeout = 15000 }, ct);
        if (pageResponse == null || !pageResponse.Ok) return null;
        var html = await pageResponse.TextAsync();
        ct.ThrowIfCancellationRequested();

        var m = EmbedRegex.Match(html);
        if (!m.Success) return null;

        var host = m.Groups["host"].Value;
        var id = m.Groups["id"].Value;
        var token = m.Groups["token"].Value;
        var expires = m.Groups["exp"].Value;
        var embedUrl = $"https://{host}/embed/{id}?token={token}&expires={expires}";

        var playlist = await ThrottledGetAsync(api, $"https://{host}/embed/{id}/playlist?token={token}&expires={expires}",
            new APIRequestContextOptions
            {
                Timeout = 15000,
                Headers = new Dictionary<string, string> { ["Referer"] = embedUrl, ["X-Requested-With"] = "XMLHttpRequest" }
            }, ct);
        if (playlist == null || !playlist.Ok) return null;

        using var doc = System.Text.Json.JsonDocument.Parse(await playlist.TextAsync());
        if (!doc.RootElement.TryGetProperty("d", out var dataEl) || dataEl.GetString() is not { Length: > 0 } data)
            return null;

        var bytes = Convert.FromBase64String(data);
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) chars[i] = (char)(bytes[i] ^ token[i % token.Length]);
        var src = new string(chars);

        // Episodi su YouTube o risposte inattese: li gestisce il browser
        if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return null;

        _logger.LogInformation("[SATURN] ✅ Video (via veloce): {Url}", src);
        return src;
    }

    private async Task<string?> GetVideoUrlViaBrowserAsync(string episodePageUrl, CancellationToken ct)
    {
        var playerUrl = episodePageUrl.Replace("/episode/", "/anime/", StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("[SATURN] Video da (player, browser): {Url}", playerUrl);
        await InitializeAsync();   // riapre il browser se è stato chiuso (es. cambio di proxy)
        if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");

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

                    // === FIX: scarta GIF/PNG/tracking pixel anche se content-type mente ===
                    if (IsDefinitelyNotVideo(lower)) return;

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

                        // === FIX: scarta GIF/PNG/tracking pixel anche se content-type mente ===
                        if (IsDefinitelyNotVideo(lower)) return;

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

    /// <summary>
    /// True se l'URL è sicuramente NON un video, anche se il content-type
    /// dichiara "video/*" per errore (es. tracking pixel ping.gif).
    /// </summary>
    private static bool IsDefinitelyNotVideo(string lowerUrl)
    {
        // Estensioni immagine/svg
        if (lowerUrl.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
            lowerUrl.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            lowerUrl.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            lowerUrl.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
            lowerUrl.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
            lowerUrl.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
            lowerUrl.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
            return true;

        // Tracking pixel / beacon noti
        if (lowerUrl.Contains("ping.gif") ||
            lowerUrl.Contains("/ping") ||
            lowerUrl.Contains("beacon") ||
            lowerUrl.Contains("tracker") ||
            lowerUrl.Contains("analytics") ||
            lowerUrl.Contains("1x1") ||
            lowerUrl.Contains("pixel"))
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
        const int parallelism = 4;   // il ritmo verso il sito lo decide ThrottledGetAsync

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