using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Models;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence;

public class AccountRepository
{
    private readonly ILogger<AccountRepository> _logger;
    private readonly string _filePath;

    public AccountRepository(ILogger<AccountRepository> logger)
    {
        _logger = logger;
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DDLDownloadManager");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "account.dat");
    }

    public AccountCredentials Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return new AccountCredentials();

            var protectedBytes = File.ReadAllBytes(_filePath);
            var plainBytes = ProtectedData.Unprotect(
                protectedBytes, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plainBytes);
            return JsonSerializer.Deserialize<AccountCredentials>(json) ?? new AccountCredentials();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossibile leggere credenziali account");
            return new AccountCredentials();
        }
    }

    public void Save(AccountCredentials creds)
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
            _logger.LogWarning(ex, "Impossibile salvare credenziali account");
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
            _logger.LogWarning(ex, "Impossibile cancellare credenziali account");
        }
    }
}