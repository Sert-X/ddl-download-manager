using System.Net;
using System.Net.Http;
using DownloadManager.Models;
using DownloadManager.Persistence;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Proxy;

public class ProxyService
{
    private readonly ProxyConfigRepository _repository;
    private readonly ILogger<ProxyService> _logger;

    public ProxyService(ProxyConfigRepository repository, ILogger<ProxyService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public List<ProxyConfig> All { get; } = new();

    public ProxyConfig? Active => All.FirstOrDefault(p => p.IsEnabled);

    public async Task LoadAllAsync()
    {
        var list = await _repository.GetAllAsync();
        All.Clear();
        All.AddRange(list);
    }

    public async Task SaveAsync(ProxyConfig p)
    {
        if (p.Id == 0) await _repository.InsertAsync(p);
        else await _repository.UpdateAsync(p);
    }

    public async Task DeleteAsync(ProxyConfig p)
    {
        if (p.Id > 0) await _repository.DeleteAsync(p.Id);
        All.Remove(p);
    }

    /// <summary>
    /// Test: prova a fare una richiesta HTTP attraverso il proxy e verifica che
    /// risponda. Usa httpbin.org/ip perché ritorna l'IP di uscita del proxy.
    /// </summary>
    public async Task<(bool ok, string message, string? exitIp)> TestAsync(ProxyConfig p, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.Host))
            return (false, "Host vuoto", null);

        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = BuildWebProxy(p),
                UseProxy = true
            };

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

            var resp = await http.GetAsync("https://httpbin.org/ip", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}", null);

            // Estrai l'IP dal JSON {"origin":"1.2.3.4"}
            string? ip = null;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"origin\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) ip = m.Groups[1].Value;

            return (true, $"OK — uscita: {ip ?? "?"}", ip);
        }
        catch (TaskCanceledException)
        {
            return (false, "Timeout (15s)", null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, null);
        }
    }

    public static IWebProxy BuildWebProxy(ProxyConfig p)
    {
        var scheme = p.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
        var proxy = new WebProxy(new Uri($"{scheme}://{p.Host}:{p.Port}"));

        if (p.Auth == ProxyAuthType.UserPassword)
        {
            proxy.Credentials = new NetworkCredential(p.Username, p.Password);
        }

        return proxy;
    }
}