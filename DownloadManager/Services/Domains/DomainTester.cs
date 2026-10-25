using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using DownloadManager.Models;
using DownloadManager.Services.Proxy;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Domains;

/// <summary>
/// Verifica se un dominio è raggiungibile. Un dominio è "OK" SOLO se il server
/// risponde 200 OK. Usa il proxy attivo per il provider del dominio testato.
/// </summary>
public class DomainTester
{
    private readonly ILogger<DomainTester> _logger;
    private readonly IProxyProvider _proxyProvider;

    // Cache un HttpClient per provider (così riusiamo le connessioni)
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    public DomainTester(ILogger<DomainTester> logger, IProxyProvider proxyProvider)
    {
        _logger = logger;
        _proxyProvider = proxyProvider;
    }

    private HttpClient GetClientForProvider(string providerKey)
    {
        return _clients.GetOrAdd(providerKey, key =>
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            };

            // Prova a ottenere il proxy attivo per questo provider
            ProxyConfig? proxy = null;
            try { proxy = _proxyProvider.GetActive(key); } catch { }

            if (proxy != null)
            {
                var scheme = proxy.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
                var webProxy = new WebProxy($"{scheme}://{proxy.Host}:{proxy.Port}");

                if (proxy.Auth == ProxyAuthType.UserPassword &&
                    !string.IsNullOrEmpty(proxy.Username))
                {
                    webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
                }

                handler.Proxy = webProxy;
                handler.UseProxy = true;

                _logger.LogInformation("[DOMAIN] Proxy per {Provider}: {Host}:{Port}",
                    key, proxy.Host, proxy.Port);
            }
            else
            {
                handler.UseProxy = false;
                _logger.LogInformation("[DOMAIN] Nessun proxy per {Provider}, connessione diretta", key);
            }

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

            return client;
        });
    }

    /// <summary>
    /// Invalida la cache dei client quando cambia il proxy attivo.
    /// Chiamato quando l'utente modifica i proxy dal tab.
    /// </summary>
    public void InvalidateCache()
    {
        foreach (var c in _clients.Values)
        {
            try { c.Dispose(); } catch { }
        }
        _clients.Clear();
        _logger.LogInformation("[DOMAIN] Cache client HTTP invalidata");
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
            // Il providerKey del proxy coincide con ProviderName, tranne CB01/Maxstream che condividono "Cb01"
            var providerKey = config.ProviderName is DomainResolver.Cb01 or DomainResolver.Maxstream
                ? ProviderKeys.Cb01
                : config.ProviderName ?? "";
            var http = GetClientForProvider(providerKey);

            var sw = Stopwatch.StartNew();

            HttpResponseMessage response;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, config.Url);
                response = await http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                config.Status = DomainStatus.Unreachable;
                config.StatusMessage = "Timeout (12s)";
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

            // SOLO 200 OK è verde
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