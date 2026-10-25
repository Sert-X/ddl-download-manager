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

    public event EventHandler? ActiveProxyChanged;

    public async Task LoadAllAsync()
    {
        var list = await _repository.GetAllAsync();
        All.Clear();
        All.AddRange(list);
    }

    public async Task SaveAsync(ProxyConfig p)
    {
        // Un proxy nuovo (creato o importato) deve essere visibile anche ai
        // provider, che leggono da All: senza questo non veniva mai usato.
        if (!All.Contains(p)) All.Add(p);

        if (p.Id == 0) await _repository.InsertAsync(p);
        else await _repository.UpdateAsync(p);
    }

    /// <summary>
    /// Salva più proxy in un'unica transazione, fuori dal thread UI.
    /// </summary>
    public async Task SaveManyAsync(IEnumerable<ProxyConfig> proxies)
    {
        var list = proxies.ToList();
        if (list.Count == 0) return;

        foreach (var p in list)
            if (!All.Contains(p)) All.Add(p);

        await Task.Run(() => _repository.SaveManyAsync(list));
    }

    public async Task DeleteAsync(ProxyConfig p)
    {
        if (p.Id > 0) await _repository.DeleteAsync(p.Id);
        All.Remove(p);
    }

    /// <summary>Elimina più proxy con una sola query, fuori dal thread UI.</summary>
    public async Task DeleteManyAsync(IEnumerable<ProxyConfig> proxies)
    {
        var list = proxies.ToList();
        var ids = list.Where(p => p.Id > 0).Select(p => p.Id).ToList();
        if (ids.Count > 0) await Task.Run(() => _repository.DeleteManyAsync(ids));

        var set = list.ToHashSet();
        All.RemoveAll(set.Contains);
    }

    // ============================================================
    //  GET ATTIVO PER PROVIDER
    // ============================================================

    /// <summary>
    /// Ritorna il proxy attivo per il provider specificato.
    /// Il concetto "attivo" è: <c>IsEnabledForX = true</c> e nessun altro proxy
    /// abilitato per lo stesso provider.
    /// </summary>
    public ProxyConfig? GetActiveFor(string providerKey)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var candidates = All.Where(p => IsEnabledFor(p, providerKey)).ToList();
        if (candidates.Count == 0) return null;

        // Preferisci un proxy sano
        var healthy = candidates
            .Where(p => p.ConsecutiveFailures < 3)
            .Where(p => p.LastFailedUtc == null || p.LastFailedUtc < cutoff)
            .OrderBy(p => p.ConsecutiveFailures)
            .FirstOrDefault();

        return healthy ?? candidates.First();
    }

    // ============================================================
    //  ROTAZIONE PER PROVIDER
    // ============================================================

    public async Task<ProxyConfig?> RotateNextForAsync(string providerKey)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var current = GetActiveFor(providerKey);

        bool IsHealthy(ProxyConfig p) =>
            p.HasTestOkFor(providerKey) &&
            p.ConsecutiveFailures == 0 &&
            (p.LastFailedUtc == null || p.LastFailedUtc < cutoff);

        bool IsUntested(ProxyConfig p) => !p.ProviderResults.ContainsKey(providerKey);

        bool IsRecentlyFailed(ProxyConfig p) =>
            (p.ProviderResults.TryGetValue(providerKey, out var r) && r.StartsWith("❌")) ||
            (p.LastFailedUtc != null && p.LastFailedUtc >= cutoff);

        // Ordina a partire dal corrente + 1, con wrap-around
        int currentIdx = current != null ? All.IndexOf(current) : -1;
        int n = All.Count;
        if (n == 0) return null;

        var ordered = new List<ProxyConfig>();
        for (int step = 1; step <= n; step++)
        {
            int idx = (currentIdx + step) % n;
            var p = All[idx];
            if (p == current) continue;
            ordered.Add(p);
        }

        var next = ordered.FirstOrDefault(IsHealthy)
                ?? ordered.FirstOrDefault(IsUntested)
                ?? ordered.FirstOrDefault(p => !IsRecentlyFailed(p) && p.ConsecutiveFailures < 3);

        if (next == null)
        {
            _logger.LogWarning("[PROXY] Nessun candidato per {P}", providerKey);
            return null;
        }

        // Disattiva il provider su tutti, attiva solo su next (salvando solo quelli cambiati)
        var changed = All.Where(p => p != next && IsEnabledFor(p, providerKey)).ToList();
        foreach (var p in changed) SetEnabledFor(p, providerKey, false);
        SetEnabledFor(next, providerKey, true);

        // Reset manual-override perché è una rotazione automatica
        ClearManualDisabled(next, providerKey);
        changed.Add(next);

        try { await SaveManyAsync(changed); }
        catch (Exception ex) { _logger.LogWarning(ex, "[PROXY] Salvataggio rotazione fallito"); }

        _logger.LogWarning("[PROXY] Rotazione {P} → {Name}", providerKey, next.Name);
        ActiveProxyChanged?.Invoke(this, EventArgs.Empty);
        return next;
    }

    public async Task MarkFailedAndRotateAsync(ProxyConfig proxy, string providerKey)
    {
        if (proxy == null) return;

        proxy.ConsecutiveFailures++;
        proxy.LastFailedUtc = DateTime.UtcNow;
        try { await _repository.UpdateAsync(proxy); }
        catch (Exception ex) { _logger.LogWarning(ex, "[PROXY] Salvataggio di {Name} fallito", proxy.Name); }

        _logger.LogWarning("[PROXY] {Name} failed su {P} ({N})",
            proxy.Name, providerKey, proxy.ConsecutiveFailures);

        if (IsEnabledFor(proxy, providerKey))
        {
            await RotateNextForAsync(providerKey);
        }
    }

    // ============================================================
    //  HELPER PROVIDER-ENABLEMENT
    // ============================================================

    public static bool IsEnabledFor(ProxyConfig p, string providerKey)
    {
        return providerKey switch
        {
            ProviderKeys.AnimeWorld => p.IsEnabledForAnimeWorld,
            ProviderKeys.AnimeSaturn => p.IsEnabledForAnimeSaturn,
            ProviderKeys.StreamingCommunity => p.IsEnabledForStreamingCommunity,
            ProviderKeys.Downloads => p.IsEnabledForDownloads,
            ProviderKeys.Cb01 => p.IsEnabledForCb01,
            _ => false
        };
    }

    public static void SetEnabledFor(ProxyConfig p, string providerKey, bool value)
    {
        switch (providerKey)
        {
            case ProviderKeys.AnimeWorld: p.IsEnabledForAnimeWorld = value; break;
            case ProviderKeys.AnimeSaturn: p.IsEnabledForAnimeSaturn = value; break;
            case ProviderKeys.StreamingCommunity: p.IsEnabledForStreamingCommunity = value; break;
            case ProviderKeys.Downloads: p.IsEnabledForDownloads = value; break;
            case ProviderKeys.Cb01: p.IsEnabledForCb01 = value; break;
        }
    }

    public static bool IsManuallyDisabledFor(ProxyConfig p, string providerKey)
    {
        return providerKey switch
        {
            ProviderKeys.AnimeWorld => p.ManuallyDisabledForAnimeWorld,
            ProviderKeys.AnimeSaturn => p.ManuallyDisabledForAnimeSaturn,
            ProviderKeys.StreamingCommunity => p.ManuallyDisabledForStreamingCommunity,
            ProviderKeys.Downloads => p.ManuallyDisabledForDownloads,
            ProviderKeys.Cb01 => p.ManuallyDisabledForCb01,
            _ => false
        };
    }

    public static void SetManuallyDisabledFor(ProxyConfig p, string providerKey, bool value)
    {
        switch (providerKey)
        {
            case ProviderKeys.AnimeWorld: p.ManuallyDisabledForAnimeWorld = value; break;
            case ProviderKeys.AnimeSaturn: p.ManuallyDisabledForAnimeSaturn = value; break;
            case ProviderKeys.StreamingCommunity: p.ManuallyDisabledForStreamingCommunity = value; break;
            case ProviderKeys.Downloads: p.ManuallyDisabledForDownloads = value; break;
            case ProviderKeys.Cb01: p.ManuallyDisabledForCb01 = value; break;
        }
    }

    public static void ClearManualDisabled(ProxyConfig p, string providerKey)
        => SetManuallyDisabledFor(p, providerKey, false);

    /// <summary>
    /// Applica la selezione automatica post-test: se il test è ✅ e l'utente
    /// non ha disattivato manualmente il provider, attiva il flag.
    /// </summary>
    public async Task ApplyAutoSelectAfterTestAsync(ProxyConfig p, string providerKey, bool testOk)
    {
        if (!testOk) return;
        if (IsManuallyDisabledFor(p, providerKey)) return;
        if (IsEnabledFor(p, providerKey)) return;

        SetEnabledFor(p, providerKey, true);
        try { await _repository.UpdateAsync(p); }
        catch (Exception ex) { _logger.LogWarning(ex, "[PROXY] Salvataggio di {Name} fallito", p.Name); }
        ActiveProxyChanged?.Invoke(this, EventArgs.Empty);
    }

    // ============================================================
    //  TEST
    // ============================================================

    public async Task<(bool ok, string message, string? exitIp)> TestAsync(
    ProxyConfig p, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.Host))
            return (false, "Host vuoto", null);

        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = BuildWebProxy(p),
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

            // Preemptive auth: manda le credenziali nel primo header
            AddPreemptiveProxyAuth(http, p);

            var resp = await http.GetAsync("https://httpbin.org/ip", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if ((int)resp.StatusCode == 407)
                return (false, "407 — credenziali proxy rifiutate", null);

            if (!resp.IsSuccessStatusCode)
                return (false, $"HTTP {(int)resp.StatusCode}", null);

            string? ip = null;
            var m = System.Text.RegularExpressions.Regex.Match(body, "\"origin\"\\s*:\\s*\"([^\"]+)\"");
            if (m.Success) ip = m.Groups[1].Value;

            return (true, $"OK — uscita: {ip ?? "?"}", ip);
        }
        catch (TaskCanceledException) { return (false, "Timeout (15s)", null); }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    public async Task<(bool ok, string message)> TestProviderAsync(
        ProxyConfig p, string providerName, string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.Host))
            return (false, "Host vuoto");

        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = BuildWebProxy(p),
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

            // Preemptive auth
            AddPreemptiveProxyAuth(http, p);

            var resp = await http.GetAsync(url, ct);
            var status = (int)resp.StatusCode;

            if (status == 407)
                return (false, $"{providerName}: 407 — credenziali proxy rifiutate");
            if (status >= 400)
                return (false, $"{providerName}: HTTP {status}");

            return (true, $"{providerName}: OK ({status})");
        }
        catch (TaskCanceledException) { return (false, $"{providerName}: timeout"); }
        catch (Exception ex) { return (false, $"{providerName}: {ex.Message}"); }
    }

    /// <summary>
    /// Aggiunge manualmente l'header Proxy-Authorization (Basic) nel primo
    /// request. Alcuni proxy non fanno il challenge-response e vogliono le
    /// credenziali subito.
    /// </summary>
    private static void AddPreemptiveProxyAuth(HttpClient http, ProxyConfig p)
    {
        if (p.Auth != ProxyAuthType.UserPassword) return;
        if (string.IsNullOrEmpty(p.Username)) return;

        var raw = $"{p.Username}:{p.Password}";
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
        http.DefaultRequestHeaders.TryAddWithoutValidation("Proxy-Authorization", $"Basic {b64}");
    }

    public static IWebProxy BuildWebProxy(ProxyConfig p)
    {
        var scheme = p.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
        var proxy = new WebProxy(new Uri($"{scheme}://{p.Host}:{p.Port}"));
        if (p.Auth == ProxyAuthType.UserPassword)
            proxy.Credentials = new NetworkCredential(p.Username, p.Password);
        return proxy;
    }
}