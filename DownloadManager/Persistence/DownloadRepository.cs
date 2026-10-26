using Dapper;
using DownloadManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence;

public class DownloadRepository
{
    private readonly ILogger<DownloadRepository> _logger;
    private readonly string _connectionString;
    private readonly string _dbPath;

    public DownloadRepository(ILogger<DownloadRepository> logger)
    {
        _logger = logger;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DDLDownloadManager");
        Directory.CreateDirectory(appDataDir);

        _dbPath = Path.Combine(appDataDir, "downloads.db");
        _connectionString = $"Data Source={_dbPath}";
    }

    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await conn.ExecuteAsync(@"
            CREATE TABLE IF NOT EXISTS Downloads (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Url TEXT NOT NULL,
                DestinationPath TEXT NOT NULL,
                SeriesName TEXT NOT NULL DEFAULT '',
                EpisodeNumber INTEGER NOT NULL DEFAULT 0,
                SeasonNumber INTEGER NOT NULL DEFAULT 0,
                TotalBytes INTEGER NOT NULL DEFAULT 0,
                DownloadedBytes INTEGER NOT NULL DEFAULT 0,
                Status INTEGER NOT NULL DEFAULT 0,
                ErrorMessage TEXT NOT NULL DEFAULT '',
                Category TEXT NOT NULL DEFAULT 'anime',
                CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS DownloadChunks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                DownloadId INTEGER NOT NULL,
                StartByte INTEGER NOT NULL,
                EndByte INTEGER NOT NULL,
                FOREIGN KEY (DownloadId) REFERENCES Downloads(Id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS IX_DownloadChunks_DownloadId
                ON DownloadChunks(DownloadId);
        ");

        try
        {
            var cols = (await conn.QueryAsync<string>(
                "SELECT name FROM pragma_table_info('Downloads')")).ToList();

            if (!cols.Contains("Category", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN Category TEXT NOT NULL DEFAULT 'anime'");

            if (!cols.Contains("SeasonNumber", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN SeasonNumber INTEGER NOT NULL DEFAULT 0");

            if (!cols.Contains("EpisodeTitle", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN EpisodeTitle TEXT NOT NULL DEFAULT ''");

            if (!cols.Contains("ProviderKey", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN ProviderKey TEXT NOT NULL DEFAULT ''");

            if (!cols.Contains("QualityName", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN QualityName TEXT NOT NULL DEFAULT ''");

            if (!cols.Contains("ContentLink", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN ContentLink TEXT NOT NULL DEFAULT ''");

            if (!cols.Contains("EpisodeId", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN EpisodeId INTEGER NOT NULL DEFAULT 0");

            if (!cols.Contains("IsSeries", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN IsSeries INTEGER NOT NULL DEFAULT 0");

            if (!cols.Contains("AudioUrl", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN AudioUrl TEXT NOT NULL DEFAULT ''");

            if (!cols.Contains("AudioLanguages", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN AudioLanguages TEXT NOT NULL DEFAULT ''");

            if (!cols.Contains("SubtitleLanguages", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN SubtitleLanguages TEXT NOT NULL DEFAULT ''");

            if (!cols.Contains("BurnSubtitles", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN BurnSubtitles INTEGER NOT NULL DEFAULT 0");

            // Download rimasti in coda dalle versioni precedenti: "Copia in MP4" (4), come facevano prima
            if (!cols.Contains("Conversion", StringComparer.OrdinalIgnoreCase))
                await conn.ExecuteAsync("ALTER TABLE Downloads ADD COLUMN Conversion INTEGER NOT NULL DEFAULT 4");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Migrazione colonne fallita (DB forse nuovo)");
        }

        _logger.LogInformation("Database inizializzato in {Path}", _dbPath);
    }

    // ============================================================
    //  INSERT singolo
    // ============================================================

    public async Task<int> InsertAsync(DownloadItem item)
    {
        await using var conn = new SqliteConnection(_connectionString);
        var id = await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Downloads
                (Url, DestinationPath, SeriesName, EpisodeNumber, SeasonNumber,
                 TotalBytes, DownloadedBytes, Status, ErrorMessage, Category,
                 EpisodeTitle, ProviderKey, QualityName, ContentLink, EpisodeId, IsSeries, AudioUrl,
                 AudioLanguages, SubtitleLanguages, BurnSubtitles, Conversion)
            VALUES
                (@Url, @DestinationPath, @SeriesName, @EpisodeNumber, @SeasonNumber,
                 @TotalBytes, @DownloadedBytes, @Status, @ErrorMessage, @Category,
                 @EpisodeTitle, @ProviderKey, @QualityName, @ContentLink, @EpisodeId, @IsSeries, @AudioUrl,
                 @AudioLanguages, @SubtitleLanguages, @BurnSubtitles, @Conversion);
            SELECT last_insert_rowid();
        ", new
        {
            item.Url,
            item.DestinationPath,
            item.SeriesName,
            item.EpisodeNumber,
            item.SeasonNumber,
            item.TotalBytes,
            item.DownloadedBytes,
            Status = (int)item.Status,
            item.ErrorMessage,
            item.Category,
            item.EpisodeTitle,
            item.ProviderKey,
            item.QualityName,
            item.ContentLink,
            item.EpisodeId,
            IsSeries = item.IsSeries ? 1 : 0,
            item.AudioUrl,
            item.AudioLanguages,
            item.SubtitleLanguages,
            BurnSubtitles = item.BurnSubtitles ? 1 : 0,
            Conversion = (int)item.Conversion
        });
        item.Id = id;
        return id;
    }

    // ============================================================
    //  INSERT multiplo — una sola connessione, una sola transazione
    //  (10-15x più veloce per batch di 20+ item)
    // ============================================================

    public async Task InsertManyAsync(IReadOnlyList<DownloadItem> items)
    {
        if (items == null || items.Count == 0) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var tx = await conn.BeginTransactionAsync();

        foreach (var item in items)
        {
            var id = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO Downloads
                    (Url, DestinationPath, SeriesName, EpisodeNumber, SeasonNumber,
                     TotalBytes, DownloadedBytes, Status, ErrorMessage, Category,
                     EpisodeTitle, ProviderKey, QualityName, ContentLink, EpisodeId, IsSeries, AudioUrl,
                 AudioLanguages, SubtitleLanguages, BurnSubtitles, Conversion)
                VALUES
                    (@Url, @DestinationPath, @SeriesName, @EpisodeNumber, @SeasonNumber,
                     @TotalBytes, @DownloadedBytes, @Status, @ErrorMessage, @Category,
                     @EpisodeTitle, @ProviderKey, @QualityName, @ContentLink, @EpisodeId, @IsSeries, @AudioUrl,
                 @AudioLanguages, @SubtitleLanguages, @BurnSubtitles, @Conversion);
                SELECT last_insert_rowid();
            ", new
            {
                item.Url,
                item.DestinationPath,
                item.SeriesName,
                item.EpisodeNumber,
                item.SeasonNumber,
                item.TotalBytes,
                item.DownloadedBytes,
                Status = (int)item.Status,
                item.ErrorMessage,
                item.Category,
                item.EpisodeTitle,
                item.ProviderKey,
                item.QualityName,
                item.ContentLink,
                item.EpisodeId,
                IsSeries = item.IsSeries ? 1 : 0,
                item.AudioUrl,
                item.AudioLanguages,
                item.SubtitleLanguages,
                BurnSubtitles = item.BurnSubtitles ? 1 : 0,
                Conversion = (int)item.Conversion
            }, transaction: tx);

            item.Id = id;
        }

        await tx.CommitAsync();

        _logger.LogInformation("[DB] InsertMany: {Count} item in una transazione", items.Count);
    }

    // ============================================================
    //  UPDATE
    // ============================================================

    public async Task UpdateAsync(DownloadItem item)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(@"
            UPDATE Downloads
            SET Url = @Url,
                DestinationPath = @DestinationPath,
                SeriesName = @SeriesName,
                EpisodeNumber = @EpisodeNumber,
                SeasonNumber = @SeasonNumber,
                TotalBytes = @TotalBytes,
                DownloadedBytes = @DownloadedBytes,
                Status = @Status,
                ErrorMessage = @ErrorMessage,
                Category = @Category,
                EpisodeTitle = @EpisodeTitle,
                ProviderKey = @ProviderKey,
                QualityName = @QualityName,
                ContentLink = @ContentLink,
                EpisodeId = @EpisodeId,
                IsSeries = @IsSeries,
                AudioUrl = @AudioUrl,
                AudioLanguages = @AudioLanguages,
                SubtitleLanguages = @SubtitleLanguages,
                BurnSubtitles = @BurnSubtitles,
                Conversion = @Conversion
            WHERE Id = @Id;
        ", new
        {
            item.Id,
            item.Url,
            item.DestinationPath,
            item.SeriesName,
            item.EpisodeNumber,
            item.SeasonNumber,
            item.TotalBytes,
            item.DownloadedBytes,
            Status = (int)item.Status,
            item.ErrorMessage,
            item.Category,
            item.EpisodeTitle,
            item.ProviderKey,
            item.QualityName,
            item.ContentLink,
            item.EpisodeId,
            IsSeries = item.IsSeries ? 1 : 0,
            item.AudioUrl,
            item.AudioLanguages,
            item.SubtitleLanguages,
            BurnSubtitles = item.BurnSubtitles ? 1 : 0,
            Conversion = (int)item.Conversion
        });
    }

    public async Task<List<DownloadItem>> GetUnfinishedAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);

        var rows = await conn.QueryAsync<DownloadRow>(@"
            SELECT * FROM Downloads
            WHERE Status IN (@Pending, @Downloading, @Paused)
            OR (
                    Status IN (@Cancelled, @Failed)
                    AND TotalBytes > 0
                    AND DownloadedBytes < TotalBytes
                )
            ORDER BY Id ASC;
        ", new
        {
            Pending     = (int)DownloadStatus.Pending,
            Downloading = (int)DownloadStatus.Downloading,
            Paused      = (int)DownloadStatus.Paused,
            Cancelled   = (int)DownloadStatus.Cancelled,
            Failed      = (int)DownloadStatus.Failed
        });

        return rows.Select(r => new DownloadItem
        {
            Id = r.Id,
            Url = r.Url,
            DestinationPath = r.DestinationPath,
            SeriesName = r.SeriesName,
            EpisodeNumber = r.EpisodeNumber,
            SeasonNumber = r.SeasonNumber,
            TotalBytes = r.TotalBytes,
            DownloadedBytes = r.DownloadedBytes,
            Status = DownloadStatus.Pending,
            ErrorMessage = string.Empty,
            Category = string.IsNullOrWhiteSpace(r.Category) ? "anime" : r.Category,
            EpisodeTitle = r.EpisodeTitle ?? "",
            ProviderKey = r.ProviderKey ?? "",
            QualityName = r.QualityName ?? "",
            ContentLink = r.ContentLink ?? "",
            EpisodeId = r.EpisodeId,
            IsSeries = r.IsSeries != 0,
            AudioUrl = r.AudioUrl ?? "",
            AudioLanguages = r.AudioLanguages ?? "",
            SubtitleLanguages = r.SubtitleLanguages ?? "",
            BurnSubtitles = r.BurnSubtitles != 0,
            Conversion = (VideoConversion)r.Conversion
        }).ToList();
    }

    public async Task DeleteAsync(int id)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM Downloads WHERE Id = @Id;", new { Id = id });
    }

    /// <summary>Elimina più download con una sola query (invece di una connessione per riga).</summary>
    public async Task DeleteManyAsync(IReadOnlyCollection<int> ids)
    {
        if (ids.Count == 0) return;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM Downloads WHERE Id IN @Ids;", new { Ids = ids });
    }

    public async Task SaveChunkAsync(int downloadId, long start, long end)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(@"
            INSERT INTO DownloadChunks (DownloadId, StartByte, EndByte)
            VALUES (@DownloadId, @Start, @End);
        ", new { DownloadId = downloadId, Start = start, End = end });
    }

    public async Task<List<(long Start, long End)>> GetChunksAsync(int downloadId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        var rows = await conn.QueryAsync<(long StartByte, long EndByte)>(@"
            SELECT StartByte, EndByte FROM DownloadChunks
            WHERE DownloadId = @DownloadId
            ORDER BY StartByte;
        ", new { DownloadId = downloadId });

        return rows.Select(r => (r.StartByte, r.EndByte)).ToList();
    }

    public async Task ClearChunksAsync(int downloadId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(
            "DELETE FROM DownloadChunks WHERE DownloadId = @DownloadId;",
            new { DownloadId = downloadId });
    }

    private class DownloadRow
    {
        public int Id { get; set; }
        public string Url { get; set; } = "";
        public string DestinationPath { get; set; } = "";
        public string SeriesName { get; set; } = "";
        public int EpisodeNumber { get; set; }
        public int SeasonNumber { get; set; }
        public long TotalBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public int Status { get; set; }
        public string ErrorMessage { get; set; } = "";
        public string Category { get; set; } = "anime";
        public string CreatedAt { get; set; } = "";
        public string EpisodeTitle { get; set; } = "";
        public string ProviderKey { get; set; } = "";
        public string QualityName { get; set; } = "";
        public string ContentLink { get; set; } = "";
        public int EpisodeId { get; set; }
        public int IsSeries { get; set; }
        public string AudioUrl { get; set; } = "";
        public string? AudioLanguages { get; set; }
        public string? SubtitleLanguages { get; set; }
        public int BurnSubtitles { get; set; }
        public int Conversion { get; set; }
    }
}