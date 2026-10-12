using System.Text.Json;
using System.Text.RegularExpressions;
using DownloadManager.Models;
using DownloadManager.Services.Domains;
using DownloadManager.Services.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using System.Text;
using System;

namespace DownloadManager.Services.Streaming.StreamingCommunity;

public class StreamingCommunityService : IStreamingCommunityService
{
    private readonly ILogger<StreamingCommunityService> _logger;
    private readonly DomainResolver _domainResolver;

    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private bool _initialized;
    private readonly StreamingCommunityAuthService _authService;
    private readonly IProxyProvider _proxyProvider;
    private string _currentProxyKey = "(none)";

    // Cache cookie (5 min)
    private string? _cachedCookiesNetscape;
    private DateTime _cachedCookiesAt = DateTime.MinValue;
    private static readonly TimeSpan CookieCacheTtl = TimeSpan.FromMinutes(5);

    private const string DefaultBaseUrl = "https://streamingcommunityz.shiksha";
    /// <summary>
    /// CDN immagini: derivato dal dominio base (cambia se il dominio cambia).
    /// Es. https://streamingcommunityz.rip → https://cdn.streamingcommunityz.rip/images/
    /// </summary>
    private string CdnBase
    {
        get
        {
            try
            {
                var uri = new Uri(BaseUrl);
                // Il CDN è sempre cdn.<host-del-dominio-base>
                return $"https://cdn.{uri.Host}/images/";
            }
            catch
            {
                // Fallback: prova a derivare dal dominio di default
                try
                {
                    var fallbackUri = new Uri(DefaultBaseUrl);
                    return $"https://cdn.{fallbackUri.Host}/images/";
                }
                catch
                {
                    return "https://cdn.streamingcommunityz.shiksha/images/";
                }
            }
        }
    }

    public const string ChromeVersion = "150";

    public const string RealisticUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        $"(KHTML, like Gecko) Chrome/{ChromeVersion}.0.0.0 Safari/537.36";

    public const string SecChUa =
        $"\"Chromium\";v=\"{ChromeVersion}\", \"Not_A Brand\";v=\"24\"";

    public string Name => "StreamingCommunity";
    public string BaseUrl
    {
        get
        {
            try { return _domainResolver.GetBaseUrl("StreamingCommunity"); }
            catch { return DefaultBaseUrl; }
        }
    }
    public bool IsStreamingProvider => true;

