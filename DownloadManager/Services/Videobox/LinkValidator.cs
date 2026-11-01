using System;
using System.Collections.Generic;

namespace DownloadManager.Services.Videobox;

public static class LinkValidator
{
    /// <summary>
    /// Il link è su uno dei domini validi. Un dominio può avere anche un percorso
    /// (es. "https://m3u.mugiwara.fans/schls-fast/"): allora valgono solo i link sotto quel percorso.
    /// </summary>
    public static bool IsValidDomain(string url, IEnumerable<string> validDomains)
        => new DomainMatcher(validDomains).IsValid(url);

    /// <summary>Host del link ("" se non si riesce a leggerlo).</summary>
    public static string HostOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        try
        {
            // Aggiunge https:// se manca, per far funzionare Uri
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            return new Uri(url, UriKind.Absolute).Host;
        }
        catch
        {
            // Fallback manuale se l'URI è malformato
            var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0 ? parts[0] : string.Empty;
        }
    }

    /// <summary>Percorso del link senza codifica (es. "/schls-fast/12256/0.m3u8"), "" se non leggibile.</summary>
    public static string PathOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return string.Empty;
        try { return Uri.UnescapeDataString(u.AbsolutePath); } catch { return u.AbsolutePath; }
    }

    /// <summary>Dominio impostato ripulito per il confronto (solo l'host, minuscolo). "" se vuoto.</summary>
    public static string CleanDomain(string? domain) => SplitDomain(domain).Host;

    /// <summary>
    /// Host (minuscolo) e percorso di un dominio impostato: "https://a.it/schls-fast/" diventa
    /// ("a.it", "/schls-fast"); senza percorso il percorso è "".
    /// </summary>
    public static (string Host, string Path) SplitDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return ("", "");
        var clean = domain.Trim();
        int scheme = clean.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) clean = clean[(scheme + 3)..];
        clean = clean.Split('?', '#')[0];
        int slash = clean.IndexOf('/');
        var host = (slash < 0 ? clean : clean[..slash]).ToLowerInvariant();
        var path = slash < 0 ? "" : clean[slash..].TrimEnd('/');
        try { path = Uri.UnescapeDataString(path); } catch { }
        return (host, path);
    }
}

/// <summary>
/// Confronto veloce di molti link con i domini validi (calcolato una volta per tutti i link).
/// Dominio senza percorso: vale tutto l'host. Con percorso: solo i link in quella cartella
/// ("/schls-fast" accetta "/schls-fast/12256/0.m3u8", non "/ashls-fast/..." né "/schls-fast2/...").
/// </summary>
public sealed class DomainMatcher
{
    private readonly HashSet<string> _wholeHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _pathsByHost = new(StringComparer.OrdinalIgnoreCase);

    public DomainMatcher(IEnumerable<string> domains)
    {
        foreach (var d in domains)
        {
            var (host, path) = LinkValidator.SplitDomain(d);
            if (host.Length == 0) continue;
            if (path.Length == 0) { _wholeHosts.Add(host); continue; }
            if (!_pathsByHost.TryGetValue(host, out var list)) _pathsByHost[host] = list = new List<string>();
            list.Add(path);
        }
    }

    public bool IsValid(string url) => IsValid(url, LinkValidator.HostOf(url));

    /// <param name="host">Host del link, se già calcolato.</param>
    public bool IsValid(string url, string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        if (_wholeHosts.Contains(host)) return true;
        if (!_pathsByHost.TryGetValue(host, out var paths)) return false;
        var linkPath = LinkValidator.PathOf(url);
        foreach (var p in paths)
            if (linkPath.Equals(p, StringComparison.OrdinalIgnoreCase)
                || linkPath.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
