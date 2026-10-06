using System.Diagnostics;
using System.Net;
using System.Net.Http;
using DownloadManager.Models;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Domains;

/// <summary>
/// Verifica se un dominio è raggiungibile. Un dominio è "OK" se il server
/// risponde con un codice HTTP qualsiasi (2xx, 3xx, 4xx, 5xx) — è indice
/// di vita del server. Se non arriva alcuna risposta (DNS fail, timeout,
/// connection refused), è "Unreachable".
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
            AllowAutoRedirect = false,   // vogliamo vedere il 3xx, non seguirlo
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

    /// <summary>
    /// Testa un dominio. Non lancia eccezioni: aggiorna la DomainConfig passata.
    /// </summary>
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
                // Timeout dell'HttpClient (8s)
                config.Status = DomainStatus.Unreachable;
                config.StatusMessage = "Timeout (8s)";
                config.LastTested = DateTime.Now;
                _logger.LogWarning("[DOMAIN] {Provider} {Url}: timeout", config.ProviderName, config.Url);
                return;
            }
            catch (HttpRequestException ex)
            {
                // DNS fail / conn refused / no route
                config.Status = DomainStatus.Unreachable;
                config.StatusMessage = ShortenMessage(ex.Message);
                config.LastTested = DateTime.Now;
                _logger.LogWarning("[DOMAIN] {Provider} {Url}: {Msg}",
                    config.ProviderName, config.Url, ex.Message);
                return;
            }

            sw.Stop();

            // Qualunque risposta HTTP = server vivo
            var code = (int)response.StatusCode;
            config.Status = DomainStatus.Ok;
            config.StatusMessage = $"HTTP {code} · {sw.ElapsedMilliseconds}ms";
            config.LastTested = DateTime.Now;

            _logger.LogInformation("[DOMAIN] {Provider} {Url}: OK HTTP {Code} ({Ms}ms)",
                config.ProviderName, config.Url, code, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Cancellazione utente
            config.Status = DomainStatus.Unknown;
            config.StatusMessage = "Annullato";
        }
        finally
        {
            config.IsTesting = false;
        }
    }

    /// <summary>Testa una lista di domini in parallelo (concorrenza configurabile).</summary>
    public async Task TestManyAsync(
        IEnumerable<DomainConfig> configs,
        int concurrency = 4,
        CancellationToken ct = default)
    {
        var gate = new SemaphoreSlim(Math.Max(1, concurrency));

        var tasks = configs.Select(async c =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await TestAsync(c, ct);
            }
            finally
            {
                gate.Release();
            }
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