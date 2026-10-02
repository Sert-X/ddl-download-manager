using Dapper;
using DownloadManager.Models;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Persistence;

public class ProxyConfigRepository
{
    private readonly string _dbPath;

    public ProxyConfigRepository()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DDLDownloadManager");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "downloads.db");
    }

    private SqliteConnection Open() => new($"Data Source={_dbPath}");

    public async Task InitializeAsync()
    {
        using var conn = Open();
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS Proxies (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Protocol INTEGER NOT NULL,
                Host TEXT NOT NULL,
                Port INTEGER NOT NULL,
                Auth INTEGER NOT NULL,
                Username TEXT,
                PasswordEnc TEXT,
                IsEnabled INTEGER NOT NULL DEFAULT 0,
                TestResult TEXT,
                LastTested TEXT
            );
        ");

        // Migrazione: aggiungi le colonne se non esistono
        await TryAddColumnAsync(conn, "Proxies", "TestResult", "TEXT");
        await TryAddColumnAsync(conn, "Proxies", "LastTested", "TEXT");
    }

    private static async Task TryAddColumnAsync(SqliteConnection conn, string table, string column, string type)
    {
        try
        {
            await conn.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {type};");
        }
        catch
        {
            // Colonna già presente: ignora.
        }
    }

    public async Task<List<ProxyConfig>> GetAllAsync()
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<dynamic>("SELECT * FROM Proxies ORDER BY Id");

        var list = new List<ProxyConfig>();
        foreach (var r in rows)
        {
            list.Add(new ProxyConfig
            {
                Id = (int)(long)r.Id,
                Name = (string)r.Name,
                Protocol = (ProxyProtocol)(long)r.Protocol,
                Host = (string)r.Host,
                Port = (int)(long)r.Port,
                Auth = (ProxyAuthType)(long)r.Auth,
                Username = (string?)r.Username ?? "",
                Password = Decrypt((string?)r.PasswordEnc),
                IsEnabled = ((long)r.IsEnabled) == 1,
                TestResult = (string?)r.TestResult ?? "",
                LastTested = r.LastTested is string s && DateTime.TryParse(s, out var dt) ? dt : default
            });
        }
        return list;
    }

    public async Task InsertAsync(ProxyConfig p)
    {
        using var conn = Open();
        var id = await conn.ExecuteScalarAsync<long>(@"
            INSERT INTO Proxies (Name, Protocol, Host, Port, Auth, Username, PasswordEnc, IsEnabled, LastTested)
            VALUES (@Name, @Protocol, @Host, @Port, @Auth, @Username, @PasswordEnc, @IsEnabled, @LastTested);
            SELECT last_insert_rowid();",
            new
            {
                p.Name,
                Protocol = (int)p.Protocol,
                p.Host,
                p.Port,
                Auth = (int)p.Auth,
                p.Username,
                PasswordEnc = Encrypt(p.Password),
                IsEnabled = p.IsEnabled ? 1 : 0,
                TestResult = p.TestResult ?? "",
                LastTested = p.LastTested == default ? null : p.LastTested.ToString("o")
            });
        p.Id = (int)id;
    }

    public async Task UpdateAsync(ProxyConfig p)
    {
        using var conn = Open();
        await conn.ExecuteAsync(@"
            UPDATE Proxies SET
                Name = @Name, Protocol = @Protocol, Host = @Host, Port = @Port,
                Auth = @Auth, Username = @Username, PasswordEnc = @PasswordEnc,
                IsEnabled = @IsEnabled, LastTested = @LastTested
            WHERE Id = @Id",
            new
            {
                p.Id,
                p.Name,
                Protocol = (int)p.Protocol,
                p.Host,
                p.Port,
                Auth = (int)p.Auth,
                p.Username,
                PasswordEnc = Encrypt(p.Password),
                IsEnabled = p.IsEnabled ? 1 : 0,
                TestResult = p.TestResult ?? "",
                LastTested = p.LastTested == default ? null : p.LastTested.ToString("o")
            });
    }

    public async Task DeleteAsync(int id)
    {
        using var conn = Open();
        await conn.ExecuteAsync("DELETE FROM Proxies WHERE Id = @Id", new { Id = id });
    }

    // --- DPAPI (Windows only) ---

    private static string Encrypt(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(plain);
        var enc = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(enc);
    }

    private static string Decrypt(string? enc)
    {
        if (string.IsNullOrEmpty(enc)) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(enc);
            var dec = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(dec);
        }
        catch { return string.Empty; }
    }
}