using DownloadManager.Models;
using DownloadManager.Services.Proxy;
using DownloadManager.Services.Domains;
using ManagedCode.Playwright.Stealth;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace DownloadManager.Services.Anime.AnimeWorld;

public class AnimeWorldService : IAnimeWorldService
{
    public string Name => "AnimeWorld";
    public string BaseUrl => _domainResolver.GetBaseUrl(Name);

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private readonly DomainResolver _domainResolver;
    private readonly ILogger<AnimeWorldService> _logger;
    private readonly IProxyProvider _proxyProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private string _currentProxyKey = "(none)";

    public AnimeWorldService(ILogger<AnimeWorldService> logger, IProxyProvider proxyProvider,DomainResolver domainResolver)
    {
        _logger = logger;
        _proxyProvider = proxyProvider;
        _domainResolver = domainResolver;
    }

    public async Task NotifyProxyChangedAsync()
    {
        if (_context == null) return; // niente da fare

        _logger.LogInformation("Proxy cambiato: reset del browser Playwright");
        await DisposeAsync();
    }

    public async Task InitializeAsync(bool headless = true)
    {
        var active = _proxyProvider.GetActive(ProviderKeys.AnimeWorld);
        var proxyKey = active == null
            ? "(none)"
            : $"{active.Protocol}|{active.Host}|{active.Port}|{active.Username}";

        // Se il browser è già avviato ma il proxy è cambiato, ricrea tutto.
        if (_context != null && _currentProxyKey != proxyKey)
        {
            _logger.LogInformation(
                "Proxy cambiato ({Old} → {New}): re-init browser Playwright",
                _currentProxyKey, proxyKey);
            await DisposeAsync();
        }

        if (_context != null) return;

        await _gate.WaitAsync();
        try
        {
            if (_context != null) return;

            _logger.LogInformation("Inizializzazione Playwright (proxy: {Proxy})...", proxyKey);

            _playwright = await Playwright.CreateAsync();

            // 1) Lancia stealth → restituisce browser + context di default (senza proxy).
            var (browser, defaultContext) = await _playwright.Chromium.LaunchStealthAsync();
            _browser = browser;

            // 2) Chiudi il context di default: non ha il proxy, ci serve un context nuovo.
            try { await defaultContext.CloseAsync(); } catch { }
            try { await defaultContext.DisposeAsync(); } catch { }

            // 3) Crea un nuovo context con impostazioni realistiche + proxy.
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

                _logger.LogInformation(
                    "Proxy applicato al context: {Server} (auth: {Auth})",
                    proxy.Server,
                    active.Auth == ProxyAuthType.UserPassword ? active.Username : "nessuna");
            }
            else
            {
                _logger.LogInformation("Context Playwright senza proxy (connessione diretta)");
            }

            _context = await _browser.NewContextAsync(contextOptions);
            _currentProxyKey = proxyKey;

            _logger.LogInformation("Playwright inizializzato correttamente.");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ============================================================
    //  RICERCA
    // ============================================================

    public async Task<List<AnimeSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        await InitializeAsync();   // riapre il browser se è stato chiuso (es. cambio di proxy)
        await _gate.WaitAsync(ct);
        try
        {
            var results = new List<AnimeSearchResult>();
            var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");

            var page = await _context.NewPageAsync();
            try
            {
                _logger.LogInformation("[AW] Ricerca HTML per '{Query}'", query);

                var searchUrl = $"{BaseUrl}/search?keyword={Uri.EscapeDataString(query)}";

                var navResponse = await page.GotoAsync(searchUrl, new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30000
                });

                if (navResponse != null && navResponse.Status >= 400)
                    throw new InvalidOperationException(
                        $"HTTP {navResponse.Status} {navResponse.StatusText}");

                // I risultati veri stanno SOLO in div.film-list: la pagina contiene anche
                // riquadri laterali (classifica, consigliati) con le stesse classe "item"/"name",
                // che prima venivano presi come risultati anche per parole inesistenti.
                // Senza risultati, nella lista compare un avviso "alert-danger".
                try
                {
                    await page.WaitForSelectorAsync("div.film-list div.item, div.film-list .alert",
                        new PageWaitForSelectorOptions { Timeout = 10000 });
                }
                catch
                {
                    _logger.LogWarning("[AW] Nessun risultato trovato per '{Query}'", query);
                    return results;
                }

                if (await page.QuerySelectorAsync("div.film-list div.item") == null)
                {
                    _logger.LogInformation("[AW] Nessun risultato per '{Query}'", query);
                    return results;
                }

                var anchors = await page.QuerySelectorAllAsync("div.film-list div.item > div.inner > a.name");
                if (anchors == null || anchors.Count == 0)
                    anchors = await page.QuerySelectorAllAsync("div.film-list div.item a.name");

                _logger.LogInformation("[AW] Trovati {N} anchor", anchors?.Count ?? 0);

                if (anchors != null)
                {
                    foreach (var a in anchors)
                    {
                        try
                        {
                            var name = (await a.InnerTextAsync()).Trim();
                            var href = await a.GetAttributeAsync("href") ?? "";

                            if (string.IsNullOrEmpty(href)) continue;
                            if (href.StartsWith("/")) href = BaseUrl + href;

                            if (!href.Contains("/play/", StringComparison.OrdinalIgnoreCase) &&
                                !href.Contains("/anime/", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var key = href.Split('?')[0].ToLowerInvariant();
                            if (!seenLinks.Add(key)) continue;

                            // === ESTRAZIONE POSTER ===
                            string? posterUrl = null;
                            try
                            {
                                var img = await a.QuerySelectorAsync("img");
                                if (img == null)
                                {
                                    // Cerca nel contenitore padre
                                    var parent = await a.EvaluateHandleAsync(
                                        "el => el.closest('.item') || el.parentElement");
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
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "[AW] Errore poster per {Href}", href);
                            }

                            results.Add(new AnimeSearchResult
                            {
                                Name = name,
                                Link = href,
                                PosterUrl = posterUrl
                            });
                        }
                        catch { }
                    }
                }

                _logger.LogInformation("[AW] Trovati {N} risultati per '{Query}'",
                    results.Count, query);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AW] Errore ricerca '{Query}'", query);
                throw;
            }
            finally
            {
                try { await page.CloseAsync(); } catch { }
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ============================================================
    //  EPISODI
    // ============================================================

    public async Task<List<Episode>> GetEpisodesAsync(string animeUrl, CancellationToken ct = default)
    {
        await InitializeAsync();   // riapre il browser se è stato chiuso (es. cambio di proxy)
        await _gate.WaitAsync(ct);
        try
        {
            var episodes = new List<Episode>();
            if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");

            var page = await _context.NewPageAsync();
            try
            {
                _logger.LogInformation("Recupero episodi da: {Url}", animeUrl);
                await page.GotoAsync(animeUrl);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

                var episodeElements = await page.QuerySelectorAllAsync("li.episode > a");

                foreach (var element in episodeElements)
                {
                    var number = await element.GetAttributeAsync("data-episode-num") ?? "";
                    var href = await element.GetAttributeAsync("href") ?? "";

                    if (string.IsNullOrEmpty(href)) continue;
                    if (href.StartsWith("/")) href = BaseUrl + href;

                    episodes.Add(new Episode
                    {
                        Number = number,
                        Title = $"Episodio {number}",
                        Link = href
                    });
                }

                _logger.LogInformation("Trovati {Count} episodi", episodes.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante il recupero degli episodi da {Url}", animeUrl);
            }
            finally
            {
                await page.CloseAsync();
            }

            return episodes;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ============================================================
    //  URL VIDEO (singolo)
    // ============================================================

    public async Task<string?> GetVideoUrlAsync(string episodePageUrl, CancellationToken ct = default)
    {
        await InitializeAsync();   // riapre il browser se è stato chiuso (es. cambio di proxy)
        await _gate.WaitAsync(ct);
        try
        {
            if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");

            var page = await _context.NewPageAsync();

            try
            {
                await page.GotoAsync(episodePageUrl);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.WaitForTimeoutAsync(1500);

                var dataId = await page.EvaluateAsync<string?>(@"
                    () => {
                        const link = document.querySelector('li.episode > a.active')
                                  || document.querySelector('li.episode > a');
                        return link ? link.getAttribute('data-id') : null;
                    }
                ");

                if (string.IsNullOrEmpty(dataId))
                {
                    _logger.LogWarning("data-id non trovato nella pagina");
                    return null;
                }

                var apiUrl = $"/api/episode/info?id={dataId}&alt=0";
                var result = await page.EvaluateAsync<string?>(@"
                    async (apiUrl) => {
                        const response = await fetch(apiUrl, {
                            method: 'GET',
                            headers: {
                                'x-requested-with': 'XMLHttpRequest',
                                'accept': 'application/json, text/javascript, */*; q=0.01'
                            },
                            credentials: 'include'
                        });

                        if (!response.ok) return JSON.stringify({ error: response.status });
                        return await response.text();
                    }
                ", apiUrl);

                if (string.IsNullOrEmpty(result)) return null;

                using var doc = System.Text.Json.JsonDocument.Parse(result);

                if (doc.RootElement.TryGetProperty("error", out var errEl) &&
                    errEl.ValueKind != System.Text.Json.JsonValueKind.False)
                {
                    _logger.LogWarning("Errore API: {Error}", errEl.ToString());
                    return null;
                }

                if (doc.RootElement.TryGetProperty("grabber", out var grabberEl))
                {
                    var videoUrl = grabberEl.GetString();
                    _logger.LogInformation("URL video trovato: {VideoUrl}", videoUrl);
                    return videoUrl;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante il recupero dell'URL video");
                return null;
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // ============================================================
    //  URL VIDEO (batch)
    // ============================================================

    public async Task<Dictionary<string, string>> GetVideoUrlsBatchAsync(
        List<Episode> episodes, CancellationToken ct = default)
    {
        await InitializeAsync();   // riapre il browser se è stato chiuso (es. cambio di proxy)
        await _gate.WaitAsync(ct);
        try
        {
            var result = new Dictionary<string, string>();
            if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");
            if (episodes.Count == 0) return result;

            var ids = new List<(Episode Ep, string DataId)>();
            foreach (var ep in episodes)
            {
                var id = ExtractDataIdFromUrl(ep.Link);
                if (!string.IsNullOrEmpty(id))
                    ids.Add((ep, id));
            }

            if (ids.Count == 0)
            {
                _logger.LogWarning("Nessun data-id estratto dagli URL");
                return result;
            }

            _logger.LogInformation("Batch: {Count} episodi da risolvere", ids.Count);
            var idList = ids.Select(x => x.DataId).ToList();

            try
            {
                // Pagina già aperta sul sito: niente caricamento della home a ogni accodamento
                var page = await GetApiPageAsync(reload: false, ct);
                await PaceAsync(idList.Count, ct);
                var (found, throttled) = await ResolveIdsAsync(page, idList);

                // Nessun risultato senza rifiuti: il cookie di sicurezza del sito potrebbe non
                // essere pronto. Si ricarica la pagina aspettando che il sito abbia finito.
                if (found.Count == 0 && !throttled)
                {
                    _logger.LogInformation("Batch: nessun URL, ricarico la pagina del sito e riprovo");
                    page = await GetApiPageAsync(reload: true, ct);
                    await PaceAsync(idList.Count, ct);
                    (found, throttled) = await ResolveIdsAsync(page, idList);
                }

                // Il sito ha rifiutato (403/429 "troppe richieste"): si aspetta e si riprovano
                // solo quelli mancanti, con attese crescenti
                for (int round = 1; throttled && round <= 3; round++)
                {
                    var missing = idList.Where(i => !found.ContainsKey(i)).ToList();
                    if (missing.Count == 0) break;
                    var wait = TimeSpan.FromSeconds(4 * round * round);
                    _logger.LogInformation("Il sito chiede di rallentare: attendo {Sec}s e riprovo {N} episodi", (int)wait.TotalSeconds, missing.Count);
                    lock (_paceLock) _nextRequestAt = Math.Max(_nextRequestAt, Environment.TickCount64 + (long)wait.TotalMilliseconds);
                    await Task.Delay(wait, ct);
                    await PaceAsync(missing.Count, ct);
                    var (more, stillThrottled) = await ResolveIdsAsync(page, missing);
                    foreach (var kv in more) found[kv.Key] = kv.Value;
                    throttled = stillThrottled;
                }

                foreach (var (ep, id) in ids)
                    if (found.TryGetValue(id, out var url)) result[ep.Link] = url;

                _logger.LogInformation("Batch completato: {Found}/{Total}", result.Count, ids.Count);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante il batch");
                await CloseApiPageAsync();   // al prossimo giro si riparte da una pagina nuova
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Pagina del sito tenuta aperta per chiamare l'API degli episodi (serve l'origine del sito
    /// e i suoi cookie). Si apre una volta sola; immagini, video e font non vengono scaricati.
    /// </summary>
    private IPage? _apiPage;

    private async Task<IPage> GetApiPageAsync(bool reload, CancellationToken ct)
    {
        if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");
        ct.ThrowIfCancellationRequested();

        bool fresh = _apiPage == null || _apiPage.IsClosed;
        if (fresh)
        {
            _apiPage = await _context.NewPageAsync();
            await _apiPage.RouteAsync("**/*", route =>
                route.Request.ResourceType is "image" or "media" or "font"
                    ? route.AbortAsync()
                    : route.ContinueAsync());
        }

        if (fresh || reload || !_apiPage!.Url.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            await _apiPage!.GotoAsync(BaseUrl, new PageGotoOptions
            {
                // Al primo giro basta la pagina pronta; se l'API non risponde si aspetta il sito intero
                WaitUntil = reload ? WaitUntilState.NetworkIdle : WaitUntilState.DOMContentLoaded,
                Timeout = 30000
            });
        }
        return _apiPage!;
    }

    private async Task CloseApiPageAsync()
    {
        try { if (_apiPage != null) await _apiPage.CloseAsync(); } catch { }
        _apiPage = null;
    }

    /// <summary>Chiama l'API del sito per ogni id (10 alla volta) e restituisce id → URL video.</summary>
    // Ritmo verso l'API del sito: le richieste ravvicinate venivano rifiutate (403)
    private readonly object _paceLock = new();
    private long _nextRequestAt;

    /// <summary>Distanzia le richieste: circa 300 ms per episodio chiesto.</summary>
    private async Task PaceAsync(int requests, CancellationToken ct)
    {
        long wait;
        lock (_paceLock)
        {
            var now = Environment.TickCount64;
            var at = Math.Max(now, _nextRequestAt);
            _nextRequestAt = at + 300L * Math.Max(1, requests);
            wait = at - now;
        }
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait), ct);
    }

    /// <returns>id → URL trovati, e se il sito ha rifiutato qualche richiesta (403/429/503).</returns>
    private static async Task<(Dictionary<string, string> Found, bool Throttled)> ResolveIdsAsync(IPage page, List<string> ids)
    {
        var idsJson = System.Text.Json.JsonSerializer.Serialize(ids);
        var resultJson = await page.EvaluateAsync<string>(@"
                    async (idsJson) => {
                        const ids = JSON.parse(idsJson);
                        const results = {};
                        const chunkSize = 10;

                        for (let i = 0; i < ids.length; i += chunkSize) {
                            const chunk = ids.slice(i, i + chunkSize);
                            const promises = chunk.map(async (id) => {
                                try {
                                    const r = await fetch('/api/episode/info?id=' + id + '&alt=0', {
                                        credentials: 'include',
                                        headers: { 'x-requested-with': 'XMLHttpRequest' }
                                    });
                                    if (!r.ok) return [id, '#' + r.status];
                                    const j = await r.json();
                                    return [id, j.grabber || null];
                                } catch (e) {
                                    return [id, null];
                                }
                            });
                            const chunkResults = await Promise.all(promises);
                            for (const [id, url] of chunkResults) {
                                results[id] = url;
                            }
                        }

                        return JSON.stringify(results);
                    }
                ", idsJson);

        var found = new Dictionary<string, string>();
        bool throttled = false;
        using var doc = System.Text.Json.JsonDocument.Parse(resultJson);
        foreach (var id in ids)
        {
            if (!doc.RootElement.TryGetProperty(id, out var el) || el.ValueKind != System.Text.Json.JsonValueKind.String) continue;
            var value = el.GetString();
            if (string.IsNullOrEmpty(value)) continue;
            if (value.StartsWith('#'))
            {
                if (value is "#403" or "#429" or "#503") throttled = true;
                continue;
            }
            found[id] = value;
        }
        return (found, throttled);
    }

    private static string ExtractDataIdFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length >= 3 ? segments[^1] : "";
        }
        catch
        {
            return "";
        }
    }

    public async Task DisposeAsync()
    {
        if (_disposed && _context == null) return;

        await _gate.WaitAsync();
        try
        {
            _apiPage = null;   // chiusa insieme al contesto
            try { if (_context != null) await _context.DisposeAsync(); } catch { }
            try { if (_browser != null) await _browser.DisposeAsync(); } catch { }
            try { _playwright?.Dispose(); } catch { }

            _context = null;
            _browser = null;
            _playwright = null;
            _disposed = false;
        }
        finally
        {
            _gate.Release();
        }
    }
}