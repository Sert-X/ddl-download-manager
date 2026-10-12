namespace DownloadManager.Services.Streaming;

/// <summary>
/// Registro dei provider streaming disponibili, iniettato via DI.
/// </summary>
public class StreamingProviderRegistry
{
    private readonly Dictionary<string, IStreamingProvider> _providers
        = new(StringComparer.OrdinalIgnoreCase);

    public StreamingProviderRegistry(IEnumerable<IStreamingProvider> providers)
    {
        foreach (var p in providers)
            _providers[p.Name] = p;
    }

    public IEnumerable<string> Names => _providers.Keys;

    public IStreamingProvider? Get(string name)
        => _providers.TryGetValue(name, out var p) ? p : null;

    public IReadOnlyCollection<IStreamingProvider> All
        => _providers.Values;
}