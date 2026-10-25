using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Models;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence;

public class MaxstreamAccountRepository
{
    private readonly ILogger<MaxstreamAccountRepository> _logger;
    private readonly string _filePath;

    public MaxstreamAccountRepository(ILogger<MaxstreamAccountRepository> logger)
    {
        _logger = logger;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DDLDownloadManager");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "maxstream_account.dat");
    }

    public MaxstreamCredentials Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new MaxstreamCredentials();

            var protectedBytes = File.ReadAllBytes(_filePath);
            var plainBytes = ProtectedData.Unprotect(
                protectedBytes, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<MaxstreamCredentials>(json) ?? new MaxstreamCredentials();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossibile leggere credenziali Maxstream");
            return new MaxstreamCredentials();
        }
    }

    public void Save(MaxstreamCredentials creds)
    {
        try
        {
            var json = JsonSerializer.Serialize(creds);
            var plainBytes = Encoding.UTF8.GetBytes(json);
            var protectedBytes = ProtectedData.Protect(
                plainBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_filePath, protectedBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossibile salvare credenziali Maxstream");
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossibile cancellare credenziali Maxstream");
        }
    }
}
