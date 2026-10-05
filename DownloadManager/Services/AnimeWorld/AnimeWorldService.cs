using DownloadManager.Models;
using DownloadManager.Services.Proxy;
using ManagedCode.Playwright.Stealth;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace DownloadManager.Services.AnimeWorld;

public class AnimeWorldService : IAnimeWorldService
{
    private const string BaseUrlConst = "https://www.animeworld.ac";
    public string Name => "AnimeWorld";
    public string BaseUrl => BaseUrlConst; 

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    private readonly ILogger<AnimeWorldService> _logger;
    private readonly IProxyProvider _proxyProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private string _currentProxyKey = "(none)";

    public AnimeWorldService(ILogger<AnimeWorldService> logger, IProxyProvider proxyProvider)
    {
        _logger = logger;
        _proxyProvider = proxyProvider;
    }

    public async Task NotifyProxyChangedAsync()
    {
        if (_context == null) return; // niente da fare

        _logger.LogInformation("Proxy cambiato: reset del browser Playwright");
        await DisposeAsync();
    }

    public async Task InitializeAsync(bool headless = true)
    {
        var active = _proxyProvider.GetActive();
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
        await _gate.WaitAsync(ct);
        try
        {
            var results = new List<AnimeSearchResult>();
            var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (_context == null) throw new InvalidOperationException("Servizio non inizializzato.");

            var page = await _context.NewPageAsync();
            try
            {
                _logger.LogInformation("Ricerca per '{Query}'", query);

                await page.GotoAsync(BaseUrl);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.WaitForTimeoutAsync(1000);

                // ============================================================
                //  TENTATIVO 1: API interna (di solito restituisce pochi risultati)
                // ============================================================
                var keyword = Uri.EscapeDataString(query);
                var apiUrl = $"/api/search/v2?keyword={keyword}";

                var resultJson = await page.EvaluateAsync<string>(@"
                    async (apiUrl) => {
                        try {
                            const csrfMeta = document.querySelector('meta[name=""csrf-token""]');
                            const csrfToken = csrfMeta ? csrfMeta.getAttribute('content') : '';

                            const r = await fetch(apiUrl, {
                                method: 'POST',
                                credentials: 'include',
                                headers: {
                                    'x-requested-with': 'XMLHttpRequest',
                                    'csrf-token': csrfToken,
                                    'accept': 'application/json, text/javascript, */*; q=0.01'
                                }
                            });
                            const body = await r.text();
                            return JSON.stringify({ status: r.status, body: body });
                        } catch (e) {
                            return JSON.stringify({ status: -1, body: e.toString() });
                        }
                    }
                ", apiUrl);

                _logger.LogInformation("Risposta grezza API: {Raw}",
                    resultJson?.Length > 500 ? resultJson.Substring(0, 500) : resultJson);

                if (!string.IsNullOrEmpty(resultJson))
                {
                    try
                    {
                        using var wrapper = System.Text.Json.JsonDocument.Parse(resultJson);
                        var status = wrapper.RootElement.GetProperty("status").GetInt32();
                        var body = wrapper.RootElement.GetProperty("body").GetString() ?? "";

                        if (status == 200)
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(body);

                            if (doc.RootElement.TryGetProperty("animes", out var animesEl) &&
                                animesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                            {
                                foreach (var anime in animesEl.EnumerateArray())
                                {
                                    var name = anime.TryGetProperty("name", out var n) ? n.GetString() : "";
                                    var link = anime.TryGetProperty("link", out var l) ? l.GetString() : "";
                                    var identifier = anime.TryGetProperty("identifier", out var i) ? i.GetString() : "";

                                    if (string.IsNullOrEmpty(link)) continue;

                                    var fullLink = $"{BaseUrl}/play/{link}.{identifier}";
                                    var key = fullLink.ToLowerInvariant();
                                    if (!seenLinks.Add(key)) continue;

                                    results.Add(new AnimeSearchResult { Name = name ?? "", Link = fullLink });
                                }

                                _logger.LogInformation("API: {Count} risultati", results.Count);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Errore parsing risposta API.");
                    }
                }

                // ============================================================
                //  TENTATIVO 2: HTML search — SEMPRE, per aggiungere altri risultati
                // ============================================================
                _logger.LogInformation("Cerco anche nell'HTML per completare i risultati...");

                var searchUrl = $"{BaseUrlConst}/search?keyword={Uri.EscapeDataString(query)}";
                await page.GotoAsync(searchUrl);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.WaitForTimeoutAsync(500);

                // Prova più selettori: il sito può cambiare struttura.
                // Prima quello "storico", poi uno più generico.
                var anchors = await page.QuerySelectorAllAsync("div.item > div.inner > a.name");
                if (anchors == null || anchors.Count == 0)
                    anchors = await page.QuerySelectorAllAsync("a.name");

                _logger.LogInformation("HTML: trovati {Count} anchor", anchors?.Count ?? 0);

                if (anchors != null)
                {
                    foreach (var a in anchors)
                    {
                        try
                        {
                            var name = (await a.InnerTextAsync()).Trim();
                            var href = await a.GetAttributeAsync("href") ?? "";

                            if (string.IsNullOrEmpty(href)) continue;
                            if (href.StartsWith("/")) href = BaseUrlConst + href;

                            // Scarta link che non sono pagine anime (menu, login, ecc.)
                            if (!href.Contains("/play/", StringComparison.OrdinalIgnoreCase) &&
                                !href.Contains("/anime/", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var key = href.Split('?')[0].ToLowerInvariant();
                            if (!seenLinks.Add(key)) continue;

                            results.Add(new AnimeSearchResult { Name = name, Link = href });
                        }
                        catch { }
                    }
                }

                _logger.LogInformation("Totale risultati combinati: {Count}", results.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante la ricerca di '{Query}'", query);
            }
            finally
            {
                await page.CloseAsync();
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
                    if (href.StartsWith("/")) href = BaseUrlConst + href;

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

            var page = await _context.NewPageAsync();
            try
            {
                await page.GotoAsync(BaseUrlConst);
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.WaitForTimeoutAsync(1500);

                var idsJson = System.Text.Json.JsonSerializer.Serialize(
                    ids.Select(x => x.DataId).ToList());

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
                                    if (!r.ok) return [id, null];
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

                using var doc = System.Text.Json.JsonDocument.Parse(resultJson);

                foreach (var (ep, id) in ids)
                {
                    if (doc.RootElement.TryGetProperty(id, out var el) &&
                        el.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var url = el.GetString();
                        if (!string.IsNullOrEmpty(url))
                            result[ep.Link] = url;
                    }
                }

                _logger.LogInformation("Batch completato: {Found}/{Total}", result.Count, ids.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante il batch");
            }
            finally
            {
                await page.CloseAsync();
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
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