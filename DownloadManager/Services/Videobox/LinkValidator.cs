using System;
using System.Collections.Generic;

namespace DownloadManager.Services.Videobox;

public static class LinkValidator
{
    public static bool IsValidDomain(string url, IEnumerable<string> validDomains)
    {
        var host = HostOf(url);
        if (string.IsNullOrEmpty(host)) return false;
        foreach (var domain in validDomains)
        {
            var clean = CleanDomain(domain);
            if (clean.Length > 0 && host.Equals(clean, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

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

    /// <summary>Dominio impostato ripulito per il confronto (solo l'host, minuscolo). "" se vuoto.</summary>
    public static string CleanDomain(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return string.Empty;
        string clean = domain.Trim().ToLowerInvariant();
        clean = clean.Replace("https://", "").Replace("http://", "").TrimEnd('/');
        var parts = clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : clean;
    }
}
