using Dapper;
using DownloadManager.Models;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DownloadManager.Persistence;

/// <summary>
/// Archivio proxy (tabella Proxies) e liste per provider (tabella ProxyLists: una riga per
/// coppia proxy-provider con stato, ultimo esito, latenza e attivazione).
/// </summary>
public class ProxyConfigRepository
{
    private readonly string _dbPath;

    /// <summary>Chiavi delle liste provider (ProviderKeys.*), nell'ordine mostrato nella tab.</summary>
    public static readonly string[] ListKeys = { "AnimeWorld", "AnimeSaturn", "StreamingCommunity", "Cb01", "Downloads" };

    public ProxyConfigRepository(string? dbPath = null)
    {
        if (dbPath != null)
        {
            _dbPath = dbPath;
            return;
        }
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
                ConsecutiveFailures INTEGER NOT NULL DEFAULT 0,
                LastFailedUtc TEXT
            );
            CREATE TABLE IF NOT EXISTS ProxyLists (
                ProxyId INTEGER NOT NULL,
                Provider TEXT NOT NULL,
                Status INTEGER NOT NULL DEFAULT 0,
                LastResult TEXT,
                LastTestedUtc TEXT,
                LatencyMs INTEGER,
                IsActive INTEGER NOT NULL DEFAULT 0,
                ManuallyDisabled INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (ProxyId, Provider)
            );");

        await TryAddColumnAsync(conn, "Proxies", "ConsecutiveFailures", "INTEGER NOT NULL DEFAULT 0");
        await TryAddColumnAsync(conn, "Proxies", "LastFailedUtc", "TEXT");

