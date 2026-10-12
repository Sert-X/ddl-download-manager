using System.Net;
using DownloadManager.Models;

namespace DownloadManager.Services.Proxy;

public class DynamicWebProxy : IWebProxy
{
    private readonly IProxyProvider _provider;
    private readonly ICredentials _credentials;

    public DynamicWebProxy(IProxyProvider provider)
    {
        _provider = provider;
        _credentials = new DynamicProxyCredentials(provider);
    }

    public ICredentials? Credentials
    {
        get => _credentials;
        set { }
    }

    public Uri? GetProxy(Uri destination)
    {
        var key = ProviderFromHost(destination.Host);
        var active = _provider.GetActive(key);
        if (active == null) return null;

        var scheme = active.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
        return new Uri($"{scheme}://{active.Host}:{active.Port}");
    }

    public bool IsBypassed(Uri host)
    {
        var key = ProviderFromHost(host.Host);
        return _provider.GetActive(key) == null;
    }

    /// <summary>
    /// Ritorna la chiave del provider in base all'host.
    /// </summary>
    private static string ProviderFromHost(string host)
    {
        var h = host.ToLowerInvariant();
        if (h.Contains("animeworld")) return ProviderKeys.AnimeWorld;
        if (h.Contains("animesaturn")) return ProviderKeys.AnimeSaturn;
        if (h.Contains("streamingcommunity") || h.Contains("vixcloud") ||
            h.Contains("vix-content")) return ProviderKeys.StreamingCommunity;
        return ProviderKeys.Downloads;
    }
}

internal class DynamicProxyCredentials : ICredentials
{
    private readonly IProxyProvider _provider;

    public DynamicProxyCredentials(IProxyProvider provider) { _provider = provider; }

    public NetworkCredential? GetCredential(Uri uri, string authType)
    {
        var key = uri.Host.ToLowerInvariant() switch
        {
            var h when h.Contains("animeworld") => ProviderKeys.AnimeWorld,
            var h when h.Contains("animesaturn") => ProviderKeys.AnimeSaturn,
            var h when h.Contains("streamingcommunity") || h.Contains("vixcloud") => ProviderKeys.StreamingCommunity,
            _ => ProviderKeys.Downloads
        };

        var active = _provider.GetActive(key);
        if (active == null) return null;
        if (active.Auth != ProxyAuthType.UserPassword) return null;
        if (string.IsNullOrEmpty(active.Username)) return null;

        return new NetworkCredential(active.Username, active.Password);
    }
}