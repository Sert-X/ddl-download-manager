using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence;

/// <summary>
/// Risultati del check dei file vuoti (0 byte) per server, in downloads.db: restano dopo la
/// chiusura dell'app. Si salva il minimo da cui ricostruire tutto il resto:
/// - i file a 0 byte trovati;
/// - le cartelle controllate (con il riassunto della colonna Contenuto);
/// - le cartelle in cui è stato lanciato un check (lì le cartelle nuove si controllano da sole).
/// Una cartella è "rossa" se contiene, anche in profondità, un file a 0 byte.
/// </summary>
public class ZeroByteCheckRepository
{
    private readonly ILogger<ZeroByteCheckRepository> _logger;
    private string _connectionString;
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public ZeroByteCheckRepository(ILogger<ZeroByteCheckRepository> logger)
    {
        _logger = logger;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDLDownloadManager");
        Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={Path.Combine(dir, "downloads.db")};Default Timeout=30";
    }

    /// <summary>Solo per i test: un database diverso da quello dell'app.</summary>
    public void UseDatabase(string path) => _connectionString = $"Data Source={path}";

    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS ZeroByteFiles (
                ServerKey TEXT NOT NULL,
                FullPath  TEXT NOT NULL,
                PRIMARY KEY (ServerKey, FullPath)
            );
            CREATE TABLE IF NOT EXISTS ZeroByteFolders (
                ServerKey    TEXT NOT NULL,
                FullPath     TEXT NOT NULL,
                ContentsInfo TEXT NOT NULL DEFAULT '',
                CheckedAt    TEXT NOT NULL,
                PRIMARY KEY (ServerKey, FullPath)
            );
            CREATE TABLE IF NOT EXISTS ZeroByteCheckedParents (
                ServerKey TEXT NOT NULL,
                FullPath  TEXT NOT NULL,
                PRIMARY KEY (ServerKey, FullPath)
            );");
    }

    public sealed record Snapshot(List<string> ZeroFiles, List<(string Path, string Contents)> Folders, List<string> CheckedParents);

    private sealed class FolderRow
    {
        public string FullPath { get; set; } = "";
        public string ContentsInfo { get; set; } = "";
    }

    /// <summary>Tutto ciò che si sa di un server.</summary>
    public async Task<Snapshot> LoadAsync(string serverKey)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            var files = (await conn.QueryAsync<string>("SELECT FullPath FROM ZeroByteFiles WHERE ServerKey = @serverKey", new { serverKey })).ToList();
            var folders = (await conn.QueryAsync<FolderRow>("SELECT FullPath, ContentsInfo FROM ZeroByteFolders WHERE ServerKey = @serverKey", new { serverKey }))
                .Select(r => (r.FullPath, r.ContentsInfo)).ToList();
            var parents = (await conn.QueryAsync<string>("SELECT FullPath FROM ZeroByteCheckedParents WHERE ServerKey = @serverKey", new { serverKey })).ToList();
            return new Snapshot(files, folders, parents);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ZERO-BYTE] Lettura dei risultati salvati non riuscita");
            return new Snapshot(new(), new(), new());
        }
    }

    /// <summary>File a 0 byte salvati sotto una cartella (anche in profondità).</summary>
    public async Task<List<string>> GetZeroFilesUnderAsync(string serverKey, string folder)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            return (await conn.QueryAsync<string>(
                @"SELECT FullPath FROM ZeroByteFiles WHERE ServerKey = @serverKey AND FullPath LIKE @prefix ESCAPE '\'",
                new { serverKey, prefix = Prefix(folder) + "/%" })).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ZERO-BYTE] Lettura non riuscita per {Folder}", folder);
            return new List<string>();
        }
    }

    /// <summary>
    /// Risultato di un check su una cartella: i suoi file a 0 byte (tutti, anche nelle sottocartelle
    /// lette) sostituiscono quelli salvati per le cartelle lette; le cartelle lette risultano controllate.
    /// </summary>
    public Task SaveCheckAsync(string serverKey, IReadOnlyCollection<string> scannedFolders,
        IReadOnlyCollection<string> zeroFiles, string topFolder, string topContents, string checkedParent)
        => WriteAsync(async (conn, tx) =>
        {
            var at = DateTime.Now.ToString("s");
            foreach (var folder in scannedFolders)
            {
                // I file a 0 byte direttamente in questa cartella si riscrivono
                await conn.ExecuteAsync(
                    @"DELETE FROM ZeroByteFiles WHERE ServerKey = @serverKey AND FullPath LIKE @child ESCAPE '\' AND FullPath NOT LIKE @grandchild ESCAPE '\'",
                    new { serverKey, child = Prefix(folder) + "/%", grandchild = Prefix(folder) + "/%/%" }, tx);
                await conn.ExecuteAsync(
                    @"INSERT INTO ZeroByteFolders (ServerKey, FullPath, ContentsInfo, CheckedAt) VALUES (@serverKey, @folder, '', @at)
                      ON CONFLICT(ServerKey, FullPath) DO UPDATE SET CheckedAt = excluded.CheckedAt",
                    new { serverKey, folder = Norm(folder), at }, tx);
            }
            await conn.ExecuteAsync("UPDATE ZeroByteFolders SET ContentsInfo = @topContents WHERE ServerKey = @serverKey AND FullPath = @top",
                new { serverKey, topContents, top = Norm(topFolder) }, tx);
            foreach (var f in zeroFiles)
                await conn.ExecuteAsync("INSERT OR IGNORE INTO ZeroByteFiles (ServerKey, FullPath) VALUES (@serverKey, @f)", new { serverKey, f }, tx);
            await conn.ExecuteAsync("INSERT OR IGNORE INTO ZeroByteCheckedParents (ServerKey, FullPath) VALUES (@serverKey, @p)",
                new { serverKey, p = Norm(checkedParent) }, tx);
        });

    /// <summary>
    /// La cartella è appena stata letta: i suoi file a 0 byte diretti si riscrivono, e tutto ciò che
    /// stava sotto sottocartelle che non ci sono più si dimentica.
    /// </summary>
    public Task SyncListingAsync(string serverKey, string folder, IReadOnlyCollection<string> zeroFileNames,
        IReadOnlyCollection<string> subfolderNames)
        => WriteAsync(async (conn, tx) =>
        {
            var key = Norm(folder);
            var prefix = Prefix(folder);
            await conn.ExecuteAsync(
                @"DELETE FROM ZeroByteFiles WHERE ServerKey = @serverKey AND FullPath LIKE @child ESCAPE '\' AND FullPath NOT LIKE @grandchild ESCAPE '\'",
                new { serverKey, child = prefix + "/%", grandchild = prefix + "/%/%" }, tx);
            foreach (var name in zeroFileNames)
                await conn.ExecuteAsync("INSERT OR IGNORE INTO ZeroByteFiles (ServerKey, FullPath) VALUES (@serverKey, @f)",
                    new { serverKey, f = key + "/" + name }, tx);

            // Sottocartelle sparite (eliminate o rinominate, anche da fuori)
            var subs = subfolderNames.ToHashSet(StringComparer.Ordinal);
            foreach (var table in new[] { "ZeroByteFiles", "ZeroByteFolders", "ZeroByteCheckedParents" })
            {
                var below = (await conn.QueryAsync<string>(
                    $@"SELECT FullPath FROM {table} WHERE ServerKey = @serverKey AND FullPath LIKE @deeper ESCAPE '\'",
                    new { serverKey, deeper = prefix + "/%" }, tx)).ToList();
                foreach (var path in below)
                {
                    var child = path.Substring(key.Length + 1).Split('/')[0];
                    if (!subs.Contains(child))
                        await conn.ExecuteAsync($"DELETE FROM {table} WHERE ServerKey = @serverKey AND FullPath = @path", new { serverKey, path }, tx);
                }
            }
        });

    /// <summary>Allineamento con l'elenco completo dei file sotto una cartella (dal server).</summary>
    public Task ReplaceZeroFilesUnderAsync(string serverKey, string folder, IReadOnlyCollection<string> zeroFiles)
        => WriteAsync(async (conn, tx) =>
        {
            await conn.ExecuteAsync(@"DELETE FROM ZeroByteFiles WHERE ServerKey = @serverKey AND FullPath LIKE @deeper ESCAPE '\'",
                new { serverKey, deeper = Prefix(folder) + "/%" }, tx);
            foreach (var f in zeroFiles)
                await conn.ExecuteAsync("INSERT OR IGNORE INTO ZeroByteFiles (ServerKey, FullPath) VALUES (@serverKey, @f)", new { serverKey, f }, tx);
        });

    /// <summary>Riassunti della colonna Contenuto ricalcolati dall'elenco completo (solo cartelle già controllate).</summary>
    public Task UpdateContentsAsync(string serverKey, IReadOnlyDictionary<string, string> contentsByFolder)
        => WriteAsync(async (conn, tx) =>
        {
            foreach (var (folder, contents) in contentsByFolder)
                await conn.ExecuteAsync("UPDATE ZeroByteFolders SET ContentsInfo = @contents WHERE ServerKey = @serverKey AND FullPath = @folder",
                    new { serverKey, folder = Norm(folder), contents }, tx);
        });

    /// <summary>Cartelle controllate sotto una cartella (anche in profondità), compresa lei.</summary>
    public async Task<List<string>> GetCheckedFoldersUnderAsync(string serverKey, string folder)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            return (await conn.QueryAsync<string>(
                @"SELECT FullPath FROM ZeroByteFolders WHERE ServerKey = @serverKey AND (FullPath = @f OR FullPath LIKE @prefix ESCAPE '\')",
                new { serverKey, f = Norm(folder), prefix = Prefix(folder) + "/%" })).ToList();
        }
        catch { return new List<string>(); }
    }

    public Task RemoveFileAsync(string serverKey, string path)
        => WriteAsync((conn, tx) => conn.ExecuteAsync("DELETE FROM ZeroByteFiles WHERE ServerKey = @serverKey AND FullPath = @path",
            new { serverKey, path = Norm(path) }, tx));

    /// <summary>Elemento eliminato: si dimentica lui e tutto ciò che conteneva.</summary>
    public Task DeleteAsync(string serverKey, string path)
        => WriteAsync(async (conn, tx) =>
        {
            var p = Norm(path);
            foreach (var table in new[] { "ZeroByteFiles", "ZeroByteFolders", "ZeroByteCheckedParents" })
                await conn.ExecuteAsync($@"DELETE FROM {table} WHERE ServerKey = @serverKey AND (FullPath = @p OR FullPath LIKE @deeper ESCAPE '\')",
                    new { serverKey, p, deeper = Prefix(p) + "/%" }, tx);
        });

    /// <summary>Elemento rinominato o spostato: i risultati lo seguono.</summary>
    public Task MoveAsync(string serverKey, string oldPath, string newPath)
        => WriteAsync(async (conn, tx) =>
        {
            var o = Norm(oldPath);
            var n = Norm(newPath);
            if (o == n) return;
            foreach (var table in new[] { "ZeroByteFiles", "ZeroByteFolders", "ZeroByteCheckedParents" })
            {
                await conn.ExecuteAsync($@"DELETE FROM {table} WHERE ServerKey = @serverKey AND (FullPath = @n OR FullPath LIKE @nd ESCAPE '\')",
                    new { serverKey, n, nd = Prefix(n) + "/%" }, tx);
                await conn.ExecuteAsync($@"UPDATE {table} SET FullPath = @n || substr(FullPath, @len + 1)
                                           WHERE ServerKey = @serverKey AND (FullPath = @o OR FullPath LIKE @od ESCAPE '\')",
                    new { serverKey, o, n, len = o.Length, od = Prefix(o) + "/%" }, tx);
            }
        });

    private async Task WriteAsync(Func<SqliteConnection, SqliteTransaction, Task> work)
    {
        await WriteLock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = conn.BeginTransaction();
            await work(conn, tx);
            tx.Commit();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[ZERO-BYTE] Salvataggio non riuscito"); }
        finally { WriteLock.Release(); }
    }

    private static string Norm(string path) { var p = (path ?? "").TrimEnd('/'); return p; }
    /// <summary>Prefisso LIKE di una cartella (i figli sono prefix + "/%"): "/" diventa "".</summary>
    private static string Prefix(string folder) => EscapeLike(Norm(folder));
    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