        await MigrateLegacyColumnsAsync(conn);
    }

    private static async Task TryAddColumnAsync(SqliteConnection conn, string table, string column, string type)
    {
        try { await conn.ExecuteAsync($"ALTER TABLE {table} ADD COLUMN {column} {type};"); }
        catch { /* già presente */ }
    }

    /// <summary>
    /// Una tantum: fino alla 1.2.0 ogni proxy stava implicitamente in tutte le liste e lo stato per provider
    /// era in colonne della tabella Proxies (EnabledForX, ManuallyDisabledX, ProviderResultsJson,
    /// RemovedFromProvidersJson). Qui diventa una riga di ProxyLists per provider. I test falliti diventano
    /// "esclusi", così non vengono ritestati. Le vecchie colonne restano ma non si usano più.
    /// </summary>
    private static async Task MigrateLegacyColumnsAsync(SqliteConnection conn)
    {
        await conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS AppMeta (Key TEXT PRIMARY KEY, Value TEXT)");
        const string marker = "ProxyListsMigrated";
        if (await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM AppMeta WHERE Key = @marker", new { marker }) > 0) return;

        var columns = (await conn.QueryAsync<string>("SELECT name FROM pragma_table_info('Proxies')")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = columns.Contains("ProviderResultsJson")
            ? (await conn.QueryAsync("SELECT * FROM Proxies")).Cast<IDictionary<string, object?>>().ToList()
            : new List<IDictionary<string, object?>>();   // database nuovo, niente da migrare

        var legacyActive = new Dictionary<string, (string Enabled, string Manual)>
        {
            ["AnimeWorld"] = ("EnabledForAnimeWorld", "ManuallyDisabledAw"),
            ["AnimeSaturn"] = ("EnabledForAnimeSaturn", "ManuallyDisabledAs"),
            ["StreamingCommunity"] = ("EnabledForStreamingCommunity", "ManuallyDisabledSc"),
            ["Downloads"] = ("EnabledForDownloads", "ManuallyDisabledDl"),
        };

        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        foreach (var r in rows)
        {
            var id = Convert.ToInt64(r["Id"]);
            var results = TryJson<Dictionary<string, string>>(r, "ProviderResultsJson") ?? new();
            var removed = TryJson<HashSet<string>>(r, "RemovedFromProvidersJson") ?? new();
            DateTime? tested = r.TryGetValue("LastTested", out var lt) && lt is string s && DateTime.TryParse(s, out var d)
                ? d.ToUniversalTime() : null;

            // Fallito dove è stato provato e mai riuscito altrove: non lo si mette nelle liste
            // mai testate, altrimenti il primo "Testa da testare" riproverebbe proxy già morti.
            var tests = ListKeys.Where(results.ContainsKey).Select(k => results[k]).ToList();
            bool dead = tests.Any(t => t.StartsWith("❌")) && !tests.Any(t => t.StartsWith("✅"));

            foreach (var key in ListKeys)
            {
                if (dead && !results.ContainsKey(key) && !removed.Contains(key)) continue;

                var entry = new ProxyListEntry { Provider = key };
                if (results.TryGetValue(key, out var res))
                {
                    entry.LastResult = res;
                    entry.LastTestedUtc = tested;
                    entry.Status = res.StartsWith("✅") ? ProxyEntryStatus.Ok
                        : res.StartsWith("❌") ? ProxyEntryStatus.Excluded
                        : ProxyEntryStatus.Untested;
                }
                if (removed.Contains(key)) entry.Status = ProxyEntryStatus.Excluded;

                if (legacyActive.TryGetValue(key, out var cols))
                {
                    entry.IsActive = entry.IsInList && IsTrue(r, cols.Enabled);
                    entry.ManuallyDisabled = IsTrue(r, cols.Manual);
                }
                await conn.ExecuteAsync(InsertEntrySql, EntryParams(id, entry), tx);
            }
        }
        await conn.ExecuteAsync("INSERT OR REPLACE INTO AppMeta (Key, Value) VALUES (@marker, @now)",
            new { marker, now = DateTime.UtcNow.ToString("o") }, tx);
        tx.Commit();
    }

    private static T? TryJson<T>(IDictionary<string, object?> row, string col) where T : class
    {
        try
        {
            return row.TryGetValue(col, out var v) && v is string json && json.Length > 0
                ? JsonSerializer.Deserialize<T>(json) : null;
        }
        catch { return null; }
    }

    private static bool IsTrue(IDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) && v is long l && l != 0;

    // ============================================================
    //  LETTURA
    // ============================================================

    public async Task<List<ProxyConfig>> GetAllAsync()
    {
        using var conn = Open();
        var rows = await conn.QueryAsync<dynamic>("SELECT * FROM Proxies ORDER BY Id");

        var byId = new Dictionary<long, ProxyConfig>();
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
                ConsecutiveFailures = r.ConsecutiveFailures is long cf ? (int)cf : 0,
                LastFailedUtc = r.LastFailedUtc is string lf && DateTime.TryParse(lf, out var lfdt) ? lfdt : null,
            };
            byId[proxy.Id] = proxy;
            list.Add(proxy);
        }

        var entries = await conn.QueryAsync<dynamic>("SELECT * FROM ProxyLists");
        foreach (var e in entries)
        {
            if (!byId.TryGetValue((long)e.ProxyId, out var proxy)) continue;
            var entry = new ProxyListEntry
            {
                Provider = (string)e.Provider,
                Status = (ProxyEntryStatus)(long)e.Status,
                LastResult = (string?)e.LastResult ?? "",
                LastTestedUtc = e.LastTestedUtc is string t && DateTime.TryParse(t, null, System.Globalization.DateTimeStyles.RoundtripKind, out var tdt) ? tdt : null,
                LatencyMs = e.LatencyMs is long ms ? (int)ms : null,
                IsActive = (long)e.IsActive != 0,
                ManuallyDisabled = (long)e.ManuallyDisabled != 0,
            };
            proxy.Lists[entry.Provider] = entry;
        }
        return list;
    }

    // ============================================================
    //  SCRITTURA
    // ============================================================

    private const string InsertSql = @"
            INSERT INTO Proxies (Name, Protocol, Host, Port, Auth, Username, PasswordEnc, ConsecutiveFailures, LastFailedUtc)
            VALUES (@Name, @Protocol, @Host, @Port, @Auth, @Username, @PasswordEnc, @ConsecutiveFailures, @LastFailedUtc);
            SELECT last_insert_rowid();";

    private const string UpdateSql = @"
            UPDATE Proxies SET
                Name = @Name, Protocol = @Protocol, Host = @Host, Port = @Port,
                Auth = @Auth, Username = @Username, PasswordEnc = @PasswordEnc,
                ConsecutiveFailures = @ConsecutiveFailures, LastFailedUtc = @LastFailedUtc
            WHERE Id = @Id";

    private const string InsertEntrySql = @"
            INSERT OR REPLACE INTO ProxyLists (ProxyId, Provider, Status, LastResult, LastTestedUtc, LatencyMs, IsActive, ManuallyDisabled)
            VALUES (@ProxyId, @Provider, @Status, @LastResult, @LastTestedUtc, @LatencyMs, @IsActive, @ManuallyDisabled);";

    public Task InsertAsync(ProxyConfig p) => SaveManyAsync(new[] { p });
    public Task UpdateAsync(ProxyConfig p) => SaveManyAsync(new[] { p });

    /// <summary>
    /// Salva proxy e liste (insert se Id == 0, altrimenti update) in un'unica transazione.
    /// Le liste del proxy vengono riscritte per intero: quelle tolte spariscono anche dal DB.
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

            await conn.ExecuteAsync("DELETE FROM ProxyLists WHERE ProxyId = @Id", new { p.Id }, tx);
            foreach (var entry in p.Lists.Values)
                await conn.ExecuteAsync(InsertEntrySql, EntryParams(p.Id, entry), tx);
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
            ["ConsecutiveFailures"] = p.ConsecutiveFailures,
            ["LastFailedUtc"] = p.LastFailedUtc?.ToString("o"),
        };
        if (includeId) dict["Id"] = p.Id;
        return dict;
    }

    private static object EntryParams(long proxyId, ProxyListEntry e) => new
    {
        ProxyId = proxyId,
        e.Provider,
        Status = (int)e.Status,
        e.LastResult,
        LastTestedUtc = e.LastTestedUtc?.ToUniversalTime().ToString("o"),
        e.LatencyMs,
        IsActive = e.IsActive ? 1 : 0,
        ManuallyDisabled = e.ManuallyDisabled ? 1 : 0,
    };

    public Task DeleteAsync(int id) => DeleteManyAsync(new[] { id });

    public async Task DeleteManyAsync(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0) return;
        using var conn = Open();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync("DELETE FROM ProxyLists WHERE ProxyId IN @Ids", new { Ids = ids }, tx);
        await conn.ExecuteAsync("DELETE FROM Proxies WHERE Id IN @Ids", new { Ids = ids }, tx);
        tx.Commit();
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
