using DownloadManager.Models;

namespace DownloadManager.Services.Proxy;

/// <summary>
/// Ponte tra ProxyService e i consumer (DownloadService, AnimeWorldService).
/// Ritorna il proxy attivo al momento della chiamata, così i servizi non
/// devono essere ricreati quando l'utente cambia proxy.
/// </summary>
public interface IProxyProvider
{
    ProxyConfig? GetActive();
}