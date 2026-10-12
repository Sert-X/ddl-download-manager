using System.Diagnostics;
using System.Net;
using System.Net.Http;
using DownloadManager.Models;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Domains;

/// <summary>
/// Verifica se un dominio è raggiungibile. Un dominio è "OK" SOLO se il server
/// risponde 200 OK. Qualunque altro codice (3xx, 4xx, 5xx) è "Unreachable".
/// </summary>
public class DomainTester
{
    private readonly ILogger<DomainTester> _logger;
    private readonly HttpClient _http;

    public DomainTester(ILogger<DomainTester> logger)
    {
        _logger = logger;

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    public async Task TestAsync(DomainConfig config, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
        {
            config.Status = DomainStatus.Unreachable;
            config.StatusMessage = "URL vuoto";
            config.LastTested = DateTime.Now;
            return;
        }

        config.IsTesting = true;
        try
        {
            var sw = Stopwatch.StartNew();

            HttpResponseMessage response;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, config.Url);
                response = await _http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                config.Status = DomainStatus.Unreachable;
                config.StatusMessage = "Timeout (8s)";
                config.LastTested = DateTime.Now;
                _logger.LogWarning("[DOMAIN] {Provider} {Url}: timeout",
                    config.ProviderName, config.Url);
                return;
            }
            catch (HttpRequestException ex)
            {
                config.Status = DomainStatus.Unreachable;
                config.StatusMessage = ShortenMessage(ex.Message);
                config.LastTested = DateTime.Now;
                _logger.LogWarning("[DOMAIN] {Provider} {Url}: {Msg}",
                    config.ProviderName, config.Url, ex.Message);
                return;
            }

            sw.Stop();
            var code = (int)response.StatusCode;

            // === SOLO 200 OK è verde ===
            if (response.StatusCode == HttpStatusCode.OK)
            {
                config.Status = DomainStatus.Ok;
                config.StatusMessage = $"HTTP 200 · {sw.ElapsedMilliseconds}ms";
                config.LastTested = DateTime.Now;

                _logger.LogInformation("[DOMAIN] {Provider} {Url}: OK 200 ({Ms}ms)",
                    config.ProviderName, config.Url, sw.ElapsedMilliseconds);
            }
            else
            {
                config.Status = DomainStatus.Unreachable;
                config.StatusMessage = $"HTTP {code} (atteso 200)";
                config.LastTested = DateTime.Now;

                _logger.LogWarning("[DOMAIN] {Provider} {Url}: HTTP {Code} (non 200)",
                    config.ProviderName, config.Url, code);
            }
        }
        catch (OperationCanceledException)
        {
            config.Status = DomainStatus.Unknown;
            config.StatusMessage = "Annullato";
        }
        finally
        {
            config.IsTesting = false;
        }
    }

    public async Task TestManyAsync(
        IEnumerable<DomainConfig> configs,
        int concurrency = 4,
        CancellationToken ct = default)
    {
        var gate = new SemaphoreSlim(Math.Max(1, concurrency));

        var tasks = configs.Select(async c =>
        {
            await gate.WaitAsync(ct);
            try { await TestAsync(c, ct); }
            finally { gate.Release(); }
        }).ToList();

        await Task.WhenAll(tasks);
    }

    private static string ShortenMessage(string msg)
    {
        if (string.IsNullOrEmpty(msg)) return "Errore sconosciuto";
        if (msg.Length <= 60) return msg;
        return msg.Substring(0, 57) + "...";
    }
}