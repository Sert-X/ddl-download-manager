using Dapper;
using DownloadManager.Services.Tmdb;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence;

/// <summary>
/// Memoria locale per i titoli TMDB (in downloads.db):
/// - codice TMDB e stagione letti dalla pagina di ogni post (la seconda analisi non rilegge il forum);
/// - numero di link di ogni post e quando è cresciuto l'ultima volta ("lo si sta aggiornando").
/// </summary>
public class TmdbCacheRepository
{
    private readonly ILogger<TmdbCacheRepository> _logger;
    private string _connectionString;

    public TmdbCacheRepository(ILogger<TmdbCacheRepository> logger)
    {
        _logger = logger;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDLDownloadManager");
        Directory.CreateDirectory(dir);
        _connectionString = $"Data Source={Path.Combine(dir, "downloads.db")}";
    }

    /// <summary>Solo per i test: un database diverso da quello dell'app.</summary>
    public void UseDatabase(string path) => _connectionString = $"Data Source={path}";

    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS TmdbPageInfo (
                PostId INTEGER PRIMARY KEY,
                Kind TEXT,
                TmdbId INTEGER,
                Season INTEGER,
                Source TEXT NOT NULL DEFAULT '',
                CheckedAt TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS PostLinkCounts (
                PostId INTEGER PRIMARY KEY,
                LinkCount INTEGER NOT NULL,
                GrewAt TEXT
            );
            CREATE TABLE IF NOT EXISTS TmdbRenames (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                BatchId TEXT NOT NULL,
                PostId INTEGER NOT NULL,
                OldTitle TEXT NOT NULL,
                NewTitle TEXT NOT NULL,
                Status TEXT NOT NULL,
                Note TEXT NOT NULL DEFAULT '',
                At TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_TmdbRenames_Batch ON TmdbRenames(BatchId);
            CREATE TABLE IF NOT EXISTS TmdbOverrides (
                PostId INTEGER PRIMARY KEY,
                ManualTitle TEXT,
                Kind TEXT,
                TmdbId INTEGER,
                Season INTEGER
            );
            CREATE TABLE IF NOT EXISTS TmdbThumbnails (
                PostId INTEGER PRIMARY KEY,
                SourceUrl TEXT NOT NULL,
                ForumUrl TEXT NOT NULL,
                At TEXT NOT NULL
            );");
        // Immagini del post prima della rinomina (solo se sono state cambiate anche loro): per l'annullamento
        try { await conn.ExecuteAsync("ALTER TABLE TmdbRenames ADD COLUMN OldImages TEXT"); } catch { /* già presente */ }
    }

    // ============================================================
    //  REGISTRO DELLE RINOMINE (per controllare, riprendere e annullare)
    // ============================================================

    public sealed class RenameEntry
    {
        public long Id { get; set; }
        public string BatchId { get; set; } = "";
        public long PostId { get; set; }
        public string OldTitle { get; set; } = "";
        public string NewTitle { get; set; } = "";
        /// <summary>Pending (inviata, da verificare), Done, Failed, Undone, UndoSkipped.</summary>
        public string Status { get; set; } = "";
        public string Note { get; set; } = "";
        public string At { get; set; } = "";
        /// <summary>Immagini di prima (JSON), null se la rinomina non le ha toccate.</summary>
        public string? OldImages { get; set; }
    }

    /// <summary>Annota una rinomina PRIMA dell'invio (Pending): se l'app si chiude a metà, si sa cosa verificare.</summary>
    public async Task<long> AddRenameAsync(string batchId, int postId, string oldTitle, string newTitle, string? oldImages = null)
    {
        await using var conn = new SqliteConnection(_connectionString);
        return await conn.ExecuteScalarAsync<long>(@"INSERT INTO TmdbRenames (BatchId, PostId, OldTitle, NewTitle, Status, At, OldImages)
                                                     VALUES (@batchId, @postId, @oldTitle, @newTitle, 'Pending', @at, @oldImages);
                                                     SELECT last_insert_rowid();",
            new { batchId, postId, oldTitle, newTitle, oldImages, at = DateTime.Now.ToString("s") });
    }

    public async Task SetRenameStatusAsync(long id, string status, string note = "")
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync("UPDATE TmdbRenames SET Status = @status, Note = @note WHERE Id = @id", new { id, status, note });
    }

