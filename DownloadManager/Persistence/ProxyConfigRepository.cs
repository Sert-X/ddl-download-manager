using Dapper;
using DownloadManager.Models;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
                TestResult TEXT,
                LastTested TEXT,
                ProviderResultsJson TEXT,
                ConsecutiveFailures INTEGER NOT NULL DEFAULT 0,
                LastFailedUtc TEXT,
                EnabledForAnimeWorld INTEGER NOT NULL DEFAULT 0,
                EnabledForAnimeSaturn INTEGER NOT NULL DEFAULT 0,
                EnabledForStreamingCommunity INTEGER NOT NULL DEFAULT 0,
                EnabledForDownloads INTEGER NOT NULL DEFAULT 0,
                ManuallyDisabledAw INTEGER NOT NULL DEFAULT 0,
                ManuallyDisabledAs INTEGER NOT NULL DEFAULT 0,
                ManuallyDisabledSc INTEGER NOT NULL DEFAULT 0,
                ManuallyDisabledDl INTEGER NOT NULL DEFAULT 0,
                RemovedFromProvidersJson TEXT
            );
        ");

        // Migrazioni progressive
        await TryAddColumnAsync(conn, "Proxies", "TestResult", "TEXT");
        await TryAddColumnAsync(conn, "Proxies", "LastTested", "TEXT");
        await TryAddColumnAsync(conn, "Proxies", "ProviderResultsJson", "TEXT");
        await TryAddColumnAsync(conn, "Proxies", "ConsecutiveFailures", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "Proxies", "LastFailedUtc", "TEXT");

        await TryAddColumnAsync(conn, "Proxies", "EnabledForAnimeWorld", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "Proxies", "EnabledForAnimeSaturn", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "Proxies", "EnabledForStreamingCommunity", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "Proxies", "EnabledForDownloads", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "Proxies", "ManuallyDisabledAw", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "Proxies", "ManuallyDisabledAs", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "Proxies", "ManuallyDisabledSc", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "Proxies", "ManuallyDisabledDl", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "Proxies", "RemovedFromProvidersJson", "TEXT");
    }

    private static async Task TryAddColumnAsync(SqliteConnection conn, string table, string column, string type)
    {
        try { await conn.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {type};"); }
        catch { /* già presente */ }
    }

    public async Task<List<ProxyConfig>> GetAllAsync()
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<dynamic>("SELECT * FROM Proxies ORDER BY Id");

        var list = new List<ProxyConfig>();
        foreach (var r in rows)
        {
            var proxy = new ProxyConfig
            {
                Id = (int)(long)r.Id,
                Name = (string)r.Name,
                Protocol = (ProxyProtocol)(long)r.Protocol,
                Host = (string)r.Host,
                Port = (int)(long)r.Port,
                Auth = (ProxyAuthType)(long)r.Auth,
                Username = (string?)r.Username ?? "",
                Password = Decrypt((string?)r.PasswordEnc),
                TestResult = (string?)r.TestResult ?? "",
                LastTested = r.LastTested is string s && DateTime.TryParse(s, out var dt) ? dt : default,
                ConsecutiveFailures = r.ConsecutiveFailures is long cf ? (int)cf : 0,
                LastFailedUtc = r.LastFailedUtc is string lf && DateTime.TryParse(lf, out var lfdt) ? lfdt : null,

                IsEnabledForAnimeWorld = SafeBool(r, "EnabledForAnimeWorld"),
                IsEnabledForAnimeSaturn = SafeBool(r, "EnabledForAnimeSaturn"),
                IsEnabledForStreamingCommunity = SafeBool(r, "EnabledForStreamingCommunity"),
                IsEnabledForDownloads = SafeBool(r, "EnabledForDownloads"),

                ManuallyDisabledForAnimeWorld = SafeBool(r, "ManuallyDisabledAw"),
                ManuallyDisabledForAnimeSaturn = SafeBool(r, "ManuallyDisabledAs"),
                ManuallyDisabledForStreamingCommunity = SafeBool(r, "ManuallyDisabledSc"),
                ManuallyDisabledForDownloads = SafeBool(r, "ManuallyDisabledDl")
            };

            // Deserializza ProviderResults
            try
            {
                var json = (string?)r.ProviderResultsJson;
                if (!string.IsNullOrEmpty(json))
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict != null) proxy.ProviderResults = dict;
                }
            }
            catch { }

            // ▼▼▼ AGGIUNGI QUESTE 12 RIGHE ▼▼▼
            // Deserializza RemovedFromProviders
            try
            {
                var json2 = (string?)r.RemovedFromProvidersJson;
                if (!string.IsNullOrEmpty(json2))
                {
                    var set = JsonSerializer.Deserialize<HashSet<string>>(json2);
                    if (set != null) proxy.RemovedFromProviders = set;
                }
            }
            catch { }
            // ▲▲▲ FINE ▼▲▲

            list.Add(proxy);
        }
        return list;
    }

    private static bool SafeBool(dynamic row, string col)
    {
        try
        {
            var val = ((IDictionary<string, object>)row)[col];
            return val is long l && l != 0;
        }
        catch { return false; }
    }

    private const string InsertSql = @"
            INSERT INTO Proxies
                (Name, Protocol, Host, Port, Auth, Username, PasswordEnc,
                 TestResult, LastTested, ProviderResultsJson,
                 ConsecutiveFailures, LastFailedUtc,
                 EnabledForAnimeWorld, EnabledForAnimeSaturn,
                 EnabledForStreamingCommunity, EnabledForDownloads,
                 ManuallyDisabledAw, ManuallyDisabledAs,
                 ManuallyDisabledSc, ManuallyDisabledDl,
                 RemovedFromProvidersJson)
            VALUES
                (@Name, @Protocol, @Host, @Port, @Auth, @Username, @PasswordEnc,
                 @TestResult, @LastTested, @ProviderResultsJson,
                 @ConsecutiveFailures, @LastFailedUtc,
                 @EnabledAw, @EnabledAs, @EnabledSc, @EnabledDl,
                 @ManualAw, @ManualAs, @ManualSc, @ManualDl,
                 @RemovedFromProvidersJson);
            SELECT last_insert_rowid();";

    private const string UpdateSql = @"
            UPDATE Proxies SET
                Name = @Name, Protocol = @Protocol, Host = @Host, Port = @Port,
                Auth = @Auth, Username = @Username, PasswordEnc = @PasswordEnc,
                TestResult = @TestResult, LastTested = @LastTested,
                ProviderResultsJson = @ProviderResultsJson,
                ConsecutiveFailures = @ConsecutiveFailures, LastFailedUtc = @LastFailedUtc,
                EnabledForAnimeWorld = @EnabledAw,
                EnabledForAnimeSaturn = @EnabledAs,
                EnabledForStreamingCommunity = @EnabledSc,
                EnabledForDownloads = @EnabledDl,
                ManuallyDisabledAw = @ManualAw,
                ManuallyDisabledAs = @ManualAs,
                ManuallyDisabledSc = @ManualSc,
                ManuallyDisabledDl = @ManualDl,
                RemovedFromProvidersJson = @RemovedFromProvidersJson
            WHERE Id = @Id";

    public async Task InsertAsync(ProxyConfig p)
    {
        using var conn = Open();
        var id = await conn.ExecuteScalarAsync<long>(InsertSql, BuildParams(p, includeId: false));
        p.Id = (int)id;
    }

    public async Task UpdateAsync(ProxyConfig p)
    {
        using var conn = Open();
        await conn.ExecuteAsync(UpdateSql, BuildParams(p, includeId: true));
    }

    /// <summary>
    /// Salva più proxy (insert se Id == 0, altrimenti update) con una sola
    /// connessione e una sola transazione: molto più veloce di N chiamate singole.
    /// </summary>
    public async Task SaveManyAsync(IReadOnlyCollection<ProxyConfig> proxies)
    {
        if (proxies.Count == 0) return;

        using var conn = Open();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        foreach (var p in proxies)
        {
            if (p.Id == 0)
                p.Id = (int)await conn.ExecuteScalarAsync<long>(InsertSql, BuildParams(p, includeId: false), tx);
            else
                await conn.ExecuteAsync(UpdateSql, BuildParams(p, includeId: true), tx);
        }
        tx.Commit();
    }

    private static object BuildParams(ProxyConfig p, bool includeId)
    {
        var dict = new Dictionary<string, object?>
        {
            ["Name"] = p.Name,
            ["Protocol"] = (int)p.Protocol,
            ["Host"] = p.Host,
            ["Port"] = p.Port,
            ["Auth"] = (int)p.Auth,
            ["Username"] = p.Username,
            ["PasswordEnc"] = Encrypt(p.Password),
            ["TestResult"] = p.TestResult ?? "",
            ["LastTested"] = p.LastTested == default ? null : p.LastTested.ToString("o"),
            ["ProviderResultsJson"] = SerializeProviderResults(p),
            ["ConsecutiveFailures"] = p.ConsecutiveFailures,
            ["LastFailedUtc"] = p.LastFailedUtc?.ToString("o"),
            ["EnabledAw"] = p.IsEnabledForAnimeWorld ? 1 : 0,
            ["EnabledAs"] = p.IsEnabledForAnimeSaturn ? 1 : 0,
            ["EnabledSc"] = p.IsEnabledForStreamingCommunity ? 1 : 0,
            ["EnabledDl"] = p.IsEnabledForDownloads ? 1 : 0,
            ["ManualAw"] = p.ManuallyDisabledForAnimeWorld ? 1 : 0,
            ["ManualAs"] = p.ManuallyDisabledForAnimeSaturn ? 1 : 0,
            ["ManualSc"] = p.ManuallyDisabledForStreamingCommunity ? 1 : 0,
            ["ManualDl"] = p.ManuallyDisabledForDownloads ? 1 : 0,
            // TEMPORANEO: non persisto questa property, così la lista rimossa vale solo per la sessione.
            // Quando avremo confermato che la vista funziona, riattiviamo la persistenza.
            ["RemovedFromProvidersJson"] = p.RemovedFromProviders.Count == 0
                ? ""
                : JsonSerializer.Serialize(p.RemovedFromProviders),
        };
        if (includeId) dict["Id"] = p.Id;
        return dict;
    }

    private static string SerializeProviderResults(ProxyConfig p)
    {
        try
        {
            if (p.ProviderResults.Count == 0) return "";
            return JsonSerializer.Serialize(p.ProviderResults);
        }
        catch { return ""; }
    }

    public async Task DeleteAsync(int id)
    {
        using var conn = Open();
        await conn.ExecuteAsync("DELETE FROM Proxies WHERE Id = @Id", new { Id = id });
    }

    public async Task DeleteManyAsync(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0) return;
        using var conn = Open();
        await conn.ExecuteAsync("DELETE FROM Proxies WHERE Id IN @Ids", new { Ids = ids });
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