using System.Security.Cryptography;
using System.Text;
using Dapper;
using DownloadManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence;

public class SftpConfigRepository
{
    private readonly ILogger<SftpConfigRepository> _logger;
    private readonly string _connectionString;

    public SftpConfigRepository(ILogger<SftpConfigRepository> logger)
    {
        _logger = logger;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DDLDownloadManager");
        Directory.CreateDirectory(appDataDir);

        var dbPath = Path.Combine(appDataDir, "downloads.db");
        _connectionString = $"Data Source={dbPath}";
    }

    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS SftpConfigs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Host TEXT NOT NULL,
                Port INTEGER NOT NULL DEFAULT 22,
                Username TEXT NOT NULL,
                Password TEXT NOT NULL DEFAULT '',
                UseKeyAuth INTEGER NOT NULL DEFAULT 0,
                PrivateKeyPath TEXT NOT NULL DEFAULT '',
                PrivateKeyPassphrase TEXT NOT NULL DEFAULT '',
                RemoteBasePath TEXT NOT NULL DEFAULT '/',
                SshHostKeyFingerprint TEXT NOT NULL DEFAULT ''
            );
        ");

        try
        {
            await conn.ExecuteAsync(
                "ALTER TABLE SftpConfigs ADD COLUMN SshHostKeyFingerprint TEXT NOT NULL DEFAULT '';");
        }
        catch { /* colonna già presente */ }

        _logger.LogInformation("Tabella SftpConfigs pronta");
    }

    public async Task<List<SftpConfig>> GetAllAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        var rows = await conn.QueryAsync<SftpConfigRow>(@"
            SELECT * FROM SftpConfigs ORDER BY Id ASC;
        ");

        return rows.Select(ToConfig).ToList();
    }

    public async Task<SftpConfig?> GetAsync(int id)
    {
        await using var conn = new SqliteConnection(_connectionString);
        var row = await conn.QueryFirstOrDefaultAsync<SftpConfigRow>(@"
            SELECT * FROM SftpConfigs WHERE Id = @Id;
        ", new { Id = id });

        return row == null ? null : ToConfig(row);
    }

    public async Task<int> InsertAsync(SftpConfig config)
    {
        await using var conn = new SqliteConnection(_connectionString);
        var id = await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO SftpConfigs (Name, Host, Port, Username, Password, UseKeyAuth, PrivateKeyPath, PrivateKeyPassphrase, RemoteBasePath, SshHostKeyFingerprint)
            VALUES (@Name, @Host, @Port, @Username, @Password, @UseKeyAuth, @PrivateKeyPath, @PrivateKeyPassphrase, @RemoteBasePath, @SshHostKeyFingerprint);
            SELECT last_insert_rowid();
        ", new
        {
            config.Name,
            config.Host,
            config.Port,
            config.Username,
            Password = Protect(config.Password),
            UseKeyAuth = config.UseKeyAuth ? 1 : 0,
            config.PrivateKeyPath,
            PrivateKeyPassphrase = Protect(config.PrivateKeyPassphrase),
            config.RemoteBasePath,
            config.SshHostKeyFingerprint
        });

        config.Id = id;
        return id;
    }

    public async Task UpdateAsync(SftpConfig config)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(@"
            UPDATE SftpConfigs
            SET Name = @Name,
                Host = @Host,
                Port = @Port,
                Username = @Username,
                Password = @Password,
                UseKeyAuth = @UseKeyAuth,
                PrivateKeyPath = @PrivateKeyPath,
                PrivateKeyPassphrase = @PrivateKeyPassphrase,
                RemoteBasePath = @RemoteBasePath,
                SshHostKeyFingerprint = @SshHostKeyFingerprint
            WHERE Id = @Id;
        ", new
        {
            config.Id,
            config.Name,
            config.Host,
            config.Port,
            config.Username,
            Password = Protect(config.Password),
            UseKeyAuth = config.UseKeyAuth ? 1 : 0,
            config.PrivateKeyPath,
            PrivateKeyPassphrase = Protect(config.PrivateKeyPassphrase),
            config.RemoteBasePath,
            config.SshHostKeyFingerprint
        });
    }

    public async Task DeleteAsync(int id)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM SftpConfigs WHERE Id = @Id;", new { Id = id });
    }

    private static SftpConfig ToConfig(SftpConfigRow r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Host = r.Host,
        Port = r.Port,
        Username = r.Username,
        Password = Unprotect(r.Password),
        UseKeyAuth = r.UseKeyAuth == 1,
        PrivateKeyPath = r.PrivateKeyPath,
        PrivateKeyPassphrase = Unprotect(r.PrivateKeyPassphrase),
        RemoteBasePath = r.RemoteBasePath,
        SshHostKeyFingerprint = r.SshHostKeyFingerprint
    };

    // ============================================================
    //  DPAPI — cifratura credenziali (solo Windows)
    // ============================================================

    private const string DpapiPrefix = "dpapi:";

    private static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return string.Empty;
        if (!OperatingSystem.IsWindows()) return plaintext;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return DpapiPrefix + Convert.ToBase64String(encrypted);
        }
        catch
        {
            return plaintext;
        }
    }

    private static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return stored;
        if (!OperatingSystem.IsWindows()) return stored;

        try
        {
            var b64 = stored.Substring(DpapiPrefix.Length);
            var encrypted = Convert.FromBase64String(b64);
            var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return stored;
        }
    }

    private class SftpConfigRow
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Host { get; set; } = "";
        public int Port { get; set; }
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public int UseKeyAuth { get; set; }
        public string PrivateKeyPath { get; set; } = "";
        public string PrivateKeyPassphrase { get; set; } = "";
        public string RemoteBasePath { get; set; } = "/";
        public string SshHostKeyFingerprint { get; set; } = "";
    }
}