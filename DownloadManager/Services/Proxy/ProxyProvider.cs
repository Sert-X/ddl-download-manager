using DownloadManager.Models;

namespace DownloadManager.Services.Proxy;

public class ProxyProvider : IProxyProvider
{
    private readonly ProxyService _service;

    public ProxyProvider(ProxyService service)
    {
        _service = service;
    }

    public ProxyConfig? GetActive() => _service.Active;
}