using DownloadManager.Models;

namespace DownloadManager.Services.Proxy;

public static class ProxyFileParser
{
    /// <summary>
    /// Parsa un file di testo/csv e restituisce la lista di ProxyConfig.
    /// Formati supportati (uno per riga):
    ///   host:port
    ///   protocol://host:port
    ///   protocol://user:pass@host:port
    ///   host,port,user,pass
    ///   protocol,host,port,user,pass
    ///   host:port:user:pass
    /// Righe vuote o che iniziano con '#' sono ignorate.
    /// </summary>
    public static (List<ProxyConfig> proxies, List<string> errors) Parse(string content)
    {
        var proxies = new List<ProxyConfig>();
        var errors = new List<string>();

        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

            try
            {
                var proxy = ParseLine(line);
                if (proxy != null) proxies.Add(proxy);
            }
            catch (Exception ex)
            {
                errors.Add($"Riga {i + 1}: {ex.Message} — '{Truncate(line, 60)}'");
            }
        }

        return (proxies, errors);
    }

    private static ProxyConfig? ParseLine(string line)
    {
        // 1) Con schema: protocol://[user:pass@]host:port
        if (line.Contains("://"))
            return ParseWithScheme(line);

        // 2) CSV: contiene virgole
        if (line.Contains(','))
            return ParseCsv(line);

        // 3) host:port:user:pass (formato "vecchio" delle liste)
        if (line.Count(c => c == ':') == 3)
            return ParseLegacy(line);

        // 4) host:port
        return ParseHostPort(line);
    }

    private static ProxyConfig ParseWithScheme(string line)
    {
        var uri = new Uri(line);
        var protocol = uri.Scheme switch
        {
            "http" => ProxyProtocol.Http,
            "https" => ProxyProtocol.Http,
            "socks5" or "socks5h" => ProxyProtocol.Socks5,
            _ => throw new Exception($"Schema non supportato: {uri.Scheme}")
        };

        var p = new ProxyConfig
        {
            Protocol = protocol,
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 8080
        };

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            p.Auth = ProxyAuthType.UserPassword;
            p.Username = Uri.UnescapeDataString(parts[0]);
            p.Password = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
        }

        return p;
    }

    private static ProxyConfig ParseCsv(string line)
    {
        var parts = line.Split(',').Select(p => p.Trim()).ToArray();

        if (parts.Length < 2)
            throw new Exception("CSV richiede almeno host,port");

        var p = new ProxyConfig();
        int offset = 0;

        // Se il primo pezzo è un protocollo noto
        if (parts[0].Equals("http", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("https", StringComparison.OrdinalIgnoreCase) ||
            parts[0].Equals("socks5", StringComparison.OrdinalIgnoreCase))
        {
            p.Protocol = parts[0].ToLowerInvariant() switch
            {
                "socks5" => ProxyProtocol.Socks5,
                _ => ProxyProtocol.Http
            };
            offset = 1;
        }

        p.Host = parts[offset];
        p.Port = int.Parse(parts[offset + 1]);

        if (parts.Length >= offset + 3 && !string.IsNullOrEmpty(parts[offset + 2]))
        {
            p.Auth = ProxyAuthType.UserPassword;
            p.Username = parts[offset + 2];
            p.Password = parts.Length >= offset + 4 ? parts[offset + 3] : "";
        }

        return p;
    }

    private static ProxyConfig ParseLegacy(string line)
    {
        var parts = line.Split(':');
        var p = new ProxyConfig
        {
            Host = parts[0].Trim(),
            Port = int.Parse(parts[1].Trim()),
            Auth = ProxyAuthType.UserPassword,
            Username = parts[2].Trim(),
            Password = parts[3].Trim()
        };
        return p;
    }

    private static ProxyConfig ParseHostPort(string line)
    {
        // Prova protocollo://... senza essere un Uri valido
        var parts = line.Split(':');
        if (parts.Length < 2) throw new Exception("Formato non riconosciuto");

        return new ProxyConfig
        {
            Host = parts[0].Trim(),
            Port = int.Parse(parts[1].Trim())
        };
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "...";
}