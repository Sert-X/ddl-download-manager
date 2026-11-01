using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence;

/// <summary>
/// Chiave di TMDB, salvata cifrata per l'utente di Windows (come le credenziali di
/// StreamingCommunity e Maxstream). Non finisce mai in settings.json né nei log.
/// </summary>
public class TmdbKeyRepository
{
    private readonly ILogger<TmdbKeyRepository> _logger;
    private readonly string _filePath;

    public TmdbKeyRepository(ILogger<TmdbKeyRepository> logger)
    {
        _logger = logger;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDLDownloadManager");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "tmdb.dat");
    }

    public string Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return "";
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_filePath), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossibile leggere la chiave TMDB");
            return "";
        }
    }

    public void Save(string key)
    {
        try
        {
            var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_filePath, data);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Impossibile salvare la chiave TMDB"); }
    }

    public void Clear()
    {
        try { if (File.Exists(_filePath)) File.Delete(_filePath); }
        catch (Exception ex) { _logger.LogWarning(ex, "Impossibile cancellare la chiave TMDB"); }
    }
}
