using DownloadManager.Models;
using DownloadManager.Persistence;

namespace DownloadManager.Services.Proxy;

public class ProxyProvider : IProxyProvider
{
    private readonly ProxyService _service;
    private readonly SettingsService _settings;

    public ProxyProvider(ProxyService service, SettingsService settings)
    {
        _service = service;
        _settings = settings;
    }

    private bool IsProxyEnabledFor(string providerKey)
    {
        var s = _settings.Current;
        return providerKey switch
        {
            ProviderKeys.AnimeWorld => s.UseProxyForAnimeWorld,
            ProviderKeys.AnimeSaturn => s.UseProxyForAnimeSaturn,
            ProviderKeys.StreamingCommunity => s.UseProxyForStreamingCommunity,
            ProviderKeys.Downloads => s.UseProxyForDownloads,
            ProviderKeys.Cb01 => s.UseProxyForCb01,
            _ => true
        };
    }

    public ProxyConfig? GetActive(string providerKey)
    {
        if (!IsProxyEnabledFor(providerKey)) return null;
        return _service.GetActiveFor(providerKey);
    }

    public ProxyConfig? GetNextCandidate(string providerKey)
    {
        if (!IsProxyEnabledFor(providerKey)) return null;
        return _service.GetActiveFor(providerKey);
    }

    public void MarkFailed(ProxyConfig proxy, string providerKey)
    {
        if (proxy == null) return;
        proxy.ConsecutiveFailures++;
        proxy.LastFailedUtc = DateTime.UtcNow;
        _ = _service.SaveAsync(proxy);
    }

    public void MarkSuccess(ProxyConfig proxy, string providerKey)
    {
        if (proxy == null) return;
        if (proxy.ConsecutiveFailures == 0) return;
        proxy.ConsecutiveFailures = 0;
        proxy.LastFailedUtc = null;
        _ = _service.SaveAsync(proxy);
    }
}