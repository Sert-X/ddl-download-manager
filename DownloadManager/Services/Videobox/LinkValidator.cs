using System;
using System.Collections.Generic;

namespace DownloadManager.Services.Videobox;

public static class LinkValidator
{
    public static bool IsValidDomain(string url, IEnumerable<string> validDomains)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        string host = string.Empty;
        try
        {
            // Aggiunge https:// se manca, per far funzionare Uri
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            var uri = new Uri(url, UriKind.Absolute);
            host = uri.Host;
        }
        catch
        {
            // Fallback manuale se l'URI è malformato
            var parts = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) host = parts[0];
        }

        if (string.IsNullOrEmpty(host)) return false;

        foreach (var domain in validDomains)
        {
            if (string.IsNullOrWhiteSpace(domain)) continue;

            // Pulizia profonda del dominio per il confronto
            string cleanDomain = domain.Trim().ToLowerInvariant();
            cleanDomain = cleanDomain.Replace("https://", "").Replace("http://", "").TrimEnd('/');

            // Estrae solo la parte host del dominio pulito
            var parts = cleanDomain.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) cleanDomain = parts[0];

            if (host.Equals(cleanDomain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}