    /// <summary>L'ultimo gruppo con almeno una rinomina riuscita e non ancora annullata (null se non c'è).</summary>
    public async Task<string?> GetLastUndoableBatchAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        return await conn.QueryFirstOrDefaultAsync<string>(
            // Gli annullamenti stessi ("annulla-...") non si annullano
            "SELECT BatchId FROM TmdbRenames WHERE Status = 'Done' AND BatchId NOT LIKE 'annulla-%' ORDER BY Id DESC LIMIT 1");
    }

    public async Task<List<RenameEntry>> GetBatchAsync(string batchId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        return (await conn.QueryAsync<RenameEntry>("SELECT * FROM TmdbRenames WHERE BatchId = @batchId ORDER BY Id", new { batchId })).ToList();
    }

    /// <summary>Rinomine inviate ma non verificate (app chiusa durante l'invio).</summary>
    public async Task<List<RenameEntry>> GetPendingRenamesAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        return (await conn.QueryAsync<RenameEntry>("SELECT * FROM TmdbRenames WHERE Status = 'Pending' ORDER BY Id")).ToList();
    }

    /// <summary>
    /// Miniatura caricata da TMDB: da quale copertina e con quale indirizzo l'ha salvata il forum.
    /// Serve a non ricaricarla ogni volta (il forum le dà un indirizzo suo, diverso da quello di TMDB).
    /// </summary>
    public async Task SaveThumbnailAsync(int postId, string sourceUrl, string forumUrl)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync("INSERT OR REPLACE INTO TmdbThumbnails (PostId, SourceUrl, ForumUrl, At) VALUES (@postId, @sourceUrl, @forumUrl, @at)",
            new { postId, sourceUrl, forumUrl, at = DateTime.Now.ToString("s") });
    }

    /// <summary>La miniatura del post è già quella caricata da questa copertina?</summary>
    public async Task<bool> HasThumbnailAsync(int postId, string sourceUrl, string forumUrl)
    {
        await using var conn = new SqliteConnection(_connectionString);
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM TmdbThumbnails WHERE PostId = @postId AND SourceUrl = @sourceUrl AND ForumUrl = @forumUrl",
            new { postId, sourceUrl, forumUrl }) > 0;
    }

    public async Task ForgetThumbnailAsync(int postId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM TmdbThumbnails WHERE PostId = @postId", new { postId });
    }

    // ============================================================
    //  SCELTE DELL'UTENTE (titolo scritto a mano, codice TMDB, stagione)
    // ============================================================

    public sealed class Override
    {
        public long PostId { get; set; }
        public string? ManualTitle { get; set; }
        public string? Kind { get; set; }
        public long? TmdbId { get; set; }
        public long? Season { get; set; }
        public bool HasMatch => TmdbId is > 0 && Kind != null;
        public bool IsEmpty => ManualTitle == null && TmdbId == null && Season == null;
    }

    public async Task<Dictionary<int, Override>> GetOverridesAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        return (await conn.QueryAsync<Override>("SELECT * FROM TmdbOverrides")).ToDictionary(o => (int)o.PostId);
    }

    public async Task SaveOverrideAsync(Override o)
    {
        await using var conn = new SqliteConnection(_connectionString);
        if (o.IsEmpty)
            await conn.ExecuteAsync("DELETE FROM TmdbOverrides WHERE PostId = @PostId", new { o.PostId });
        else
            await conn.ExecuteAsync(@"INSERT OR REPLACE INTO TmdbOverrides (PostId, ManualTitle, Kind, TmdbId, Season)
                                      VALUES (@PostId, @ManualTitle, @Kind, @TmdbId, @Season)", o);
    }

    private sealed class PageRow
    {
        public long PostId { get; set; }
        public string? Kind { get; set; }
        public long? TmdbId { get; set; }
        public long? Season { get; set; }
        public string Source { get; set; } = "";
    }

    /// <summary>Codice letto in precedenza dalla pagina del post (null = mai letto).</summary>
    public async Task<PostPageInfo?> GetPageInfoAsync(int postId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        var r = await conn.QueryFirstOrDefaultAsync<PageRow>("SELECT * FROM TmdbPageInfo WHERE PostId = @postId", new { postId });
        return r == null ? null : new PostPageInfo(r.Kind, (int?)r.TmdbId, (int?)r.Season, null, r.Source);
    }

    public async Task SavePageInfoAsync(int postId, PostPageInfo info)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(@"INSERT OR REPLACE INTO TmdbPageInfo (PostId, Kind, TmdbId, Season, Source, CheckedAt)
                                  VALUES (@postId, @Kind, @TmdbId, @Season, @Source, @at)",
            new { postId, info.Kind, info.TmdbId, info.Season, info.Source, at = DateTime.Now.ToString("s") });
    }

    /// <summary>Dimentica il codice letto (es. "Rianalizza": si rilegge la pagina).</summary>
    public async Task ForgetPageInfoAsync(IEnumerable<int> postIds)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM TmdbPageInfo WHERE PostId = @id", postIds.Select(id => new { id }));
    }

    /// <summary>
    /// Registra il numero di link di un post e dice se è cresciuto negli ultimi giorni indicati
    /// (lo si sta aggiornando). Il primo conteggio non conta come crescita.
    /// </summary>
    public async Task<bool> TrackLinkCountAsync(int postId, int count, int days = 30)
    {
        await using var conn = new SqliteConnection(_connectionString);
        var row = await conn.QueryFirstOrDefaultAsync<(long LinkCount, string? GrewAt)>(
            "SELECT LinkCount, GrewAt FROM PostLinkCounts WHERE PostId = @postId", new { postId });
        string? grewAt = row == default ? null : row.GrewAt;
        if (row == default)
            await conn.ExecuteAsync("INSERT INTO PostLinkCounts (PostId, LinkCount, GrewAt) VALUES (@postId, @count, NULL)", new { postId, count });
        else if (count != row.LinkCount)
        {
            if (count > row.LinkCount) grewAt = DateTime.Now.ToString("s");
            await conn.ExecuteAsync("UPDATE PostLinkCounts SET LinkCount = @count, GrewAt = @grewAt WHERE PostId = @postId", new { postId, count, grewAt });
        }
        return grewAt != null && DateTime.TryParse(grewAt, out var g) && DateTime.Now - g < TimeSpan.FromDays(days);
    }
}
