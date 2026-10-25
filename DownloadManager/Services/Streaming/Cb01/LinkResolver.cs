using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Streaming.Cb01;

/// <summary>
/// Risolve i link protettori usati su cb01 fino al link d'ingresso di Maxstream:
///   stayonline.pro/l/{id}/ → uprot.net/msf*/{code}
/// Il link uprot NON viene seguito qui: va aperto dalla sessione Maxstream già loggata,
/// che così atterra sulla pagina premium (/mypremium/) invece che su quella pubblica
/// (/videostreax/) protetta da Cloudflare.
/// </summary>
public class LinkResolver
{
    private readonly ILogger _logger;
    private readonly HttpClient _http;
    private readonly Func<string> _maxstreamBase;
    private static readonly Regex RxLinkId = new(@"var\s+linkId\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    public LinkResolver(ILogger logger, HttpClient http, Func<string> maxstreamBase)
    {
        _logger = logger;
        _http = http;
        _maxstreamBase = maxstreamBase;
    }

    private bool IsMaxstreamHost(string host)
    {
        var h = host.ToLowerInvariant();
        if (h.StartsWith("www.")) h = h[4..];
        return h == Domains.DomainResolver.HostOf(_maxstreamBase()) || h.Contains("maxstream");
    }

    /// <summary>
    /// Link uprot o Maxstream pronto per MaxstreamService; null se il link porta altrove
    /// (es. un "Maxstream" che in realtà è Mixdrop perché il post ha le etichette invertite).
    /// </summary>
    public async Task<string?> ResolveAsync(string protectedUrl, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(protectedUrl, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();

        if (IsMaxstreamHost(host) || host.Contains("uprot")) return protectedUrl;

        if (host.Contains("stayonline"))
            return await ResolveStayonlineAsync(protectedUrl, ct);

        _logger.LogInformation("[LINK-RESOLVER] {Url}: non porta a Maxstream, scartato", protectedUrl);
        return null;
    }

    private async Task<string?> ResolveStayonlineAsync(string url, CancellationToken ct)
    {
        try
        {
            // 1) GET della pagina per prendere l'id e i cookie
            var pageReq = new HttpRequestMessage(HttpMethod.Get, url);
            var pageResp = await _http.SendAsync(pageReq, ct);
            var html = await pageResp.Content.ReadAsStringAsync(ct);

            var idMatch = RxLinkId.Match(html);
            if (!idMatch.Success)
            {
                _logger.LogWarning("[LINK-RESOLVER] linkId non trovato in {Url}", url);
                return null;
            }
            var linkId = idMatch.Groups[1].Value;

            // 2) POST ajax per ottenere il vero link
            var baseUri = new Uri(url);
            var ajaxUrl = $"{baseUri.Scheme}://{baseUri.Host}/ajax/linkView.php";

            var postReq = new HttpRequestMessage(HttpMethod.Post, ajaxUrl)
            {
                Content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("id", linkId),
                    new KeyValuePair<string, string>("ref", ""),
                })
            };
            postReq.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            postReq.Headers.Referrer = new Uri(url);

            var ajaxResp = await _http.SendAsync(postReq, ct);
            var json = await ajaxResp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("value", out var value))
            {
                var nextUrl = value.GetString();
                if (!string.IsNullOrEmpty(nextUrl))
                {
                    _logger.LogInformation("[LINK-RESOLVER] stayonline {Id} → {Next}", linkId, nextUrl);
                    return await ResolveAsync(nextUrl, ct);
                }
            }

            _logger.LogWarning("[LINK-RESOLVER] stayonline: risposta senza link: {Json}", json);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LINK-RESOLVER] Errore stayonline {Url}", url);
            return null;
        }
    }
}
