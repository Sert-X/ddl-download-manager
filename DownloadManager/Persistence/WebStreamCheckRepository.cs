using Dapper;
using DownloadManager.Services.Sftp;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence;

/// <summary>
/// Esiti della verifica "streaming web" dei video sui server SFTP (in downloads.db), così restano
/// dopo la chiusura dell'app. Un esito vale finché dimensione e data del file non cambiano.
/// </summary>
public class WebStreamCheckRepository
{
    private readonly ILogger<WebStreamCheckRepository> _logger;
    private string _connectionString;
    /// <summary>Le scritture vanno una alla volta (la verifica salva tre esiti insieme): niente "database occupato".</summary>
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public WebStreamCheckRepository(ILogger<WebStreamCheckRepository> logger)
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
            CREATE TABLE IF NOT EXISTS WebStreamChecks (
                ServerKey TEXT NOT NULL,
                FullPath  TEXT NOT NULL,
                Size      INTEGER NOT NULL,
                Modified  TEXT NOT NULL,
                Level     INTEGER NOT NULL,
                ShortText TEXT NOT NULL,
                Detail    TEXT NOT NULL,
                CheckedAt TEXT NOT NULL,
                PRIMARY KEY (ServerKey, FullPath)
            );");
        // Controllo lingua (regole versione 2): NULL = lingua mai controllata
        foreach (var column in new[]
                 {
                     "LangLevel INTEGER", "LangText TEXT", "LangDetail TEXT",
                     "RulesVersion INTEGER NOT NULL DEFAULT 1"
                 })
        {
            try { await conn.ExecuteAsync($"ALTER TABLE WebStreamChecks ADD COLUMN {column}"); } catch { /* già presente */ }
        }
    }

    private sealed class Row
    {
        public string FullPath { get; set; } = "";
        public long Size { get; set; }
        public string Modified { get; set; } = "";
        public long Level { get; set; }
        public string ShortText { get; set; } = "";
        public string Detail { get; set; } = "";
        public string CheckedAt { get; set; } = "";
        public long? LangLevel { get; set; }
        public string? LangText { get; set; }
        public string? LangDetail { get; set; }
        public long RulesVersion { get; set; }
    }

    private static WebStreamResult ToResult(Row r) => new(
        r.FullPath, (WebStreamLevel)r.Level, r.ShortText, r.Detail, r.Size,
        DateTime.TryParse(r.Modified, null, System.Globalization.DateTimeStyles.RoundtripKind, out var m) ? m : DateTime.MinValue,
        DateTime.TryParse(r.CheckedAt, out var c) ? c : DateTime.MinValue,
        (WebStreamLevel)(r.LangLevel ?? 0), r.LangText ?? "", r.LangDetail ?? "", (int)r.RulesVersion);

    public async Task SaveAsync(string serverKey, WebStreamResult r)
    {
        await WriteLock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.ExecuteAsync(@"INSERT OR REPLACE INTO WebStreamChecks
                    (ServerKey, FullPath, Size, Modified, Level, ShortText, Detail, CheckedAt,
                     LangLevel, LangText, LangDetail, RulesVersion)
                    VALUES (@serverKey, @FullPath, @Size, @modified, @level, @ShortText, @Detail, @at,
                     @langLevel, @LangText, @LangDetail, @RulesVersion)",
                new
                {
                    serverKey, r.FullPath, r.Size, r.ShortText, r.Detail, r.LangText, r.LangDetail, r.RulesVersion,
                    langLevel = r.LangLevel == WebStreamLevel.None ? (int?)null : (int)r.LangLevel,
                    modified = r.Modified.ToString("o"),
                    level = (int)r.Level,
                    at = r.CheckedAt.ToString("s")
                });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[WEB-STREAM] Salvataggio esito non riuscito per {Path}", r.FullPath); }
        finally { WriteLock.Release(); }
    }

    /// <summary>Tutti gli esiti sotto una cartella (anche nelle sottocartelle).</summary>
    public async Task<List<WebStreamResult>> GetUnderAsync(string serverKey, string folder)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            var prefix = EscapeLike(folder.TrimEnd('/')) + "/%";
            var rows = await conn.QueryAsync<Row>(
                @"SELECT FullPath, Size, Modified, Level, ShortText, Detail, CheckedAt,
                         LangLevel, LangText, LangDetail, RulesVersion FROM WebStreamChecks
                  WHERE ServerKey = @serverKey AND FullPath LIKE @prefix ESCAPE '\'",
                new { serverKey, prefix });
            return rows.Select(ToResult).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WEB-STREAM] Lettura esiti non riuscita per {Folder}", folder);
            return new List<WebStreamResult>();
        }
    }

    /// <summary>Dimensione segnata sugli esiti di file cambiati da fuori: l'esito non vale più ("da ricontrollare").</summary>
    public const long ChangedMarker = -1;

    /// <summary>Elimina gli esiti di questi file (percorsi esatti), tutti insieme.</summary>
    public async Task DeleteManyAsync(string serverKey, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        await WriteLock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync("DELETE FROM WebStreamChecks WHERE ServerKey = @serverKey AND FullPath = @p",
                paths.Select(p => new { serverKey, p }), tx);
            tx.Commit();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[WEB-STREAM] Eliminazione esiti non riuscita"); }
        finally { WriteLock.Release(); }
    }

    /// <summary>File cambiati da fuori (dimensione o data diverse): gli esiti restano ma "da ricontrollare".</summary>
    public async Task MarkChangedAsync(string serverKey, IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return;
        await WriteLock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = conn.BeginTransaction();
            await conn.ExecuteAsync("UPDATE WebStreamChecks SET Size = @marker WHERE ServerKey = @serverKey AND FullPath = @p",
                paths.Select(p => new { serverKey, p, marker = ChangedMarker }), tx);
            tx.Commit();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[WEB-STREAM] Aggiornamento esiti non riuscito"); }
        finally { WriteLock.Release(); }
    }

    /// <summary>Elimina l'esito di un file o di tutto quello che sta sotto una cartella.</summary>
    public async Task DeleteAsync(string serverKey, string path)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            var p = path.TrimEnd('/');
            await conn.ExecuteAsync(
                @"DELETE FROM WebStreamChecks WHERE ServerKey = @serverKey AND (FullPath = @p OR FullPath LIKE @prefix ESCAPE '\')",
                new { serverKey, p, prefix = EscapeLike(p) + "/%" });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[WEB-STREAM] Eliminazione esiti non riuscita per {Path}", path); }
    }

    /// <summary>Il file o la cartella ha cambiato percorso: gli esiti lo seguono.</summary>
    public async Task MoveAsync(string serverKey, string oldPath, string newPath)
    {
        try
        {
            var o = oldPath.TrimEnd('/');
            var n = newPath.TrimEnd('/');
            if (o == n) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = conn.BeginTransaction();
            // Ciò che c'era già alla destinazione viene sostituito
            await conn.ExecuteAsync(
                @"DELETE FROM WebStreamChecks WHERE ServerKey = @serverKey AND (FullPath = @n OR FullPath LIKE @nPrefix ESCAPE '\')",
                new { serverKey, n, nPrefix = EscapeLike(n) + "/%" }, tx);
            await conn.ExecuteAsync(
                @"UPDATE WebStreamChecks SET FullPath = @n || substr(FullPath, @len + 1)
                  WHERE ServerKey = @serverKey AND (FullPath = @o OR FullPath LIKE @oPrefix ESCAPE '\')",
                new { serverKey, o, n, len = o.Length, oPrefix = EscapeLike(o) + "/%" }, tx);
            tx.Commit();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[WEB-STREAM] Aggiornamento percorso non riuscito {Old} → {New}", oldPath, newPath); }
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
