using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Web;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Domains;
using DownloadManager.Services.Proxy;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Streaming.Cb01;

/// <summary>Stato dell'account Maxstream letto dalla pagina "?op=my_account".</summary>
public record MaxstreamAccountInfo(bool LoggedIn, bool IsPremium, DateTime? PremiumExpire, string Message)
{
    public int DaysLeft => PremiumExpire is { } d ? Math.Max(0, (d.Date - DateTime.Today).Days) : 0;
}

/// <summary>Link scaricabile scelto tra le varianti premium (Mirror Original, Original, HD, Standard).</summary>
public record MaxstreamDownload(string Url, string FileName, long? SizeBytes, string Variant);

/// <summary>
/// Accede a Maxstream con l'account premium ed estrae il file originale.
///
/// Da loggati i link uprot portano a /mypremium/ (niente Cloudflare), quindi di norma basta
/// HTTP diretto dall'IP dell'utente. FlareSolverr serve solo se Cloudflare blocca la connessione
/// diretta. Il link di download vale per l'IP che l'ha chiesto: se è attivo il proxy CB01,
/// lo usano sia la connessione diretta sia FlareSolverr, e i download (host-cdn → proxy CB01).
/// </summary>
public class MaxstreamService : IAsyncDisposable
{
    private readonly ILogger<MaxstreamService> _logger;
    private readonly IProxyProvider _proxyProvider;
    private readonly SettingsService _settings;
    private readonly DomainResolver _domains;

    private HttpClient? _fsHttp;
    private HttpClient? _directHttp;
    private string? _directProxyKey;
    private string? _flareSolverrSession;
    private string? _sessionFlareUrl;
    private bool _loggedIn;
    private string? _loginBase;
    private bool _useFlare;

    // Una sessione è una sola scheda/cookie jar: due operazioni insieme si pestano i piedi
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string _username = string.Empty;
    private string _password = string.Empty;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static readonly Regex RxHostCdn = new(
        @"https?://[^""'\s<>]*host-cdn[^""'\s<>]*\.(?:m3u8|mp4|mkv|urlset)[^""'\s<>]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Le pagine Maxstream usano sia href="..." sia href='...'
    private static readonly Regex RxDownloadButton = new(
        @"<a\s[^>]*?href=['""]([^'""]+)['""][^>]*>\s*<button[^>]*>([^<]+)</button>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex RxFileName = new(
        @"(?:id=""fileTitle""|File\s+name\s*:)\s*[^>]*>\s*([^<]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxFileId = new(
        @"/(?:dpremium|mypremium|cast2|ddd)/([a-z0-9]{12})|/videostreax/[^/]+/([a-z0-9]{12})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxSize = new(
        @"([\d.,]+)\s*(Gb|Mb|Kb)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // <td>Premium expire:</td> <td><b>4 November 2026</b>
    private static readonly Regex RxPremiumExpire = new(
        @"Premium\s+expire:\s*</td>\s*<td[^>]*>\s*(?:<b>)?\s*([^<]+?)\s*<",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxLoginError = new(
        @"class=""(?:err|alert[^""]*danger)[^""]*""[^>]*>\s*([^<]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Scatta dopo ogni login o verifica: la tab Account aggiorna stato e scadenza.</summary>
    public event Action<MaxstreamAccountInfo>? AccountChanged;
    public MaxstreamAccountInfo? LastAccount { get; private set; }

    public MaxstreamService(ILogger<MaxstreamService> logger, IProxyProvider proxyProvider,
        SettingsService settings, DomainResolver domains)
    {
        _logger = logger;
        _proxyProvider = proxyProvider;
        _settings = settings;
        _domains = domains;
    }

    public void SetCredentials(string username, string password)
    {
        if (_username == username && _password == password) return;
        _username = username;
        _password = password;
        _loggedIn = false;
    }

    public bool HasCredentials => !string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password);

    private string FlareSolverrUrl => _settings.Current.FlareSolverrUrl?.Trim() ?? "";
    private bool HasFlareSolverr => !string.IsNullOrEmpty(FlareSolverrUrl);
    private string ConfiguredBase => _domains.GetBaseUrl(DomainResolver.Maxstream);

    private static string BaseOf(string url)
    {
        var u = new Uri(url);
        return $"{u.Scheme}://{u.Host}";
    }

    // ============================================================
    //  API PER LA TAB ACCOUNT
    // ============================================================

    /// <summary>Nuova sessione e nuovo login sul dominio configurato; restituisce stato e scadenza premium.</summary>
    public async Task<MaxstreamAccountInfo> LoginAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await ResetSessionAsync();
            return await LoginCoreAsync(ConfiguredBase, ct);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Rilegge la pagina account con la sessione corrente, senza rifare il login.</summary>
    public async Task<MaxstreamAccountInfo> CheckSessionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!_loggedIn || _loginBase == null)
                return Publish(new MaxstreamAccountInfo(false, false, null, "Nessuna sessione attiva: premi Accedi."));

            var (html, _) = await GetPageAsync($"{_loginBase}/?op=my_account", null, ct);
            var info = ParseAccount(html);
            if (!info.LoggedIn) _loggedIn = false;
            return Publish(info);
        }
        finally { _gate.Release(); }
    }

