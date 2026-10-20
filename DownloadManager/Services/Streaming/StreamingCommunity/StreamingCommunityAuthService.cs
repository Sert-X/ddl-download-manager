using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Domains;
using DownloadManager.Services.Proxy;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Streaming.StreamingCommunity;

public class StreamingCommunityAuthService
{
    private readonly ILogger<StreamingCommunityAuthService> _logger;
    private readonly DomainResolver _domainResolver;
    private readonly AccountRepository _repository;

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36";

    private const string SecChUa =
        "\"Google Chrome\";v=\"143\", \"Chromium\";v=\"143\", \"Not A(Brand\";v=\"24\"";

    public AccountCredentials Current { get; private set; }
    public event EventHandler? StateChanged;
    private readonly IProxyProvider _proxyProvider;

    public StreamingCommunityAuthService(
        ILogger<StreamingCommunityAuthService> logger,
        DomainResolver domainResolver,
        AccountRepository repository,
        IProxyProvider proxyProvider)
    {
        _logger = logger;
        _domainResolver = domainResolver;
        _repository = repository;
        _proxyProvider = proxyProvider;
        Current = repository.Load();
    }

    private string BaseUrl
    {
        get
        {
            try { return _domainResolver.GetBaseUrl("StreamingCommunity").TrimEnd('/'); }
            catch { return "https://streamingcommunityz.rip"; }
        }
    }

    // ============================================================
    //  LOGIN
    // ============================================================

    public class AuthenticatedWebProxy : IWebProxy
    {
        private readonly Uri _proxyUri;
        private readonly NetworkCredential? _credentials;

        public AuthenticatedWebProxy(string proxyAddress, string? username, string? password)
        {
            _proxyUri = new Uri(proxyAddress);
            if (!string.IsNullOrEmpty(username))
            {
                _credentials = new NetworkCredential(username, password);
            }
        }

        public ICredentials? Credentials
        {
            get => _credentials;
            set { /* Ignorato, usiamo _credentials interno */ }
        }

        public Uri? GetProxy(Uri destination) => _proxyUri;

        public bool IsBypassed(Uri host) => false;

        // Forza l'invio delle credenziali per evitare la sfida 407
        public bool IsBypassed(Uri host, out bool isBypassed)
        {
            isBypassed = false;
            return false;
        }
    }

