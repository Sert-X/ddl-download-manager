using DownloadManager.Models;

namespace DownloadManager.Services.Proxy;

public interface IProxyProvider
{
    /// <summary>Ritorna il proxy attivo per il provider dato, o null se disabilitato o assente.</summary>
    ProxyConfig? GetActive(string providerKey);

    /// <summary>Il proxy attivo è "sano" (non recentemente failed)?</summary>
    ProxyConfig? GetNextCandidate(string providerKey);

    void MarkFailed(ProxyConfig proxy, string providerKey);
    void MarkSuccess(ProxyConfig proxy, string providerKey);
}

public static class ProviderKeys
{
    public const string AnimeWorld = "AnimeWorld";
    public const string AnimeSaturn = "AnimeSaturn";
    public const string StreamingCommunity = "StreamingCommunity";
    public const string Downloads = "Downloads";
}