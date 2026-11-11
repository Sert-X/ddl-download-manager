using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using DownloadManager.Models;
using DownloadManager.Services.Domains;
using DownloadManager.Services.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace DownloadManager.Services.Streaming.Loonex;

/// <summary>
/// Loonex (archivio cartoni). Ricerca, stagioni ed episodi sono HTML server-side letti via HTTP;
/// il link video (m3u8 firmato e a scadenza) si intercetta dal player con Playwright al via del download.
/// </summary>
public sealed class LoonexService : ILoonexService
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const int MaxSearchPages = 5;

    private readonly ILogger<LoonexService> _logger;
    private readonly IProxyProvider _proxyProvider;
    private readonly DomainResolver _domains;

    private readonly object _httpLock = new();
    private HttpClient? _http;
    private HttpClientHandler? _httpHandler;
    private string _httpProxyKey = "";
    private readonly SemaphoreSlim _warmUpLock = new(1, 1);
    private DateTime _warmedUpAt = DateTime.MinValue;

    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private string _browserProxyKey = "";

    private readonly Dictionary<string, (DateTime At, string Html)> _pageCache = new();
    private static readonly TimeSpan PageCacheTtl = TimeSpan.FromMinutes(10);
    private const int PageCacheMax = 4;

    public LoonexService(ILogger<LoonexService> logger, IProxyProvider proxyProvider, DomainResolver domains)
    {
        _logger = logger;
        _proxyProvider = proxyProvider;
        _domains = domains;
    }

    public string Name => "Loonex";

    /// <summary>Dominio dalla tab Domini (es. https://loonex.eu).</summary>
    private string Root => _domains.GetBaseUrl(DomainResolver.Loonex).TrimEnd('/');
    private string Cartoni => Root + "/cartoni";

    private static string ProxyKeyOf(ProxyConfig? p) => p == null
        ? "(none)"
        : $"{p.Protocol}|{p.Host}|{p.Port}|{p.Username}";

    // ============================================================
    //  HTTP
    // ============================================================

    private (HttpClient Client, HttpClientHandler Handler) EnsureHttp()
    {
        var proxy = _proxyProvider.GetActive(ProviderKeys.Loonex);
        var key = ProxyKeyOf(proxy);
        lock (_httpLock)
        {
            if (_http != null && _httpHandler != null && _httpProxyKey == key)
                return (_http, _httpHandler);

            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = true,
                CookieContainer = new CookieContainer(),
            };
            if (proxy != null)
            {
                var scheme = proxy.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
                handler.Proxy = new WebProxy($"{scheme}://{proxy.Host}:{proxy.Port}");
                if (proxy.Auth == ProxyAuthType.UserPassword)
                    handler.Proxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
                handler.UseProxy = true;
            }

            var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("it-IT,it;q=0.9,en-US;q=0.8,en;q=0.7");
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            http.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
            http.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
            http.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
            http.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");
            http.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
            http.DefaultRequestHeaders.Add("Sec-CH-UA", "\"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\"");
            http.DefaultRequestHeaders.Add("Sec-CH-UA-Mobile", "?0");
            http.DefaultRequestHeaders.Add("Sec-CH-UA-Platform", "\"Windows\"");

            _http?.Dispose();
            _http = http;
            _httpHandler = handler;
            _httpProxyKey = key;
            return (http, handler);
        }
    }

    /// <summary>
    /// Dopo un 403: apre la home con Playwright (supera la verifica anti-bot) e copia i cookie
    /// nell'HttpClient. Le pagine lette in parallelo che ricevono 403 insieme lo fanno una volta sola.
    /// </summary>
    private async Task WarmUpAsync(DateTime failedAt, CancellationToken ct)
    {
        await _warmUpLock.WaitAsync(ct);
        try
        {
            if (_warmedUpAt >= failedAt) return;

            _logger.LogInformation("[LOONEX] 403 dal sito: apro la home col browser per ottenere i cookie...");
            var context = await GetBrowserContextAsync();
            var page = await context.NewPageAsync();
            try
            {
                await page.GotoAsync(Root + "/", new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });
                await page.WaitForTimeoutAsync(3000);

                var cookies = await context.CookiesAsync([Root]);
                var (_, handler) = EnsureHttp();
                var uri = new Uri(Root);
                foreach (var c in cookies)
                {
                    try { handler.CookieContainer.Add(uri, new System.Net.Cookie(c.Name, c.Value, c.Path, c.Domain)); }
                    catch (CookieException) { }
                }
                _logger.LogInformation("[LOONEX] Cookie del browser copiati: {Count}", cookies.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[LOONEX] Apertura della home col browser fallita");
            }
            finally
            {
                try { await page.CloseAsync(); } catch { }
            }
            _warmedUpAt = DateTime.UtcNow;
        }
        finally { _warmUpLock.Release(); }
    }

    private async Task<HttpResponseMessage> SendGetAsync(string url, CancellationToken ct)
    {
        var (http, _) = EnsureHttp();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri(Root + "/");
        return await http.SendAsync(req, ct);
    }

    private async Task<string> GetHtmlAsync(string url, CancellationToken ct)
    {
        var sentAt = DateTime.UtcNow;
        var resp = await SendGetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.Forbidden)
        {
            resp.Dispose();
            await WarmUpAsync(sentAt, ct);
            resp = await SendGetAsync(url, ct);
        }
        using (resp)
        {
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
    }

    /// <summary>Pagina di una scheda (anche 2-3 MB): in cache per non riscaricarla a ogni cambio stagione.</summary>
    private async Task<string> GetSchedaHtmlAsync(string schedaUrl, CancellationToken ct)
    {
        lock (_pageCache)
        {
            if (_pageCache.TryGetValue(schedaUrl, out var hit) && DateTime.UtcNow - hit.At < PageCacheTtl)
                return hit.Html;
        }

        var html = await GetHtmlAsync(schedaUrl, ct);
        lock (_pageCache)
        {
            _pageCache[schedaUrl] = (DateTime.UtcNow, html);
            while (_pageCache.Count > PageCacheMax)
                _pageCache.Remove(_pageCache.MinBy(kv => kv.Value.At).Key);
        }
        return html;
    }

    /// <summary>Link della scheda ricostruito sul dominio attuale (vale anche per i link salvati col dominio vecchio).</summary>
    private string SchedaUrl(string linkOrSlug)
    {
        var m = RxSchedaSlug.Match(linkOrSlug);
        var slug = m.Success ? m.Groups[1].Value : linkOrSlug;
        return $"{Cartoni}/index.php?scheda={slug}";
    }

    // ============================================================
    //  RICERCA
    // ============================================================

    public async Task<List<MovieSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var baseUrl = $"{Cartoni}/index.php?search={Uri.EscapeDataString(query.Trim())}";
        try
        {
            var first = await GetHtmlAsync(baseUrl, ct);
            var pages = new List<string> { first };

            var lastPage = RxPageLink.Matches(first).Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(1).Max();
            if (lastPage > 1)
            {
                var more = await Task.WhenAll(Enumerable.Range(2, Math.Min(lastPage, MaxSearchPages) - 1)
                    .Select(async p =>
                    {
                        try { return await GetHtmlAsync($"{baseUrl}&page={p}", ct); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "[LOONEX] Ricerca '{Query}': pagina {Page} non letta", query, p);
                            return "";
                        }
                    }));
                pages.AddRange(more);
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var output = new List<MovieSearchResult>();
            foreach (var html in pages)
                foreach (var r in ParseCards(html))
                    if (seen.Add(r.Link)) output.Add(r);

            _logger.LogInformation("[LOONEX] Ricerca '{Query}': {Count} risultati ({Pages} pagine)",
                query, output.Count, Math.Min(lastPage, MaxSearchPages));
            return output;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[LOONEX] Errore ricerca '{Query}'", query);
            throw new InvalidOperationException($"Loonex non raggiungibile: {ex.GetBaseException().Message}", ex);
        }
    }

    private IEnumerable<MovieSearchResult> ParseCards(string html)
    {
        var pageBase = new Uri(Cartoni + "/");
        foreach (Match m in RxCard.Matches(html))
        {
            var slug = WebUtility.HtmlDecode(m.Groups[1].Value);
            if (!RxValidSlug.IsMatch(slug)) continue;   // template JS della pagina ("${...}")
            var body = m.Groups[2].Value;

            var rawTitle = RxCardTitle.Match(body) is { Success: true } t ? t.Groups[1].Value
                : RxImgAlt.Match(body) is { Success: true } a ? a.Groups[1].Value : "";
            var title = Regex.Replace(WebUtility.HtmlDecode(rawTitle), @"\s+", " ").Trim();
            if (title.Length == 0) continue;

            string? year = null;
            var y = RxTrailingYear.Match(title);
            if (y.Success)
            {
                year = y.Groups[1].Value;
                title = title[..y.Index].Trim();
            }

            string? poster = null;
            if (RxImgSrc.Match(body) is { Success: true } img &&
                Uri.TryCreate(pageBase, WebUtility.HtmlDecode(img.Groups[1].Value), out var posterUri))
                poster = posterUri.ToString();

            var isFilm = body.Contains("movie-badge", StringComparison.OrdinalIgnoreCase) ||
                         body.Contains("FILM COMPLETO", StringComparison.OrdinalIgnoreCase);

            yield return new MovieSearchResult
            {
                Title = title,
                Year = year,
                Link = SchedaUrl(slug),
                PosterUrl = poster,
                IsSeries = !isFilm,
                Description = isFilm ? "Film" : "Serie",
                TitleId = slug,
            };
        }
    }

    // ============================================================
    //  STAGIONI
    // ============================================================

    private sealed record SeasonTab(string Target, string Name);

    private static List<SeasonTab> ParseTabs(string html) =>
        RxSeasonTab.Matches(html)
            .Select(m => new SeasonTab(m.Groups[1].Value, Regex.Replace(WebUtility.HtmlDecode(m.Groups[2].Value), @"\s+", " ").Trim()))
            .Where(t => t.Name.Length > 0)
            .ToList();

    /// <summary>Link di una puntata con l'etichetta della riga; SeasonKey = N di "Episodio NxMM" (null se assente).</summary>
    private sealed record WatchEntry(string Link, string Label, int? SeasonKey, string Number, bool Regular);

    private List<WatchEntry> ParseEntries(string scope)
    {
        var list = new List<WatchEntry>();
        foreach (Match a in RxWatchLink.Matches(scope))
        {
            var tag = a.Value;
            var enc = RxAttrLxWatch.Match(tag);
            if (!enc.Success) continue;
            int seed = RxAttrSeed.Match(tag) is { Success: true } s ? int.Parse(s.Groups[1].Value) : 0;

            var target = DecodeLink(enc.Groups[1].Value, seed);
            if (target == null) continue;

            var label = LabelBefore(scope, a.Index);
            var em = RxEpisodeLabel.Match(label);
            bool regular = em.Success && !em.Groups[2].Value.Contains('.');
            var number = regular
                ? int.Parse(em.Groups[2].Value).ToString()
                : em.Success ? em.Groups[2].Value.TrimStart('0') : "";
            int? key = em.Success ? int.Parse(em.Groups[1].Value) : null;
            list.Add(new WatchEntry(target, label, key, number, regular));
        }
        return list;
    }

    /// <summary>
    /// Stagione mostrata nell'app: una tab intera, oppure una parte di tab. Le tab "Serie Completa"
    /// contengono più stagioni (1x.., 2x..): si dividono per numero di stagione; le puntate senza
    /// numero finiscono in "Extra". Group = serie della tab quando la pagina ne ha più d'una.
    /// </summary>
    private sealed record SeasonPart(string TabTarget, int? SeasonKey, bool IsExtra, int Number, string Title, string Group);

    private List<SeasonPart> BuildParts(string html)
    {
        var tabs = ParseTabs(html);
        var parts = new List<SeasonPart>();
        var used = new HashSet<(string, int)>();

        foreach (var (tab, i) in tabs.Select((t, i) => (t, i)))
        {
            var entries = ParseEntries(PaneOf(html, tab.Target));
            var keys = entries.Where(e => e.SeasonKey != null).Select(e => e.SeasonKey!.Value).Distinct().ToList();
            var hasUnnumbered = entries.Any(e => e.SeasonKey == null);

            var seasonTab = RxSeasonTabName.Match(tab.Name);
            var group = tabs.Count > 1 && !seasonTab.Success ? CleanGroupName(tab.Name) : "";

            if (keys.Count > 1)
            {
                foreach (var n in keys)
                {
                    used.Add((group, n));
                    parts.Add(new SeasonPart(tab.Target, n, false, n, $"Stagione {n}", group));
                }
                if (hasUnnumbered)
                {
                    int extra = 0;
                    while (!used.Add((group, extra))) extra++;
                    parts.Add(new SeasonPart(tab.Target, null, true, extra, "Extra", group));
                }
                continue;
            }

            int number = seasonTab.Success ? int.Parse(seasonTab.Groups[1].Value)
                : group.Length > 0 && keys.Count == 1 ? keys[0]
                : group.Length > 0 ? 1
                : i + 1;
            while (!used.Add((group, number))) number++;
            // Serie a sé con una sola stagione: il nome della serie è già nel menu "Serie"
            var title = group.Length > 0 && keys.Count == 1 ? $"Stagione {number}" : tab.Name;
            parts.Add(new SeasonPart(tab.Target, null, false, number, title, group));
        }
        return parts;
    }

    /// <summary>"Ben 10 (2005) Serie Completa" → "Ben 10 (2005)".</summary>
    private static string CleanGroupName(string tabName)
    {
        var name = RxGroupNoise.Replace(tabName, " ");
        return Regex.Replace(name, @"\s+", " ").Trim(' ', '-', ':', '·', '|', ',');
    }

    /// <summary>Stagioni della scheda (tab, divise per stagione quando serve). Lista vuota = film (nessuna tab).</summary>
    public async Task<List<Season>> GetSeasonsAsync(string schedaUrl, CancellationToken ct = default)
    {
        var url = SchedaUrl(schedaUrl);
        var html = await GetSchedaHtmlAsync(url, ct);
        var parts = BuildParts(html);

        var seasons = parts.Select(p => new Season
        {
            Number = p.Number,
            Title = p.Title,
            Group = p.TabTarget,
            GroupTitle = p.Group,
        }).ToList();

        _logger.LogInformation("[LOONEX] {Url}: {Count} stagioni in {Groups} serie", url, seasons.Count,
            Math.Max(1, parts.Select(p => p.Group).Distinct().Count()));
        return seasons;
    }

    // ============================================================
    //  EPISODI
    // ============================================================

    /// <summary>
    /// Episodi della stagione <paramref name="seasonIndex"/> (posizione nella lista di <see cref="GetSeasonsAsync"/>).
    /// Per i film (nessuna tab): le versioni del film.
    /// </summary>
    public async Task<List<StreamingEpisode>> GetEpisodesAsync(string schedaUrl, int seasonIndex, CancellationToken ct = default)
    {
        var url = SchedaUrl(schedaUrl);
        var html = await GetSchedaHtmlAsync(url, ct);
        var parts = BuildParts(html);

        IEnumerable<WatchEntry> entries;
        int seasonNumber = 1;
        bool hasTabs = parts.Count > 0;
        if (hasTabs)
        {
            if (seasonIndex < 0 || seasonIndex >= parts.Count) return [];
            var part = parts[seasonIndex];
            seasonNumber = part.Number;
            entries = ParseEntries(PaneOf(html, part.TabTarget))
                .Where(e => part.IsExtra ? e.SeasonKey == null : part.SeasonKey == null || e.SeasonKey == part.SeasonKey);
        }
        else
        {
            entries = ParseEntries(html);
        }

        var episodes = new List<StreamingEpisode>();
        foreach (var e in entries)
        {
            episodes.Add(new StreamingEpisode
            {
                Number = e.Number,
                Title = e.Label.Length > 0 ? e.Label : $"Episodio {episodes.Count + 1}",
                Link = e.Link,
                SeasonNumber = seasonNumber,
                // Speciali "1x05.5" o senza numero: si accodano come video a sé, non come episodio N
                IsMovie = !e.Regular && hasTabs,
            });
        }

        _logger.LogInformation("[LOONEX] {Url} stagione {Index}: {Count} episodi", url, seasonIndex, episodes.Count);
        return episodes;
    }

    private static string PaneOf(string html, string targetId)
    {
        var start = html.IndexOf($"id=\"{targetId}\"", StringComparison.Ordinal);
        if (start < 0) return "";
        var end = html.IndexOf("class=\"tab-pane", start, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }

    /// <summary>Etichetta della riga che contiene il link ("Episodio 1x01", "Film Completo").</summary>
    private static string LabelBefore(string scope, int index)
    {
        var from = Math.Max(0, index - 4000);
        var window = scope.Substring(from, index - from);
        var m = RxRowLabel.Matches(window).LastOrDefault();
        return m == null ? "" : Regex.Replace(WebUtility.HtmlDecode(m.Groups[1].Value), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Il sito cifra il link di ogni episodio (attributo data-lx-watch, XOR con chiave derivata da data-s)
    /// e lo rimette nell'href via JS. Ritorna il link relativo a /cartoni/ (es. "guarda.php?id=x_1x01").
    /// </summary>
    private string? DecodeLink(string b64, int seed)
    {
        string? real = seed > 0 ? TryDecode(b64, seed) : null;
        for (int s = 1; real == null && s <= 128; s++) real = TryDecode(b64, s);
        if (real == null) return null;

        if (!Uri.TryCreate(new Uri(Root + "/"), real, out var uri)) return null;
        var pq = uri.PathAndQuery;
        return pq.StartsWith("/cartoni/", StringComparison.OrdinalIgnoreCase) ? pq["/cartoni/".Length..] : pq;
    }

    private static string? TryDecode(string b64, int seed)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); } catch { return null; }

        var key = new byte[32];
        uint h = unchecked((uint)seed * 0x01000193u);
        for (int i = 0; i < 32; i++)
        {
            h = unchecked((h ^ (uint)(i * 109 + 89)) * 0x01000193u);
            key[i] = (byte)(h >> ((i % 4) * 8));
        }

        var sb = new StringBuilder(bytes.Length);
        for (int i = 0; i < bytes.Length; i++)
            sb.Append((char)(bytes[i] ^ key[i % 32] ^ ((i * 11 + 37) & 0xFF)));
        var u = sb.ToString();

        bool validStart = u.StartsWith("/cartoni/") || u.StartsWith("/guarda/") || u.StartsWith("http://") ||
                          u.StartsWith("https://") || u.StartsWith('/');
        return u.Length >= 8 && validStart && u.All(c => c >= 0x21 && c <= 0x7E) ? u : null;
    }

    // ============================================================
    //  ESTRAZIONE URL VIDEO (m3u8)
    // ============================================================

    private async Task<IBrowserContext> GetBrowserContextAsync()
    {
        var proxy = _proxyProvider.GetActive(ProviderKeys.Loonex);
        var key = ProxyKeyOf(proxy);

        await _browserLock.WaitAsync();
        try
        {
            if (_context != null && _browser is { IsConnected: true } && _browserProxyKey == key)
                return _context;

            await CloseBrowserAsync();

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--disable-blink-features=AutomationControlled"],
            });

            var options = new BrowserNewContextOptions { UserAgent = UserAgent, Locale = "it-IT" };
            if (proxy != null)
            {
                var scheme = proxy.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
                options.Proxy = new Microsoft.Playwright.Proxy
                {
                    Server = $"{scheme}://{proxy.Host}:{proxy.Port}",
                    Username = proxy.Auth == ProxyAuthType.UserPassword ? proxy.Username : null,
                    Password = proxy.Auth == ProxyAuthType.UserPassword ? proxy.Password : null
                };
            }

            _context = await _browser.NewContextAsync(options);
            _browserProxyKey = key;
            _logger.LogInformation("[LOONEX] Browser avviato{Proxy}", proxy != null ? " (con proxy)" : "");
            return _context;
        }
        finally { _browserLock.Release(); }
    }

    /// <summary>
    /// Apre il player della puntata e intercetta la richiesta della playlist .m3u8 (firmata, scade):
    /// va chiamato al via del download.
    /// </summary>
    public async Task<string?> GetVideoUrlAsync(string videoLink, CancellationToken ct = default)
    {
        string url;
        if (videoLink.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(videoLink, UriKind.Absolute, out var abs))
            url = Root + abs.PathAndQuery;
        else if (videoLink.StartsWith('/'))
            url = Root + videoLink;
        else
            url = $"{Cartoni}/{videoLink}";

        var context = await GetBrowserContextAsync();
        var page = await context.NewPageAsync();
        var found = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await page.AddInitScriptAsync(@"
                Object.defineProperty(navigator, 'webdriver', { get: () => false });
                Object.defineProperty(navigator, 'plugins', { get: () => [1,2,3,4,5] });
                Object.defineProperty(navigator, 'languages', { get: () => ['it-IT','it','en'] });
                window.chrome = { runtime: {} };
            ");

            await page.RouteAsync("**/*", route =>
                route.Request.ResourceType is "image" or "font" or "stylesheet" ? route.AbortAsync() : route.ContinueAsync());
            page.Request += (_, request) =>
            {
                if (request.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                    found.TrySetResult(request.Url);
            };

            await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(25));
            try
            {
                var m3u8 = await found.Task.WaitAsync(cts.Token);
                _logger.LogInformation("[LOONEX] Playlist trovata per {Link}", videoLink);
                return m3u8;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("[LOONEX] Nessun m3u8 intercettato, provo window.originalVideoSrc per {Link}", videoLink);
                try
                {
                    var fallback = await page.EvaluateAsync<string?>(
                        "() => { var s = window.originalVideoSrc; return (s && !s.includes('start1.mp4')) ? s : null; }");
                    if (!string.IsNullOrEmpty(fallback))
                    {
                        _logger.LogInformation("[LOONEX] Playlist da JS per {Link}", videoLink);
                        return fallback;
                    }
                }
                catch { /* ignore */ }

                _logger.LogWarning("[LOONEX] Nessuna playlist m3u8 entro 25 s per {Link}", videoLink);
                return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[LOONEX] Errore estrazione video {Link}", videoLink);
            return null;
        }
        finally
        {
            try { await page.CloseAsync(); } catch { }
        }
    }

    // ============================================================
    //  COOKIE EXPORT (per yt-dlp / FFmpeg)
    // ============================================================

    public async Task<string?> GetSessionCookiesNetscapeAsync(CancellationToken ct = default)
    {
        await _browserLock.WaitAsync(ct);
        try
        {
            if (_context == null) return null;
            // Tutti i domini: servono anche quelli del CDN della playlist, non solo di loonex
            var cookies = await _context.CookiesAsync();
            if (cookies.Count == 0) return null;

            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            foreach (var c in cookies)
            {
                var domain = c.Domain.StartsWith('.') ? c.Domain : "." + c.Domain;
                var expiry = c.Expires > 0 ? (long)c.Expires : DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
                sb.AppendLine($"{domain}\tTRUE\t{c.Path}\t{(c.Secure ? "TRUE" : "FALSE")}\t{expiry}\t{c.Name}\t{c.Value}");
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LOONEX] Impossibile esportare i cookie");
            return null;
        }
        finally { _browserLock.Release(); }
    }

    // ============================================================
    //  IStreamingProvider (adapter)
    // ============================================================

    string IStreamingProvider.BaseUrl => Cartoni;
    bool IStreamingProvider.IsStreamingProvider => true;

    Task IStreamingProvider.InitializeAsync(bool headless) => Task.CompletedTask;

    Task<List<MovieSearchResult>> IStreamingProvider.SearchAsync(string query) => SearchAsync(query);

    Task<List<Season>> IStreamingProvider.GetSeasonsAsync(string seriesLink) => GetSeasonsAsync(seriesLink);

    async Task<List<StreamingEpisode>> IStreamingProvider.GetEpisodesAsync(string seriesLink, int seasonNumber)
    {
        var seasons = await GetSeasonsAsync(seriesLink);
        var index = seasons.FindIndex(s => s.Number == seasonNumber);
        return await GetEpisodesAsync(seriesLink, index < 0 ? seasonNumber - 1 : index);
    }

    Task<string?> IStreamingProvider.GetVideoUrlAsync(
        string contentLink, bool isSeries, int seasonNumber, int episodeNumber, int? episodeId)
        => GetVideoUrlAsync(contentLink);

    // ============================================================
    //  DISPOSE
    // ============================================================

    private async Task CloseBrowserAsync()
    {
        if (_context != null) { try { await _context.CloseAsync(); } catch { } }
        if (_browser != null) { try { await _browser.CloseAsync(); } catch { } }
        _playwright?.Dispose();
        _context = null;
        _browser = null;
        _playwright = null;
    }

    public async ValueTask DisposeAsync()
    {
        await _browserLock.WaitAsync();
        try { await CloseBrowserAsync(); }
        finally { _browserLock.Release(); }
        lock (_httpLock) { _http?.Dispose(); _http = null; }
    }

    // ============================================================
    //  REGEX
    // ============================================================

    private static readonly Regex RxCard = new(
        @"<a\s+href=""(?:index\.php)?\?scheda=([^""#&]+)""[^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex RxCardTitle = new(
        @"class=""card-title-cine-v2[^""]*""\s+title=""([^""]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RxImgAlt = new(@"<img[^>]*\balt=""([^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxImgSrc = new(@"<img[^>]*\bsrc=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxTrailingYear = new(@"\s*\((\d{4})\)\s*$", RegexOptions.Compiled);
    private static readonly Regex RxPageLink = new(@"[?&](?:amp;)?page=(\d+)", RegexOptions.Compiled);

    private static readonly Regex RxValidSlug = new(@"^[A-Za-z0-9._%\-]+$", RegexOptions.Compiled);
    private static readonly Regex RxSchedaSlug =new(@"[?&]scheda=([^&#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RxSeasonTab = new(
        @"<button[^>]*data-bs-target=""#([^""]+)""[^>]*role=""tab""[^>]*>(.*?)</button>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex RxWatchLink = new(@"<a\b[^>]*\bdata-lx-watch=""[^""]+""[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxAttrLxWatch = new(@"\bdata-lx-watch=""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxAttrSeed = new(@"\bdata-s=""(\d+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxRowLabel = new(@"\bdata-(?:ep-title|movie-label)=""([^""]*)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RxEpisodeLabel = new(@"Episodio\s+(\d+)x(\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    /// <summary>Tab che è una stagione della stessa serie ("Stagione 2", "Stagione 2 Sub ITA"), non una serie a sé.</summary>
    private static readonly Regex RxSeasonTabName = new(@"^\s*Stagione\s+(\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxGroupNoise = new(
        @"\b(?:Serie\s+Completa|Serie\s+TV|Stagioni\s+Complete|Tutte\s+le\s+stagioni|Completa)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
