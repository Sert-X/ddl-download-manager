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

    // HttpClientHandler legge questa property per autenticarsi al proxy.
    // Siccome le credenziali possono cambiare (l'utente può attivare un altro
    // proxy), ritorniamo un ICredentials dinamico che le legge al volo.
    public ICredentials? Credentials
    {
        get => _credentials;
        set { /* ignorato: le credenziali vengono sempre dal provider */ }
    }

    public Uri? GetProxy(Uri destination)
    {
        var active = _provider.GetActive();
        if (active == null) return null;

        var scheme = active.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
        return new Uri($"{scheme}://{active.Host}:{active.Port}");
    }

    public bool IsBypassed(Uri host) => _provider.GetActive() == null;
}

/// <summary>
/// Fornisce le credenziali del proxy attivo al momento della richiesta.
/// Usato da HttpClientHandler per rispondere alle challenge 407.
/// </summary>
internal class DynamicProxyCredentials : ICredentials
{
    private readonly IProxyProvider _provider;

    public DynamicProxyCredentials(IProxyProvider provider)
    {
        _provider = provider;
    }

    public NetworkCredential? GetCredential(Uri uri, string authType)
    {
        var active = _provider.GetActive();
        if (active == null) return null;
        if (active.Auth != ProxyAuthType.UserPassword) return null;
        if (string.IsNullOrEmpty(active.Username)) return null;

        return new NetworkCredential(active.Username, active.Password);
    }
}