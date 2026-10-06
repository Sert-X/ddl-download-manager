namespace DownloadManager.Services.Anime;

/// <summary>
/// Registro dei provider anime disponibili. Oggi ce n'è uno solo
/// (AnimeWorld). Quando se ne aggiunge un secondo, basta registrarlo
/// in DI e comparirà qui automaticamente.
/// </summary>
public class AnimeProviderRegistry
{
    private readonly Dictionary<string, IAnimeProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public AnimeProviderRegistry(IEnumerable<IAnimeProvider> providers)
    {
        foreach (var p in providers)
            _providers[p.Name] = p;
    }

    public IReadOnlyCollection<string> Names => _providers.Keys;

    public IAnimeProvider? Get(string name)
        => _providers.TryGetValue(name, out var p) ? p : null;

    public IAnimeProvider Default
        => _providers.Values.FirstOrDefault()
           ?? throw new InvalidOperationException("Nessun provider anime registrato.");
}