    public async Task LogoutAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await ResetSessionAsync();
            Publish(new MaxstreamAccountInfo(false, false, null, "Disconnesso."));
        }
        finally { _gate.Release(); }
    }

    private MaxstreamAccountInfo Publish(MaxstreamAccountInfo info)
    {
        LastAccount = info;
        AccountChanged?.Invoke(info);
        return info;
    }

    // ============================================================
    //  TRASPORTO: HTTP diretto (default) o FlareSolverr (riserva)
    // ============================================================

    private ProxyConfig? ActiveProxy => _proxyProvider.GetActive(ProviderKeys.Cb01);

    /// <summary>Motivo dell'ultimo link non ottenuto, in parole semplici (mostrato nella coda download).</summary>
    public string? LastProblem { get; private set; }

    /// <summary>Messaggio per Cloudflare che blocca: col proxy CB01 attivo la colpa è del proxy.</summary>
    private string CloudflareMessage(string fallback) => ActiveProxy is { } p
        ? $"Maxstream (Cloudflare) blocca il proxy CB01 {p.Host}: i proxy di datacenter/VPN non superano la sua verifica. " +
          "Spegni il proxy per CB01 nella tab Proxy (con la connessione di casa funziona) o usa un proxy residenziale."
        : fallback;

    private HttpClient GetDirectHttpClient()
    {
        // Il client tiene i cookie di login: si ricrea solo se cambia il proxy (e allora serve un nuovo login)
        var proxy = ActiveProxy;
        var key = proxy?.ToString() ?? "(none)";
        if (_directHttp != null && key == _directProxyKey) return _directHttp;

        if (_directHttp != null)
        {
            _logger.LogInformation("[MAXSTREAM] Proxy CB01 cambiato ({Old} → {New}): nuovo login", _directProxyKey, key);
            _directHttp.Dispose();
            _loggedIn = false;
        }

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        if (proxy != null)
        {
            var scheme = proxy.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
            handler.Proxy = new WebProxy($"{scheme}://{proxy.Host}:{proxy.Port}");
            if (proxy.Auth == ProxyAuthType.UserPassword)
                handler.Proxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
            handler.UseProxy = true;
        }

        _directHttp = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _directHttp.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _directProxyKey = key;
        return _directHttp;
    }

    /// <summary>(html, URL finale dopo i redirect). Html null = nessuna risposta.</summary>
    private async Task<(string? Html, string? FinalUrl)> GetPageAsync(string url, string? referer, CancellationToken ct, bool ajax = false)
    {
        if (_useFlare) return await FsSendAsync("request.get", url, null, ct);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (referer != null) req.Headers.Referrer = new Uri(referer);
            if (ajax) req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            using var resp = await GetDirectHttpClient().SendAsync(req, ct);
            return (await resp.Content.ReadAsStringAsync(ct), resp.RequestMessage?.RequestUri?.ToString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("[MAXSTREAM] {Url}: {Msg}", url, ex.Message);
            return (null, null);
        }
    }

    private async Task<string?> PostPageAsync(string url, string postData, CancellationToken ct, bool ajax = false)
    {
        if (_useFlare) return (await FsSendAsync("request.post", url, postData, ct)).Html;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            if (ajax) req.Headers.Add("X-Requested-With", "XMLHttpRequest");
            using var resp = await GetDirectHttpClient().SendAsync(req, ct);
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("[MAXSTREAM] {Url}: {Msg}", url, ex.Message);
            return null;
        }
    }

    /// <summary>Cloudflare blocca l'IP (error 1005, "Sorry, you have been blocked") o chiede la sfida JS.</summary>
    private static bool IsCloudflareBlock(string? html) =>
        html != null && (html.Contains("Just a moment") || html.Contains("Ci siamo quasi") ||
                         html.Contains("error code: 1005") || html.Contains("you have been blocked"));

    // ---------------- FlareSolverr ----------------

    private async Task<FlareSolverrResponse?> FlareSolverrRequestAsync(
        FsRequest payload, CancellationToken ct, int timeoutMs = 120_000, string? flareUrl = null)
    {
        _fsHttp ??= new HttpClient { Timeout = TimeSpan.FromMilliseconds(120_000 + 30_000) };
        var apiUrl = $"{(flareUrl ?? FlareSolverrUrl).TrimEnd('/')}/v1";

        var json = JsonSerializer.Serialize(payload, FsJsonOptions);
        _logger.LogInformation("[FLARESOLVERR] {Cmd} {Url}", payload.Cmd, payload.Url ?? "");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs + 30_000);
        using var content = new StringContent(json, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json"));
        HttpResponseMessage resp;
        try
        {
            resp = await _fsHttp.PostAsync(apiUrl, content, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("[FLARESOLVERR] Non raggiungibile su {Url}: {Msg}. È avviato?", apiUrl, ex.Message);
            return null;
        }
        var body = await resp.Content.ReadAsStringAsync(ct);

        FlareSolverrResponse? result = null;
        try { result = JsonSerializer.Deserialize<FlareSolverrResponse>(body, FsJsonOptions); } catch { }

        if (!resp.IsSuccessStatusCode || result?.Status != "ok")
        {
            _logger.LogWarning("[FLARESOLVERR] HTTP {Code}, status={Status}: {Msg}", (int)resp.StatusCode,
                result?.Status, result?.Message ?? (body.Length > 300 ? body[..300] : body));
            return null;
        }
        return result;
    }

    private async Task EnsureSessionAsync(CancellationToken ct)
    {
        // Sessione e login vivono dentro un'istanza FlareSolverr: se l'URL cambia vanno rifatti
        if (_flareSolverrSession != null && _sessionFlareUrl != FlareSolverrUrl)
        {
            _logger.LogInformation("[FLARESOLVERR] URL cambiato ({Old} → {New}): nuova sessione e nuovo login",
                _sessionFlareUrl, FlareSolverrUrl);
            await ResetSessionAsync();
            _useFlare = true;
        }
        if (_flareSolverrSession != null) return;

        _sessionFlareUrl = FlareSolverrUrl;
        _flareSolverrSession = $"maxstream_{Guid.NewGuid():N}"[..24];

        // Il browser di FlareSolverr esce dallo stesso proxy CB01 dei download (link legati all'IP)
        FsProxy? proxy = null;
        if (ActiveProxy is { } p)
        {
            var scheme = p.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
            proxy = new FsProxy
            {
                Url = $"{scheme}://{p.Host}:{p.Port}",
                Username = p.Auth == ProxyAuthType.UserPassword ? p.Username : null,
                Password = p.Auth == ProxyAuthType.UserPassword ? p.Password : null
            };
        }
        await FlareSolverrRequestAsync(new FsRequest { Cmd = "sessions.create", Session = _flareSolverrSession, Proxy = proxy }, ct);
    }

    private async Task<(string? Html, string? FinalUrl)> FsSendAsync(string cmd, string url, string? postData, CancellationToken ct, int? waitSeconds = null)
    {
        await EnsureSessionAsync(ct);
        var result = await FlareSolverrRequestAsync(new FsRequest
        {
            Cmd = cmd,
            Url = url,
            Session = _flareSolverrSession,
            PostData = postData,
            MaxTimeout = 120000,
            WaitInSeconds = waitSeconds
        }, ct);
        return (result?.Solution?.Response, result?.Solution?.Url);
    }

    private async Task ResetSessionAsync()
    {
        _loggedIn = false;
        _loginBase = null;

        // Ogni sessione è un Chrome aperto dentro FlareSolverr: va chiusa sul server dove è stata creata
        if (_flareSolverrSession != null && !string.IsNullOrEmpty(_sessionFlareUrl))
        {
            try
            {
                await FlareSolverrRequestAsync(new FsRequest { Cmd = "sessions.destroy", Session = _flareSolverrSession },
                    CancellationToken.None, 10_000, _sessionFlareUrl);
            }
            catch { }
        }
        _flareSolverrSession = null;
        _useFlare = false;

        // In modalità diretta i cookie stanno nel client: buttarlo equivale a uscire
        _directHttp?.Dispose();
        _directHttp = null;
    }

    // ============================================================
    //  LOGIN PREMIUM
    // ============================================================

    private async Task EnsureLoggedInAsync(string baseUrl, CancellationToken ct)
    {
        if (_loggedIn && _loginBase == baseUrl) return;
        await LoginCoreAsync(baseUrl, ct);
    }

    private async Task<MaxstreamAccountInfo> LoginCoreAsync(string baseUrl, CancellationToken ct)
    {
        _loggedIn = false;
        if (!HasCredentials)
        {
            _logger.LogWarning("[MAXSTREAM] Nessuna credenziale premium: inseriscile nella tab Account, senza login Maxstream mostra il captcha.");
            return Publish(new MaxstreamAccountInfo(false, false, null, "Credenziali Maxstream mancanti."));
        }

        // La home fa passare Cloudflare e imposta i cookie prima del POST.
        // Prima via diretta; se Cloudflare la blocca, FlareSolverr (se configurato).
        var (home, _) = await GetPageAsync(baseUrl + "/", null, ct);
        if (!_useFlare && (home == null || IsCloudflareBlock(home)))
        {
            if (!HasFlareSolverr)
                return Publish(new MaxstreamAccountInfo(false, false, null,
                    CloudflareMessage("Cloudflare blocca la connessione diretta a Maxstream: configura FlareSolverr o un proxy residenziale CB01.")));

            _logger.LogWarning("[MAXSTREAM] Cloudflare blocca la connessione diretta, passo a FlareSolverr {Url}", FlareSolverrUrl);
            _useFlare = true;
            await GetPageAsync(baseUrl + "/", null, ct);
        }

        _logger.LogInformation("[MAXSTREAM] Login premium come {User} su {Base} ({Mode})...",
            _username, baseUrl, _useFlare ? "FlareSolverr " + FlareSolverrUrl : "diretto");

        var postData = $"op=login&redirect=&login={Uri.EscapeDataString(_username)}&password={Uri.EscapeDataString(_password)}";
        var html = await PostPageAsync(baseUrl + "/", postData, ct);

        var info = ParseAccount(html);
        // La home è passata ma il login no: Maxstream ha rimandato alla sua pagina di accesso protetta
        // (succede dopo molti accessi ravvicinati o da IP diversi), non è un blocco della connessione
        if (!info.LoggedIn && !_useFlare && IsCloudflareBlock(html))
            info = info with
            {
                Message = CloudflareMessage(
                    "Maxstream ha messo in verifica il login di questo account (troppi accessi ravvicinati o da IP diversi). " +
                    "Accedi una volta dal browser su maxstream.video, poi riprova; di solito si sblocca da solo dopo un po'.")
            };
        if (!info.LoggedIn) LastProblem = info.Message;
        if (info.LoggedIn)
        {
            _loggedIn = true;
            _loginBase = baseUrl;
            _logger.LogInformation("[MAXSTREAM] Login riuscito: {Msg}", info.Message);
            if (!info.IsPremium)
                _logger.LogWarning("[MAXSTREAM] L'account non è premium: i link CB01 chiederanno il captcha.");
            else if (!_useFlare)
                await EnsureWhitelistedAsync(baseUrl, ct);
        }
        else
        {
            _logger.LogWarning("[MAXSTREAM] Login fallito: {Msg}", info.Message);
        }
        return Publish(info);
    }

    /// <summary>
    /// "Uprot Unlocker" (solo premium): mette in whitelist l'IP corrente, come il pulsante Unlock
    /// della pagina account. Evita il captcha di uprot quando l'IP di casa cambia.
    /// </summary>
    private async Task EnsureWhitelistedAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            var (check, _) = await GetPageAsync($"{baseUrl}/api/sys/check_whitelist.php", $"{baseUrl}/?op=my_account", ct, ajax: true);
            if (check != null && check.Contains("success")) return;

            var add = await PostPageAsync($"{baseUrl}/api/sys/add_whitelist.php", "", ct, ajax: true);
            _logger.LogInformation("[MAXSTREAM] IP aggiunto alla whitelist uprot: {Resp}", add?.Trim() ?? "(nessuna risposta)");
        }
        catch (Exception ex)
        {
            _logger.LogInformation("[MAXSTREAM] Whitelist uprot non verificata: {Msg}", ex.Message);
        }
    }

    /// <summary>Loggato = c'è il link "op=logout"; la scadenza è nella riga "Premium expire:".</summary>
    private static MaxstreamAccountInfo ParseAccount(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return new(false, false, null, "Nessuna risposta da Maxstream: controlla connessione, dominio (tab Domini) e FlareSolverr.");

        if (!html.Contains("op=logout", StringComparison.OrdinalIgnoreCase))
        {
            if (IsCloudflareBlock(html))
                return new(false, false, null, "Cloudflare blocca la connessione: configura FlareSolverr o un proxy residenziale.");
            var err = RxLoginError.Match(html);
            var msg = err.Success ? WebUtility.HtmlDecode(err.Groups[1].Value).Trim() : "controlla username e password";
            return new(false, false, null, $"Login non riuscito: {msg}");
        }

        DateTime? expire = null;
        var m = RxPremiumExpire.Match(html);
        if (m.Success)
        {
            var text = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            string[] formats = { "d MMMM yyyy", "dd MMMM yyyy", "MMMM d, yyyy", "d MMM yyyy", "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss" };
            if (DateTime.TryParseExact(text, formats, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var d) ||
                DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out d))
                expire = d;
        }

        var it = CultureInfo.GetCultureInfo("it-IT");
        if (expire is { } e && e.Date >= DateTime.Today)
            return new(true, true, e, $"Premium attivo fino al {e.ToString("d MMMM yyyy", it)}");
        if (expire is { } past)
            return new(true, false, past, $"Premium scaduto il {past.ToString("d MMMM yyyy", it)}");
        // Data non leggibile ma la pagina offre il rinnovo: è un account premium
        if (html.Contains("Extend Premium account", StringComparison.OrdinalIgnoreCase))
            return new(true, true, null, "Premium attivo (scadenza non leggibile)");
        return new(true, false, null, "Account gratuito (nessun premium)");
    }

    // ============================================================
    //  ESTRAZIONE LINK DI DOWNLOAD
    // ============================================================

    /// <summary>
    /// Da un link uprot (msf/msfi) o Maxstream restituisce il file migliore tra le varianti premium.
    /// Null se il file non esiste più o non è raggiungibile.
    /// </summary>
    public async Task<MaxstreamDownload?> ResolveDownloadAsync(string link, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var loginBase = ConfiguredBase;
            const int maxAttempts = 3;
            string? lastProblem = null;
            bool lastCloudflare = false;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await EnsureLoggedInAsync(loginBase, ct);

                _logger.LogInformation("[MAXSTREAM] Apro {Url}...", link);
                var (html, finalUrl) = await GetPageAsync(link, null, ct);

                // uprot ha portato su un dominio Maxstream diverso da quello configurato: login lì
                if (finalUrl != null && Uri.TryCreate(finalUrl, UriKind.Absolute, out var fu) &&
                    fu.Host.Contains('.') && !fu.Host.Contains("uprot", StringComparison.OrdinalIgnoreCase) &&
                    DomainResolver.HostOf(finalUrl) != DomainResolver.HostOf(loginBase))
                {
                    _logger.LogWarning("[MAXSTREAM] Il link porta a {Page} ma nella tab Domini Maxstream è {Cfg}: aggiornalo.",
                        BaseOf(finalUrl), ConfiguredBase);
                    loginBase = BaseOf(finalUrl);
                    _loggedIn = false;
                    continue;
                }

                var problem = DiagnosePage(html, out bool retryable);
                if (problem == null)
                    return await ExtractDownloadAsync(html!, finalUrl ?? link, loginBase, ct);
                lastProblem = problem;
                lastCloudflare = IsCloudflareBlock(html);

                _logger.LogWarning("[MAXSTREAM] {Problem} (tentativo {N}/{Max})", problem, attempt, maxAttempts);
                if (!retryable) return null;

                var cfBlock = IsCloudflareBlock(html);
                await ResetSessionAsync();
                // Cloudflare sulla via diretta: il prossimo tentativo passa da FlareSolverr
                if (cfBlock && HasFlareSolverr) _useFlare = true;
            }

            _logger.LogWarning("[MAXSTREAM] Rinuncio a {Url}: controlla credenziali premium, connessione e FlareSolverr.", link);
            LastProblem = lastCloudflare
                ? CloudflareMessage("Cloudflare blocca le pagine di Maxstream: riprova più tardi o configura FlareSolverr.")
                : lastProblem ?? "Link Maxstream non disponibile.";
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Null = pagina video valida. Il captcha "upcaptcha" compare quando la sessione premium non è attiva.</summary>
    private static string? DiagnosePage(string? html, out bool retryable)
    {
        retryable = true;
        if (string.IsNullOrEmpty(html)) return "Pagina vuota (connessione o sessione non valida)";
        if (html.Contains("File not found") || html.Contains("was deleted"))
        {
            retryable = false;
            return "File non trovato o cancellato su Maxstream";
        }
        if (IsCloudflareBlock(html)) return "Cloudflare blocca la pagina";
        if (html.Contains("upcaptcha", StringComparison.OrdinalIgnoreCase))
            return "Maxstream chiede il captcha: sessione premium non attiva";
        return null;
    }

    private async Task<MaxstreamDownload?> ExtractDownloadAsync(string html, string pageUrl, string baseUrl, CancellationToken ct)
    {
        var pageName = RxFileName.Match(html) is { Success: true } fm ? WebUtility.HtmlDecode(fm.Groups[1].Value).Trim() : null;

        var idMatch = RxFileId.Match(html);
        if (!idMatch.Success) idMatch = RxFileId.Match(pageUrl);
        var fileId = idMatch.Success ? (idMatch.Groups[1].Success ? idMatch.Groups[1].Value : idMatch.Groups[2].Value) : null;

        // Le varianti scaricabili (originale, HD, standard) arrivano dall'AJAX della pagina /dpremium/
        if (fileId != null)
        {
            string? ajax;
            if (_useFlare)
            {
                // Il browser di FlareSolverr non manda il Referer richiesto dall'AJAX: si apre la pagina
                // /dpremium/ e si aspetta che il suo script inserisca i pulsanti
                (ajax, _) = await FsSendAsync("request.get", $"{baseUrl}/dpremium/{fileId}", null, ct, waitSeconds: 3);
            }
            else
            {
                var hyl = Convert.ToBase64String(Encoding.UTF8.GetBytes($"15000|{fileId}|1|{_username}"));
                (ajax, _) = await GetPageAsync($"{baseUrl}/download/insidepremiumx.php?hyl={hyl}",
                    $"{baseUrl}/dpremium/{fileId}", ct, ajax: true);
            }
            var best = ajax != null ? PickBestVariant(ajax) : null;
            if (best != null)
            {
                _logger.LogInformation("[MAXSTREAM] {Name}: {Variant} ({Size}) → {Url}", best.FileName, best.Variant,
                    best.SizeBytes is { } s ? $"{s / 1048576.0:F0} MB" : "?", best.Url);
                return best;
            }
            _logger.LogWarning("[MAXSTREAM] Nessuna variante premium per {Id}, uso lo streaming", fileId);
        }

        // Riserva: playlist HLS dello streaming (qualità standard)
        var hls = RxHostCdn.Match(html);
        if (hls.Success)
        {
            var name = pageName ?? $"maxstream_{fileId}";
            _logger.LogInformation("[MAXSTREAM] {Name}: streaming HLS → {Url}", name, hls.Value);
            return new MaxstreamDownload(hls.Value, name, null, "Streaming (HLS)");
        }

        var snippet = html.Length > 1500 ? html[..1500] : html;
        _logger.LogWarning("[MAXSTREAM] Nessun link trovato. HTML: {Html}", snippet);
        return null;
    }

    /// <summary>
    /// Ordine: Mirror Original (ms-*, file originale) → Download Original (orig.host-cdn) →
    /// High Definition → Standard Quality. "Watch in streaming" non è un file.
    /// </summary>
    private MaxstreamDownload? PickBestVariant(string html)
    {
        var options = new List<(int Rank, MaxstreamDownload D)>();
        foreach (Match b in RxDownloadButton.Matches(html))
        {
            var url = WebUtility.HtmlDecode(b.Groups[1].Value).Trim();
            var label = WebUtility.HtmlDecode(b.Groups[2].Value).Trim();
            if (!url.Contains("host-cdn", StringComparison.OrdinalIgnoreCase)) continue;

            int rank =
                label.Contains("Mirror Original", StringComparison.OrdinalIgnoreCase) ? 0 :
                label.Contains("Original", StringComparison.OrdinalIgnoreCase) ? 1 :
                label.Contains("High Definition", StringComparison.OrdinalIgnoreCase) ? 2 :
                label.Contains("Standard", StringComparison.OrdinalIgnoreCase) ? 3 : 4;

            options.Add((rank, new MaxstreamDownload(url, FileNameFromUrl(url), ParseSize(label), label)));
        }
        return options.OrderBy(o => o.Rank).Select(o => o.D).FirstOrDefault();
    }

    /// <summary>Nome reale del file: parametro new_filename (link Original) o ultimo segmento del percorso (mirror).</summary>
    private static string FileNameFromUrl(string url)
    {
        var uri = new Uri(url);
        var q = HttpUtility.ParseQueryString(uri.Query)["new_filename"];
        if (!string.IsNullOrWhiteSpace(q)) return q.Trim();
        return Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "").Trim('/');
    }

    private static long? ParseSize(string label)
    {
        var m = RxSize.Match(label);
        if (!m.Success) return null;
        if (!double.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        var mult = m.Groups[2].Value.ToLowerInvariant() switch { "gb" => 1L << 30, "mb" => 1L << 20, _ => 1L << 10 };
        return (long)(v * mult);
    }

    // ============================================================
    //  CLEANUP
    // ============================================================

    public async ValueTask DisposeAsync()
    {
        await ResetSessionAsync();
        _fsHttp?.Dispose();
        _fsHttp = null;
        GC.SuppressFinalize(this);
    }

    // ============================================================
    //  FLARESOLVERR DTOs
    // ============================================================

    private static readonly JsonSerializerOptions FsJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private class FsRequest
    {
        [JsonPropertyName("cmd")] public string Cmd { get; set; } = "";
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("session")] public string? Session { get; set; }
        [JsonPropertyName("maxTimeout")] public int? MaxTimeout { get; set; }
        [JsonPropertyName("postData")] public string? PostData { get; set; }
        [JsonPropertyName("proxy")] public FsProxy? Proxy { get; set; }
        [JsonPropertyName("waitInSeconds")] public int? WaitInSeconds { get; set; }
    }

    private class FsProxy
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("password")] public string? Password { get; set; }
    }

    private class FlareSolverrResponse
    {
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("solution")] public FsSolution? Solution { get; set; }
    }

    private class FsSolution
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("status")] public int Status { get; set; }
        [JsonPropertyName("response")] public string? Response { get; set; }
    }
}