    public StreamingCommunityService(
        ILogger<StreamingCommunityService> logger,
        DomainResolver domainResolver,
        StreamingCommunityAuthService authService,
        IProxyProvider proxyProvider)
    {
        _logger = logger;
        _domainResolver = domainResolver;
        _authService = authService;
        _proxyProvider = proxyProvider;
        _authService.StateChanged += OnAuthStateChanged;
    }

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        _logger.LogWarning("[SC] Auth cambiata: resetto Playwright");
        _ = Task.Run(async () =>
        {
            try { await NotifyAuthChangedAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "[SC] Errore reset post-auth"); }
        });
    }

    public async Task NotifyAuthChangedAsync()
    {
        _logger.LogWarning("[SC] NotifyAuthChanged: close + reinit");
        await CloseContextOnlyAsync();
        await InitializeAsync();
    }

    public async Task<(bool LoggedIn, bool Premium, string Info)> CheckSessionAsync()
    {
        await InitializeAsync();
        if (_context == null) return (false, false, "context null");

        var page = await NewStealthPageAsync();
        try
        {
            var resp = await page.GotoAsync($"{BaseUrl}/it/account",
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30000
                });

            var html = await resp!.TextAsync();

            var dataPage = ExtractDataPageJsonDecodedPublic(html);
            if (string.IsNullOrEmpty(dataPage))
                return (false, false, "no data-page nel /it/account");

            using var doc = System.Text.Json.JsonDocument.Parse(dataPage);
            if (!doc.RootElement.TryGetProperty("props", out var props))
                return (false, false, "no props");

            bool loggedIn = false;
            bool premium = false;
            string user = "?";

            if (props.TryGetProperty("auth", out var auth) &&
                auth.TryGetProperty("user", out var usr) &&
                usr.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                loggedIn = true;
                if (usr.TryGetProperty("name", out var nm))
                    user = nm.GetString() ?? "?";
            }

            if (props.TryGetProperty("premiumUser", out var pu) &&
                pu.ValueKind == System.Text.Json.JsonValueKind.Object)
                premium = true;

            return (loggedIn, premium, $"user={user}");
        }
        catch (Exception ex)
        {
            return (false, false, $"errore: {ex.Message}");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    internal static string? ExtractDataPageJsonDecodedPublic(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var trimmed = html.TrimStart();
        if (trimmed.StartsWith("{")) return trimmed;
        var m = Regex.Match(html,
            @"\bdata-page\s*=\s*[""']([^""']+)[""']",
            RegexOptions.Singleline);
        if (m.Success)
            return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
        return null;
    }

    // ============================================================
    //  INIZIALIZZAZIONE
    // ============================================================

    public async Task InitializeAsync(bool headless = true)
    {
        if (_initialized && _context != null) return;

        _playwright ??= await Playwright.CreateAsync();

        var active = _proxyProvider.GetActive(ProviderKeys.StreamingCommunity);
        var proxyKey = active == null
            ? "(none)"
            : $"{active.Protocol}|{active.Host}|{active.Port}|{active.Username}";

        if (_context != null && _currentProxyKey != proxyKey)
        {
            _logger.LogWarning("[SC] Proxy cambiato ({Old} → {New}): re-init context",
                _currentProxyKey, proxyKey);
            await CloseContextOnlyAsync();
        }

        if (_context != null) return;

        var launchArgs = new[]
        {
            "--disable-blink-features=AutomationControlled",
            "--no-sandbox",
            "--disable-dev-shm-usage"
        };

        IBrowser? browser = null;
        string usedChannel = "?";

        try
        {
            browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                Channel = "chrome",
                Args = launchArgs
            });
            usedChannel = "chrome";
        }
        catch
        {
            try
            {
                browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = headless,
                    Channel = "msedge",
                    Args = launchArgs
                });
                usedChannel = "msedge";
            }
            catch
            {
                browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
                {
                    Headless = headless,
                    Args = launchArgs
                });
                usedChannel = "chromium";
            }
        }

        _browser = browser;

        var contextOptions = new BrowserNewContextOptions
        {
            UserAgent = RealisticUserAgent,
            Locale = "it-IT",
            TimezoneId = "Europe/Rome",
            ViewportSize = new ViewportSize { Width = 1920, Height = 1080 }
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
            _logger.LogWarning("[SC] Proxy applicato: {Server}", proxy.Server);
        }

        _context = await _browser.NewContextAsync(contextOptions);
        _currentProxyKey = proxyKey;

        await InjectSessionCookiesAsync();
        _initialized = true;

        _logger.LogWarning("[SC] Init OK: channel={Ch}, proxy={P}", usedChannel, proxyKey);
    }

    private async Task InjectSessionCookiesAsync()
    {
        if (_context == null) return;

        if (string.IsNullOrEmpty(_authService.Current.SessionCookies))
        {
            _logger.LogWarning("[SC] InjectSessionCookies: nessun cookie salvato");
            return;
        }

        try
        {
            var cookies = StreamingCommunityAuthService.ParseCookieString(
                _authService.Current.SessionCookies);

            var baseUri = new Uri(BaseUrl);

            var pwCookies = new List<Microsoft.Playwright.Cookie>();
            foreach (var (name, value) in cookies)
            {
                pwCookies.Add(new Microsoft.Playwright.Cookie
                {
                    Name = name,
                    Value = value,
                    Domain = baseUri.Host,
                    Path = "/",
                    Secure = true,
                    HttpOnly = false,
                    SameSite = SameSiteAttribute.Lax
                });
            }

            if (pwCookies.Count > 0)
            {
                await _context.AddCookiesAsync(pwCookies);
                _logger.LogWarning("[SC] ✅ Iniettati {N} cookie in Playwright",
                    pwCookies.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SC] Errore iniezione cookie sessione");
        }
    }

    private async Task CloseContextOnlyAsync()
    {
        if (_context != null)
        {
            try { await _context.CloseAsync(); } catch { }
            _context = null;
        }
        _initialized = false;

        _cachedCookiesNetscape = null;
        _cachedCookiesAt = DateTime.MinValue;
    }

    // ============================================================
    //  HELPER
    // ============================================================

    private async Task<IPage> NewStealthPageAsync()
    {
        if (_context == null) throw new InvalidOperationException("Context Playwright non inizializzato");
        var page = await _context.NewPageAsync();
        await ApplyStealthAsync(page);
        return page;
    }

    private static async Task ApplyStealthAsync(IPage page)
    {
        await page.AddInitScriptAsync(@"
            Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
            window.chrome = { runtime: {} };
            Object.defineProperty(navigator, 'plugins', {
                get: () => [1, 2, 3, 4, 5]
            });
            Object.defineProperty(navigator, 'languages', {
                get: () => ['it-IT', 'it', 'en-US', 'en']
            });
            Object.defineProperty(navigator, 'platform', { get: () => 'Win32' });
            Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => 8 });
            Object.defineProperty(navigator, 'deviceMemory', { get: () => 8 });
        ");
    }

    // ============================================================
    //  RICERCA
    // ============================================================

    public async Task<List<MovieSearchResult>> SearchAsync(string query)
    {
        await InitializeAsync();

        var results = new List<MovieSearchResult>();
        if (_context == null) return results;

        var page = await NewStealthPageAsync();
        try
        {
            var searchUrl = $"{BaseUrl}/it/search?q={Uri.EscapeDataString(query)}";
            _logger.LogInformation("[SC] Ricerca: {Url}", searchUrl);

            var response = await page.GotoAsync(searchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45000
            });

            _logger.LogInformation("[SC] Ricerca HTTP {Status}", response?.Status);

            var dataPage = await ReadDataPageFromResponseAsync(response);

            if (dataPage != null)
            {
                var fromJson = ExtractTitlesFromJson(dataPage.Value);
                results.AddRange(fromJson);
                _logger.LogInformation("[SC] JSON ha prodotto {N} titoli", fromJson.Count);

                if (fromJson.Count == 0) LogPropsStructure(dataPage.Value);
            }

            if (results.Count == 0)
            {
                _logger.LogWarning("[SC] JSON vuoto, fallback sui link DOM");
                try
                {
                    await page.WaitForSelectorAsync("a[href*='/it/titles/']",
                        new PageWaitForSelectorOptions { Timeout = 8000 });
                }
                catch { }

                var links = await page.QuerySelectorAllAsync("a[href*='/it/titles/']");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var linkEl in links)
                {
                    try
                    {
                        var href = await linkEl.GetAttributeAsync("href");
                        if (string.IsNullOrEmpty(href)) continue;
                        if (href.StartsWith("/")) href = BaseUrl + href;
                        var cleanHref = href.Split('?')[0].Split('#')[0];
                        if (!seen.Add(cleanHref)) continue;

                        var img = await linkEl.QuerySelectorAsync("img");
                        string title = "", poster = "";
                        if (img != null)
                        {
                            title = (await img.GetAttributeAsync("alt") ?? "").Trim();
                            poster = await img.GetAttributeAsync("src") ?? "";
                        }
                        if (string.IsNullOrEmpty(title))
                            title = (await linkEl.InnerTextAsync()).Trim();
                        if (string.IsNullOrEmpty(title)) continue;

                        results.Add(new MovieSearchResult
                        {
                            Title = title,
                            Link = cleanHref,
                            PosterUrl = poster,
                            TitleId = ExtractTitleIdFromUrl(cleanHref),
                            IsSeries = false
                        });
                    }
                    catch { }
                }
            }

            _logger.LogInformation("[SC] Trovati {Count} risultati per '{Query}'",
                results.Count, query);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SC] Errore ricerca per '{Query}'", query);
        }
        finally
        {
            await page.CloseAsync();
        }

        return results;
    }

    // ============================================================
    //  STAGIONI / EPISODI
    // ============================================================

    public async Task<List<Season>> GetSeasonsAsync(string seriesLink)
    {
        var seasons = new List<Season>();

        var (dataPage, page) = await NavigateAndReadDataPageAsync(seriesLink);
        try
        {
            if (dataPage == null) return seasons;
            if (!dataPage.Value.TryGetProperty("props", out var props)) return seasons;
            if (!props.TryGetProperty("title", out var title)) return seasons;

            var type = title.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            if (type != "tv") return seasons;

            if (!title.TryGetProperty("seasons", out var seasonsArr) ||
                seasonsArr.ValueKind != JsonValueKind.Array)
                return seasons;

            foreach (var s in seasonsArr.EnumerateArray())
            {
                var num = s.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
                var name = s.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                    ? nm.GetString() ?? "" : "";

                if (num <= 0) continue;

                seasons.Add(new Season
                {
                    Number = num,
                    Title = string.IsNullOrWhiteSpace(name) ? $"Stagione {num}" : name
                });
            }

            _logger.LogInformation("[SC] Trovate {Count} stagioni per {Link}",
                seasons.Count, seriesLink);
        }
        finally
        {
            if (page != null) await page.CloseAsync();
        }

        return seasons;
    }

    public async Task<List<StreamingEpisode>> GetEpisodesAsync(string seriesLink, int seasonNumber)
    {
        var episodes = new List<StreamingEpisode>();

        var seasonUrl = seriesLink.TrimEnd('/') + $"/season-{seasonNumber}";
        _logger.LogInformation("[SC] Carico episodi: {Url}", seasonUrl);

        var (dataPage, page) = await NavigateAndReadDataPageAsync(seasonUrl);
        try
        {
            if (dataPage == null) return episodes;
            if (!dataPage.Value.TryGetProperty("props", out var props)) return episodes;
            if (!props.TryGetProperty("loadedSeason", out var loadedSeason)) return episodes;
            if (!loadedSeason.TryGetProperty("episodes", out var eps) ||
                eps.ValueKind != JsonValueKind.Array)
                return episodes;

            foreach (var e in eps.EnumerateArray())
            {
                var num = e.TryGetProperty("number", out var n) ? n.GetInt32() : 0;
                var name = e.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String
                    ? nm.GetString() ?? "" : "";
                var epId = e.TryGetProperty("id", out var id) ? id.GetInt32() : 0;

                if (num <= 0 || epId <= 0) continue;

                episodes.Add(new StreamingEpisode
                {
                    Number = num.ToString(),
                    Title = string.IsNullOrWhiteSpace(name) ? $"Episodio {num}" : name,
                    Link = seriesLink,
                    SeasonNumber = seasonNumber,
                    EpisodeId = epId,
                    IsSelected = false
                });
            }

            _logger.LogInformation("[SC] Trovati {Count} episodi (S{Season})",
                episodes.Count, seasonNumber);
        }
        finally
        {
            if (page != null) await page.CloseAsync();
        }

        return episodes;
    }

    // ============================================================
    //  ESTRAZIONE URL VIDEO
    // ============================================================

    public async Task<string?> GetVideoUrlAsync(
        string contentLink, bool isSeries = false,
        int seasonNumber = 1, int episodeNumber = 1,
        int? episodeId = null)
    {
        if (!isSeries)
            return await GetMovieStreamUrlAsync(contentLink);

        // Se episodeId è già noto, salta la navigazione
        var epId = episodeId ?? await FindEpisodeIdAsync(contentLink, seasonNumber, episodeNumber);

        if (epId <= 0)
        {
            _logger.LogWarning("[SC] Episode ID non trovato (S{Season}E{Ep})",
                seasonNumber, episodeNumber);
            return null;
        }

        var titleId = ExtractTitleIdFromUrl(contentLink);
        if (string.IsNullOrEmpty(titleId)) return null;

        var watchUrl = $"{BaseUrl}/it/watch/{titleId}?e={epId}";
        _logger.LogWarning("[SC] Watch URL (episodio): {Url}", watchUrl);
        return await ExtractM3u8FromWatchUrlAsync(watchUrl);
    }

    public async Task<string?> GetMovieStreamUrlAsync(string movieUrl)
    {
        var titleId = ExtractTitleIdFromUrl(movieUrl);
        if (string.IsNullOrEmpty(titleId))
        {
            _logger.LogWarning("[SC] GetMovieStreamUrl: titleId non estraibile da {Url}", movieUrl);
            return null;
        }

        var watchUrl = $"{BaseUrl}/it/watch/{titleId}";
        _logger.LogWarning("[SC] Watch URL (film): {Url}", watchUrl);
        return await ExtractM3u8FromWatchUrlAsync(watchUrl);
    }

    public async Task<string?> GetEpisodeStreamUrlAsync(string seriesUrl, int season, int episode)
        => await GetVideoUrlAsync(seriesUrl, true, season, episode);

    public Task<string?> ExtractM3u8Async(string pageUrl)
        => ExtractM3u8FromWatchUrlAsync(pageUrl);

    private async Task<string?> ExtractM3u8FromWatchUrlAsync(string watchUrl)
    {
        await InitializeAsync();
        if (_context == null) return null;

        var page = await NewStealthPageAsync();
        string? m3u8Url = null;
        var vixRequests = new List<string>();
        var m3u8Found = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            page.Request += (_, request) =>
            {
                var url = request.Url;

                if (url.Contains("vixcloud", StringComparison.OrdinalIgnoreCase) ||
                    url.Contains("/playlist", StringComparison.OrdinalIgnoreCase))
                {
                    lock (vixRequests) vixRequests.Add(url);
                    _logger.LogWarning("[SC] REQ: {Url}", url);
                }

                if (url.Contains("vixcloud.co/playlist")
                    && !url.Contains("type=")
                    && !url.Contains("rendition=")
                    && string.IsNullOrEmpty(m3u8Url))
                {
                    m3u8Url = url;
                    _logger.LogWarning("[SC] ✅ MASTER identificato: {Url}", url);
                    m3u8Found.TrySetResult(url);
                }
            };

            _logger.LogWarning("[SC] Navigo a watch: {Url}", watchUrl);

            var resp = await page.GotoAsync(watchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 60000
            });

            _logger.LogWarning("[SC] Watch HTTP: {Status}", resp?.Status);

            // Attesa efficiente: esce appena il master arriva (max 12s)
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                try { await m3u8Found.Task.WaitAsync(cts.Token); }
                catch (OperationCanceledException) { }
            }

            // Se non arrivato, click play e attesa breve
            if (string.IsNullOrEmpty(m3u8Url))
            {
                var sels = new[] { ".vjs-big-play-button", ".play-button", "video" };
                foreach (var sel in sels)
                {
                    try
                    {
                        var el = await page.QuerySelectorAsync(sel);
                        if (el != null)
                        {
                            _logger.LogWarning("[SC] Click su {Sel}", sel);
                            await el.ClickAsync(new ElementHandleClickOptions { Timeout = 5000 });
                            break;
                        }
                    }
                    catch { }
                }

                using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await m3u8Found.Task.WaitAsync(cts2.Token); }
                catch (OperationCanceledException) { }
            }

            // Fallback: costruisci master dalla rendition
            if (string.IsNullOrEmpty(m3u8Url))
            {
                _logger.LogWarning("[SC] ❌ Nessun master. Totale req vix: {N}", vixRequests.Count);

                var firstRendition = vixRequests
                    .FirstOrDefault(u => u.Contains("vixcloud.co/playlist") && u.Contains("type=video"));

                if (!string.IsNullOrEmpty(firstRendition))
                {
                    var mId = Regex.Match(firstRendition, @"/playlist/(\d+)");
                    if (mId.Success)
                    {
                        var tokenPart = "";
                        var qIdx = firstRendition.IndexOf('?');
                        if (qIdx >= 0)
                        {
                            var qs = firstRendition.Substring(qIdx + 1)
                                .Split('&')
                                .Where(kv => kv.StartsWith("token=", StringComparison.OrdinalIgnoreCase)
                                          || kv.StartsWith("expires=", StringComparison.OrdinalIgnoreCase)
                                          || kv.StartsWith("h=", StringComparison.OrdinalIgnoreCase))
                                .ToArray();
                            if (qs.Length > 0) tokenPart = "?" + string.Join("&", qs);
                        }

                        m3u8Url = $"https://vixcloud.co/playlist/{mId.Groups[1].Value}{tokenPart}";
                        _logger.LogWarning("[SC] Tentativo fallback master: {Url}", m3u8Url);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SC] Errore estrazione m3u8");
        }
        finally
        {
            await page.CloseAsync();
        }

        return m3u8Url;
    }

    private async Task<int> FindEpisodeIdAsync(string seriesLink, int seasonNumber, int episodeNumber)
    {
        var seasonUrl = seriesLink.TrimEnd('/') + $"/season-{seasonNumber}";
        var (dataPage, page) = await NavigateAndReadDataPageAsync(seasonUrl);
        try
        {
            if (dataPage == null) return 0;
            if (!dataPage.Value.TryGetProperty("props", out var props)) return 0;
            if (!props.TryGetProperty("loadedSeason", out var ls)) return 0;
            if (!ls.TryGetProperty("episodes", out var eps)) return 0;
            if (eps.ValueKind != JsonValueKind.Array) return 0;

            foreach (var e in eps.EnumerateArray())
            {
                if (e.TryGetProperty("number", out var n) && n.GetInt32() == episodeNumber)
                {
                    if (e.TryGetProperty("id", out var id))
                        return id.GetInt32();
                }
            }
        }
        finally
        {
            if (page != null) await page.CloseAsync();
        }
        return 0;
    }

    // ============================================================
    //  NAVIGAZIONE + JSON INERTIA
    // ============================================================

    private async Task<(JsonElement? data, IPage? page)> NavigateAndReadDataPageAsync(string url)
    {
        await InitializeAsync();
        if (_context == null) return (null, null);

        var page = await NewStealthPageAsync();
        try
        {
            _logger.LogInformation("[SC] Navigazione: {Url}", url);

            var response = await page.GotoAsync(url, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 45000
            });

            _logger.LogInformation("[SC] Response: {Status} {FinalUrl}",
                response?.Status, response?.Url);

            var data = await ReadDataPageFromResponseAsync(response);
            if (data == null)
            {
                _logger.LogWarning("[SC] data-page non letto da {Url}", url);
                await page.CloseAsync();
                return (null, null);
            }

            return (data, page);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SC] Errore navigazione verso {Url}", url);
            try { await page.CloseAsync(); } catch { }
            return (null, null);
        }
    }

    private async Task<JsonElement?> ReadDataPageFromResponseAsync(IResponse? response)
    {
        if (response == null) return null;

        try
        {
            var html = await response.TextAsync();

            var match = Regex.Match(html,
                @"<div[^>]*\bid\s*=\s*[""']app[""'][^>]*\bdata-page\s*=\s*[""']([^""']+)[""']",
                RegexOptions.Singleline);

            if (!match.Success)
            {
                match = Regex.Match(html,
                    @"\bdata-page\s*=\s*[""']([^""']+)[""']",
                    RegexOptions.Singleline);
            }

            if (!match.Success) return null;

            var raw = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
            var doc = JsonDocument.Parse(raw);

            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SC] Errore parsing data-page");
            return null;
        }
    }

    // ============================================================
    //  PARSING JSON
    // ============================================================

    private List<MovieSearchResult> ExtractTitlesFromJson(JsonElement root)
    {
        var results = new List<MovieSearchResult>();
        var seen = new HashSet<int>();
        WalkJsonForTitles(root, results, seen);
        return results;
    }

    private void WalkJsonForTitles(JsonElement el, List<MovieSearchResult> results, HashSet<int> seen)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                var parsed = TryParseAsTitle(el);
                if (parsed != null)
                {
                    var id = int.Parse(parsed.TitleId);
                    if (seen.Add(id)) results.Add(parsed);
                }
                foreach (var prop in el.EnumerateObject())
                    WalkJsonForTitles(prop.Value, results, seen);
                break;

            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    WalkJsonForTitles(item, results, seen);
                break;
        }
    }

    private MovieSearchResult? TryParseAsTitle(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        if (!el.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) return null;
        if (!el.TryGetProperty("slug", out var slugEl) || slugEl.ValueKind != JsonValueKind.String) return null;
        if (!el.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String) return null;
        if (!el.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) return null;

        var id = idEl.GetInt32();
        var slug = slugEl.GetString() ?? "";
        var name = nameEl.GetString() ?? "";
        var type = typeEl.GetString() ?? "";

        if (id <= 0 || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(slug)) return null;
        if (type != "tv" && type != "movie") return null;

        var link = $"{BaseUrl}/it/titles/{id}-{slug}";

        return new MovieSearchResult
        {
            Title = name,
            Link = link,
            PosterUrl = ExtractPosterUrl(el),
            Year = ExtractYear(el),
            IsSeries = type == "tv",
            TitleId = id.ToString()
        };
    }

    private static string? ExtractYear(JsonElement el)
    {
        foreach (var key in new[]
        {
            "release_date", "release_date_it",
            "first_air_date", "first_air_date_it",
            "last_air_date", "last_air_date_it"
        })
        {
            if (el.TryGetProperty(key, out var rd) && rd.ValueKind == JsonValueKind.String)
            {
                var raw = rd.GetString();
                if (!string.IsNullOrEmpty(raw) && raw.Length >= 4
                    && int.TryParse(raw.Substring(0, 4), out _))
                    return raw.Substring(0, 4);
            }
        }

        if (el.TryGetProperty("translations", out var tr) && tr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tr.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                if (!t.TryGetProperty("key", out var k) || k.ValueKind != JsonValueKind.String) continue;
                if (!t.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.String) continue;

                var key = k.GetString() ?? "";
                if (key is "release_date" or "release_date_it"
                         or "first_air_date" or "first_air_date_it"
                         or "last_air_date" or "last_air_date_it")
                {
                    var raw = v.GetString();
                    if (!string.IsNullOrEmpty(raw) && raw.Length >= 4
                        && int.TryParse(raw.Substring(0, 4), out _))
                        return raw.Substring(0, 4);
                }
            }
        }

        return null;
    }

    private string? ExtractPosterUrl(JsonElement el)
    {
        if (!el.TryGetProperty("images", out var imgs) || imgs.ValueKind != JsonValueKind.Array)
            return null;

        string? coverFallback = null;

        foreach (var img in imgs.EnumerateArray())
        {
            if (img.ValueKind != JsonValueKind.Object) continue;
            if (!img.TryGetProperty("filename", out var fn) || fn.ValueKind != JsonValueKind.String) continue;

            var filename = fn.GetString();
            if (string.IsNullOrEmpty(filename)) continue;

            var type = img.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String
                ? ty.GetString() : null;

            var url = CdnBase + filename;

            if (type == "poster") return url;
            if (type == "cover" && coverFallback == null) coverFallback = url;
        }

        return coverFallback;
    }

    private void LogPropsStructure(JsonElement dataPage)
    {
        try
        {
            if (!dataPage.TryGetProperty("props", out var props))
            {
                _logger.LogWarning("[SC] props assente nel data-page");
                return;
            }

            var component = dataPage.TryGetProperty("component", out var comp)
                ? comp.GetString() : "?";
            _logger.LogInformation("[SC] Component: {Comp}", component);

            var keys = new List<string>();
            foreach (var p in props.EnumerateObject())
            {
                var kind = p.Value.ValueKind switch
                {
                    JsonValueKind.Array => $"Array[{p.Value.GetArrayLength()}]",
                    JsonValueKind.Object => "Object",
                    JsonValueKind.String => "String",
                    JsonValueKind.Number => "Number",
                    JsonValueKind.True or JsonValueKind.False => "Bool",
                    JsonValueKind.Null => "Null",
                    _ => p.Value.ValueKind.ToString()
                };
                keys.Add($"{p.Name}:{kind}");
            }
            _logger.LogInformation("[SC] props keys: {Keys}", string.Join(", ", keys));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SC] Errore LogPropsStructure");
        }
    }

    private static string ExtractTitleIdFromUrl(string url)
    {
        var m = Regex.Match(url, @"/titles/(\d+)-");
        return m.Success ? m.Groups[1].Value : "";
    }

    // ============================================================
    //  QUALITÀ
    // ============================================================

    public async Task<List<VideoQuality>> GetAvailableQualitiesAsync(
        string contentLink, bool isSeries, int seasonNumber, int episodeNumber)
    {
        var qualities = new List<VideoQuality>();

        string? masterUrl;
        if (!isSeries)
            masterUrl = await GetMovieStreamUrlAsync(contentLink);
        else
            masterUrl = await GetVideoUrlAsync(contentLink, true, seasonNumber, episodeNumber);

        if (string.IsNullOrEmpty(masterUrl))
        {
            _logger.LogWarning("[SC] GetAvailableQualities: nessun master url");
            return qualities;
        }

        qualities = await ParseMasterAsync(masterUrl, CancellationToken.None);

        _logger.LogInformation("[SC] Qualità trovate: {N} ({Names})",
            qualities.Count,
            string.Join(", ", qualities.Select(q => q.Name)));

        return qualities;
    }

    public async Task<(string? VideoUrl, string? AudioUrl)> ResolveQualityAsync(
        string masterUrl, string qualityName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(qualityName) || qualityName.StartsWith("Auto"))
            return (masterUrl, null);

        var parsed = await ParseMasterFullAsync(masterUrl, ct);

        var videoMatch = parsed.Videos
            .FirstOrDefault(v => string.Equals(v.Name, qualityName, StringComparison.OrdinalIgnoreCase));

        if (videoMatch == null)
        {
            _logger.LogWarning("[SC] Qualità {Q} non trovata, uso master", qualityName);
            return (masterUrl, null);
        }

        var audio = parsed.Audios.FirstOrDefault(a =>
            a.Name.Contains("ita", StringComparison.OrdinalIgnoreCase))
            ?? parsed.Audios.FirstOrDefault();

        return (videoMatch.Url, audio?.Url);
    }

    private async Task<List<VideoQuality>> ParseMasterAsync(string masterUrl, CancellationToken ct)
    {
        var full = await ParseMasterFullAsync(masterUrl, ct);
        return full.Videos;
    }

    private class MasterPlaylist
    {
        public List<VideoQuality> Videos { get; } = new();
        public List<VideoQuality> Audios { get; } = new();
    }

    private async Task<MasterPlaylist> ParseMasterFullAsync(string masterUrl, CancellationToken ct)
    {
        var result = new MasterPlaylist();
        try
        {
            if (_context == null) return result;

            string? text = null;

            try
            {
                var apiResp = await _context.APIRequest.GetAsync(masterUrl, new APIRequestContextOptions
                {
                    Timeout = 30000
                });

                if (!apiResp.Ok) return result;

                text = await apiResp.TextAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SC] ParseMaster: errore download master");
                return result;
            }

            if (string.IsNullOrEmpty(text)) return result;

            var lines = text.Split('\n');
            var baseUri = new Uri(masterUrl);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();

                if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) &&
                    line.Contains("TYPE=AUDIO", StringComparison.OrdinalIgnoreCase))
                {
                    var nameM = Regex.Match(line, @"NAME=""([^""]+)""");
                    var uriM = Regex.Match(line, @"URI=""([^""]+)""");
                    if (uriM.Success)
                    {
                        var audioUrl = uriM.Groups[1].Value;
                        if (!audioUrl.StartsWith("http"))
                            audioUrl = new Uri(baseUri, audioUrl).ToString();

                        result.Audios.Add(new VideoQuality
                        {
                            Name = nameM.Success ? nameM.Groups[1].Value : "audio",
                            Url = audioUrl
                        });
                    }
                    continue;
                }

                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    var resMatch = Regex.Match(line, @"RESOLUTION=(\d+)x(\d+)");
                    var bwMatch = Regex.Match(line, @"BANDWIDTH=(\d+)");

                    string name = "";
                    if (resMatch.Success) name = $"{resMatch.Groups[2].Value}p";
                    else if (bwMatch.Success)
                        name = $"{long.Parse(bwMatch.Groups[1].Value) / 1_000_000}Mbps";

                    long bw = bwMatch.Success ? long.Parse(bwMatch.Groups[1].Value) : 0;

                    for (int j = i + 1; j < lines.Length; j++)
                    {
                        var next = lines[j].Trim();
                        if (string.IsNullOrEmpty(next)) continue;
                        if (next.StartsWith("#")) break;

                        var variantUrl = next;
                        if (!variantUrl.StartsWith("http"))
                            variantUrl = new Uri(baseUri, variantUrl).ToString();

                        result.Videos.Add(new VideoQuality
                        {
                            Name = name,
                            Url = variantUrl,
                            Bandwidth = bw
                        });
                        break;
                    }
                }
            }

            result.Videos.Sort((a, b) => b.Bandwidth.CompareTo(a.Bandwidth));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SC] Errore parsing master m3u8");
        }
        return result;
    }

    // ============================================================
    //  DISPOSE
    // ============================================================

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_context != null)
            {
                await _context.CloseAsync();
                _context = null;
            }

            if (_browser != null)
            {
                await _browser.CloseAsync();
                await _browser.DisposeAsync();
                _browser = null;
            }

            _playwright?.Dispose();
            _playwright = null;
            _initialized = false;
            _currentProxyKey = "(none)";
            _cachedCookiesNetscape = null;
            _cachedCookiesAt = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SC] Errore durante Dispose");
        }
    }

    public async Task NotifyProxyChangedAsync()
    {
        try
        {
            await DisposeAsync();
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SC] Errore durante NotifyProxyChanged");
        }
    }

    // ============================================================
    //  EXPORT COOKIE (senza warmup cf_clearance)
    // ============================================================

    public async Task<string?> GetSessionCookiesNetscapeAsync()
    {
        await InitializeAsync();
        if (_context == null) return null;

        // Cache 5 minuti
        if (_cachedCookiesNetscape != null &&
            DateTime.UtcNow - _cachedCookiesAt < CookieCacheTtl)
        {
            _logger.LogWarning("[SC] Cookie da cache ({N} char, {Age}s fa)",
                _cachedCookiesNetscape.Length,
                (int)(DateTime.UtcNow - _cachedCookiesAt).TotalSeconds);
            return _cachedCookiesNetscape;
        }

        try
        {
            var allCookies = await _context.CookiesAsync();

            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");

            foreach (var c in allCookies)
            {
                if (string.IsNullOrEmpty(c.Domain)) continue;

                var domain = c.Domain.StartsWith(".") ? c.Domain : "." + c.Domain;
                var flag = c.Domain.StartsWith(".") ? "TRUE" : "FALSE";
                var secure = c.Secure ? "TRUE" : "FALSE";
                var expiry = (long)(c.Expires > 0
                    ? c.Expires
                    : DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeSeconds());

                sb.AppendLine($"{domain}\t{flag}\t{c.Path}\t{secure}\t{expiry}\t{c.Name}\t{c.Value}");
            }

            _cachedCookiesNetscape = sb.ToString();
            _cachedCookiesAt = DateTime.UtcNow;

            _logger.LogWarning("[SC] Esportati {N} cookie (cache 5 min)",
                allCookies.Count);

            return _cachedCookiesNetscape;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SC] Errore export cookie");
            return null;
        }
    }
}