    public async Task<(bool Success, string Message)> LoginAsync(
        string username, string password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return (false, "Inserisci username e password.");

        try
        {
            var cookies = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                CookieContainer = cookies,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.GZip
                                    | DecompressionMethods.Deflate,
            };

            // === Applica il proxy attivo per StreamingCommunity, se configurato ===
            ProxyConfig? proxy = null;
            try { proxy = _proxyProvider.GetActive(ProviderKeys.StreamingCommunity); } catch { }

            if (proxy != null)
            {
                var scheme = proxy.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
                // Usa un IWebProxy personalizzato invece di WebProxy
                handler.Proxy = new AuthenticatedWebProxy($"{scheme}://{proxy.Host}:{proxy.Port}", proxy.Username, proxy.Password);
                handler.UseProxy = true;

                _logger.LogInformation("[SC-AUTH] Login con proxy: {Host}:{Port}",
                    proxy.Host, proxy.Port);
            }
            else
            {
                handler.UseProxy = false;
                _logger.LogInformation("[SC-AUTH] Login senza proxy (connessione diretta)");
            }

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

            // ============================================================
            // STEP 1: GET /login  (full-page, no X-Inertia)
            // ============================================================
            var loginUrl = $"{BaseUrl}/login";
            _logger.LogInformation("[SC-AUTH] STEP1 GET {Url}", loginUrl);

            var loginHtml = await SendGetAsync(http, cookies, loginUrl,
                referer: $"{BaseUrl}/en", includeInertia: false,
                inertiaVersion: null, ct);

            var inertiaVersion = ExtractInertiaVersionFromHtml(loginHtml);
            _logger.LogInformation("[SC-AUTH] inertia version: {V}",
                string.IsNullOrEmpty(inertiaVersion) ? "(vuoto)" : inertiaVersion);

            var xsrfRaw = GetCookieValue(cookies, BaseUrl, "XSRF-TOKEN");
            if (string.IsNullOrEmpty(xsrfRaw))
                return (false, "XSRF-TOKEN non ricevuto dal server.");

            var xsrf = Uri.UnescapeDataString(xsrfRaw);

            // ============================================================
            // STEP 2: POST /login  (con X-Inertia)
            // ============================================================
            var loginJson = "{\"username\":\"" + JsonEscape(username) +
                            "\",\"password\":\"" + JsonEscape(password) +
                            "\",\"remember\":false,\"recaptcha_token\":\"\"}";

            _logger.LogInformation("[SC-AUTH] STEP2 POST /login come {User}", username);

            using var postReq = new HttpRequestMessage(HttpMethod.Post, loginUrl);
            postReq.Content = new StringContent(loginJson, Encoding.UTF8);
            postReq.Content.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");

            SetupBrowserHeaders(postReq, referer: $"{BaseUrl}/en",
                includeInertia: true, inertiaVersion: inertiaVersion, xsrf: xsrf);

            using var postResp = await http.SendAsync(
                postReq, HttpCompletionOption.ResponseContentRead, ct);
            var postBody = await postResp.Content.ReadAsStringAsync(ct);

            _logger.LogInformation("[SC-AUTH] STEP2 POST /login: {Status}, location={Loc}",
                (int)postResp.StatusCode,
                postResp.Headers.Location?.ToString() ?? "(nessuno)");

            if (postBody.Contains("These credentials do not match our records.") ||
                postBody.Contains("Credenziali non valide") ||
                postBody.Contains("\"username\":\"The username field is required.\""))
                return (false, "Credenziali non valide.");

            if (postBody.Contains("Too many login attempts") ||
                postBody.Contains("Troppi tentativi di accesso"))
                return (false, "Troppi tentativi di login. Riprova più tardi.");

            var location = postResp.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location))
            {
                if (ContainsAuthUser(postBody))
                {
                    _logger.LogInformation("[SC-AUTH] Login OK (JSON diretto)");
                    return await FinalizeSuccessAsync(
                        cookies, postBody, username, password);
                }
                return (false, $"Risposta inattesa dal server ({(int)postResp.StatusCode}).");
            }

            if (location.StartsWith("/")) location = BaseUrl + location;

            // ============================================================
            // STEP 3: GET Location  (con X-Inertia)  — come nel LoliCode
            // ============================================================
            _logger.LogInformation("[SC-AUTH] STEP3 GET {Url} (X-Inertia)", location);

            var step3Body = await SendGetAsync(http, cookies, location,
                referer: $"{BaseUrl}/en", includeInertia: true,
                inertiaVersion: inertiaVersion, ct);

            if (!ContainsAuthUser(step3Body))
            {
                var snippet = step3Body.Length > 400
                    ? step3Body.Substring(0, 400) : step3Body;
                _logger.LogWarning("[SC-AUTH] STEP3 senza auth.user. Body: {S}", snippet);
            }
            else
            {
                _logger.LogInformation("[SC-AUTH] STEP3 auth.user OK");
            }

            // ============================================================
            // STEP 4: GET /en/account (con X-Inertia) — verifica finale
            // ============================================================
            var accountUrl = $"{BaseUrl}/en/account";
            _logger.LogInformation("[SC-AUTH] STEP4 GET {Url} (X-Inertia)", accountUrl);

            var accountBody = await SendGetAsync(http, cookies, accountUrl,
                referer: $"{BaseUrl}/en", includeInertia: true,
                inertiaVersion: inertiaVersion, ct);

            if (!ContainsAuthUser(accountBody))
            {
                var snippet = accountBody.Length > 800
                    ? accountBody.Substring(0, 800) : accountBody;
                _logger.LogWarning("[SC-AUTH] STEP4 senza auth.user. Body: {S}", snippet);
                return (false, "Login fallito: sessione non autenticata.");
            }

            _logger.LogInformation("[SC-AUTH] STEP4 auth.user OK");
            return await FinalizeSuccessAsync(cookies, accountBody, username, password);
        }
        catch (TaskCanceledException)
        {
            return (false, "Timeout durante il login.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SC-AUTH] Errore login");
            return (false, $"Errore: {ex.Message}");
        }
    }

    private (bool Success, string Message) FinalizeSuccess(
        CookieContainer cookies, string html,
        string username, string password)
    {
        bool isPremium = false;
        string planName = "", updatedAt = "";
        int daysLeft = 0;

        try
        {
            var dataPageJson = ExtractDataPageJsonDecoded(html);
            if (!string.IsNullOrEmpty(dataPageJson))
            {
                using var doc = JsonDocument.Parse(dataPageJson);
                if (doc.RootElement.TryGetProperty("props", out var props))
                {
                    if (props.TryGetProperty("premiumUser", out var pu) &&
                        pu.ValueKind == JsonValueKind.Object)
                    {
                        isPremium = true;

                        if (pu.TryGetProperty("plan", out var plan) &&
                            plan.ValueKind == JsonValueKind.Object &&
                            plan.TryGetProperty("name", out var pn))
                            planName = pn.GetString() ?? "";

                        if (pu.TryGetProperty("updated_at", out var ua))
                            updatedAt = ua.GetString() ?? "";

                        if (pu.TryGetProperty("days_left", out var dl) &&
                            dl.ValueKind == JsonValueKind.Number)
                            daysLeft = dl.GetInt32();
                    }

                    if (props.TryGetProperty("auth", out var auth) &&
                        auth.TryGetProperty("user", out var usr) &&
                        usr.ValueKind == JsonValueKind.Object &&
                        usr.TryGetProperty("name", out var nm))
                    {
                        var name = nm.GetString();
                        if (!string.IsNullOrEmpty(name)) username = name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SC-AUTH] Errore parsing premiumUser");
        }

        var sessionCookies = SerializeCookies(cookies, BaseUrl);

        Current.Username = username;
        Current.Password = password;
        Current.IsPremium = isPremium;
        Current.PlanName = planName;
        Current.PlanUpdatedAt = updatedAt;
        Current.DaysLeft = daysLeft;
        Current.SessionCookies = sessionCookies;
        Current.LastLoginUtc = DateTime.UtcNow;

        _repository.Save(Current);
        StateChanged?.Invoke(this, EventArgs.Empty);

        var msg = isPremium
            ? $"Login OK. Piano: {planName} ({daysLeft}gg rimasti)"
            : "Login OK. Account gratuito (720p)";

        _logger.LogInformation("[SC-AUTH] {Msg}", msg);
        return (true, msg);
    }

    private Task<(bool Success, string Message)> FinalizeSuccessAsync(
        CookieContainer cookies, string html,
        string username, string password)
    {
        return Task.FromResult(FinalizeSuccess(cookies, html, username, password));
    }

    public void Logout()
    {
        // Mantiene username e password (utili per riaccedere rapidamente).
        // Azzera SOLO lo stato di sessione.
        Current.SessionCookies = string.Empty;
        Current.LastLoginUtc = null;
        Current.IsPremium = false;
        Current.PlanName = string.Empty;
        Current.PlanUpdatedAt = string.Empty;
        Current.DaysLeft = 0;

        // Salva di nuovo: credenziali restano nel DPAPI, sessione azzerata
        _repository.Save(Current);

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearAll()
    {
        Current = new AccountCredentials();
        _repository.Clear();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
    public void Save()
    {
        _repository.Save(Current);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ============================================================
    //  HTTP
    // ============================================================

    private async Task<string> SendGetAsync(
        HttpClient http, CookieContainer cookies, string url,
        string referer, bool includeInertia,
        string? inertiaVersion, CancellationToken ct)
    {
        var xsrfRaw = GetCookieValue(cookies, BaseUrl, "XSRF-TOKEN");
        var xsrf = string.IsNullOrEmpty(xsrfRaw) ? null : Uri.UnescapeDataString(xsrfRaw);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        SetupBrowserHeaders(req, referer: referer,
            includeInertia: includeInertia,
            inertiaVersion: inertiaVersion, xsrf: xsrf);

        using var resp = await http.SendAsync(
            req, HttpCompletionOption.ResponseContentRead, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);

        _logger.LogInformation("[SC-AUTH] GET {Url} (inertia={I}) → {Status} (len={N})",
            url, includeInertia, (int)resp.StatusCode, body.Length);

        return body;
    }

    private static void SetupBrowserHeaders(
        HttpRequestMessage req, string referer,
        bool includeInertia,
        string? inertiaVersion, string? xsrf)
    {
        req.Version = HttpVersion.Version11;
        req.VersionPolicy = HttpVersionPolicy.RequestVersionExact;

        req.Headers.TryAddWithoutValidation("accept",
            includeInertia
                ? "text/html, application/xhtml+xml"
                : "text/html, application/xhtml+xml");
        req.Headers.TryAddWithoutValidation("accept-encoding", "gzip, deflate");
        req.Headers.TryAddWithoutValidation("accept-language", "en-US,en;q=0.8");
        req.Headers.TryAddWithoutValidation("cache-control", "no-cache");
        req.Headers.TryAddWithoutValidation("pragma", "no-cache");
        req.Headers.TryAddWithoutValidation("priority", "u=1, i");
        req.Headers.TryAddWithoutValidation("referer", referer);

        req.Headers.TryAddWithoutValidation("sec-ch-ua", SecChUa);
        req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        req.Headers.TryAddWithoutValidation("sec-fetch-dest", "empty");
        req.Headers.TryAddWithoutValidation("sec-fetch-mode", "cors");
        req.Headers.TryAddWithoutValidation("sec-fetch-site", "same-origin");
        req.Headers.TryAddWithoutValidation("user-agent", UserAgent);

        if (includeInertia)
        {
            req.Headers.TryAddWithoutValidation("x-inertia", "true");
            if (!string.IsNullOrEmpty(inertiaVersion))
                req.Headers.TryAddWithoutValidation("x-inertia-version", inertiaVersion);
            req.Headers.TryAddWithoutValidation("x-requested-with", "XMLHttpRequest");
        }

        if (!string.IsNullOrEmpty(xsrf))
            req.Headers.TryAddWithoutValidation("x-xsrf-token", xsrf);
    }

    // ============================================================
    //  PARSING
    // ============================================================

    private static string ExtractInertiaVersionFromHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var trimmed = html.TrimStart();
        if (trimmed.StartsWith("{"))
        {
            var m0 = Regex.Match(trimmed, "\"version\":\"([^\"]*)\"");
            return m0.Success ? m0.Groups[1].Value : "";
        }

        var mPage = Regex.Match(html,
            @"\bdata-page\s*=\s*[""']([^""']+)[""']",
            RegexOptions.Singleline);

        if (mPage.Success)
        {
            var decoded = WebUtility.HtmlDecode(mPage.Groups[1].Value);

            var mVersion = Regex.Match(decoded,
                "\"url\":\"[^\"]*login\",\"version\":\"([^\"]*)\"");
            if (mVersion.Success) return mVersion.Groups[1].Value;

            var mAny = Regex.Match(decoded, "\"version\":\"([^\"]*)\"");
            if (mAny.Success) return mAny.Groups[1].Value;
        }

        var mEntity = Regex.Match(html,
            @"&quot;version&quot;:&quot;([^&]*)&quot;");
        if (mEntity.Success) return mEntity.Groups[1].Value;

        return "";
    }

    private static string? ExtractDataPageJsonDecoded(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;

        var trimmed = html.TrimStart();
        if (trimmed.StartsWith("{")) return trimmed;

        var m = Regex.Match(html,
            @"\bdata-page\s*=\s*[""']([^""']+)[""']",
            RegexOptions.Singleline);
        if (m.Success)
            return WebUtility.HtmlDecode(m.Groups[1].Value);

        return null;
    }

    private static bool ContainsAuthUser(string? body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        return body.Contains("\"auth\":{\"user\":{\"id\"")
            || body.Contains("\"auth\":{\"user\":{\"name\"");
    }

    private static string JsonEscape(string s)
    {
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
    }

    private static string GetCookieValue(CookieContainer container, string baseUrl, string name)
    {
        try
        {
            var uri = new Uri(baseUrl);
            var cookies = container.GetCookies(uri);
            foreach (Cookie c in cookies)
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                    return c.Value;
        }
        catch { }
        return "";
    }

    private static string SerializeCookies(CookieContainer container, string baseUrl)
    {
        var sb = new StringBuilder();
        try
        {
            var uri = new Uri(baseUrl);
            var cookies = container.GetCookies(uri);
            foreach (Cookie c in cookies)
            {
                if (sb.Length > 0) sb.Append("; ");
                sb.Append(c.Name).Append('=').Append(c.Value);
            }
        }
        catch { }
        return sb.ToString();
    }

    public static List<(string Name, string Value)> ParseCookieString(string raw)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var pair in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = pair.Trim();
            var idx = trimmed.IndexOf('=');
            if (idx <= 0) continue;
            var name = trimmed.Substring(0, idx).Trim();
            var value = trimmed.Substring(idx + 1).Trim();
            if (!string.IsNullOrEmpty(name))
                result.Add((name, value));
        }
        return result;
    }
}