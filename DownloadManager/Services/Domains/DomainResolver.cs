using DownloadManager.Models;
using DownloadManager.Persistence;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Domains;

/// <summary>
/// Risolve l'URL base corrente di ogni provider (anime + streaming).
/// Legge/scrive in AppSettings.ProviderDomains e fornisce i default.
/// </summary>
public class DomainResolver
{
    private readonly SettingsService _settings;
    private readonly ILogger<DomainResolver> _logger;

    /// <summary>
    /// URL di default per provider (hardcoded). Vanno aggiornati qui se il sito
    /// cambia dominio base.
    ///
    /// NOTA StreamingCommunity: il dominio cambia molto spesso per eludere
    /// gli oscuramenti AGCOM. Questo default è solo un punto di partenza:
    /// aggiornalo dalla tab Domini quando cambia.
    /// </summary>
    private readonly Dictionary<string, string> _defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        { "AnimeWorld", "https://www.animeworld.ac" },
        { "AnimeSaturn", "https://www.animesaturn.net" },
        { "StreamingCommunity", "https://streamingcommunity.rip" },
        { Cb01, "https://cb01one.com" },
        { Maxstream, "https://maxstream.video" }
    };

    public const string Cb01 = "CB01";
    public const string Maxstream = "Maxstream";

    /// <summary>"https://www.maxstream.video/x" → "maxstream.video": confronto tra host senza "www.".</summary>
    public static string HostOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        var h = u.Host.ToLowerInvariant();
        return h.StartsWith("www.") ? h[4..] : h;
    }

    public DomainResolver(SettingsService settings, ILogger<DomainResolver> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Ritorna l'URL base corrente per un provider (custom o default).
    /// </summary>
    public string GetBaseUrl(string providerName)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("Provider name obbligatorio", nameof(providerName));

        var domains = _settings.Current.ProviderDomains;

        if (domains.TryGetValue(providerName, out var custom)
            && !string.IsNullOrWhiteSpace(custom))
        {
            return custom.TrimEnd('/');
        }

        if (_defaults.TryGetValue(providerName, out var def))
            return def.TrimEnd('/');

        throw new InvalidOperationException(
            $"Nessun dominio configurato per il provider '{providerName}'.");
    }

    /// <summary>
    /// Ritorna l'URL di default per un provider.
    /// </summary>
    public string GetDefaultUrl(string providerName)
    {
        if (_defaults.TryGetValue(providerName, out var def))
            return def;
        return string.Empty;
    }

    /// <summary>
    /// Salva l'URL custom per un provider. Se uguale al default, rimuove l'override.
    /// </summary>
    public void SetBaseUrl(string providerName, string url)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return;

        var clean = (url ?? "").Trim().TrimEnd('/');
        var def = GetDefaultUrl(providerName);

        if (string.IsNullOrWhiteSpace(clean)
            || string.Equals(clean, def, StringComparison.OrdinalIgnoreCase))
        {
            // Rimuove override (usa default)
            if (_settings.Current.ProviderDomains.Remove(providerName))
            {
                _settings.Save();
                _logger.LogInformation(
                    "[DOMAIN] {Provider}: rimosso override, uso default {Default}",
                    providerName, def);
            }
            return;
        }

        _settings.Current.ProviderDomains[providerName] = clean;
        _settings.Save();

        _logger.LogInformation(
            "[DOMAIN] {Provider}: URL aggiornato a {Url}", providerName, clean);
    }

    /// <summary>Nomi dei provider con default noti.</summary>
    public IEnumerable<string> KnownProviders => _defaults.Keys;

    /// <summary>
    /// Registra un nuovo provider nel resolver a runtime (idempotente).
    /// Se il provider esiste già, non fa nulla.
    /// Utile per provider plug-in o caricati dinamicamente.
    /// </summary>
    public void RegisterProvider(string providerName, string defaultUrl)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return;
        if (_defaults.ContainsKey(providerName)) return;

        _defaults[providerName] = defaultUrl.TrimEnd('/');
        _logger.LogInformation(
            "[DOMAIN] Registrato nuovo provider {Provider} con default {Url}",
            providerName, defaultUrl);
    }
}