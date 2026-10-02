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
                TotalBytes INTEGER NOT NULL DEFAULT 0,
                DownloadedBytes INTEGER NOT NULL DEFAULT 0,
                Status INTEGER NOT NULL DEFAULT 0,
                ErrorMessage TEXT NOT NULL DEFAULT '',
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

        _logger.LogInformation("Database inizializzato in {Path}", _dbPath);
    }

    public async Task<int> InsertAsync(DownloadItem item)
    {
        await using var conn = new SqliteConnection(_connectionString);
        var id = await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO Downloads (Url, DestinationPath, SeriesName, EpisodeNumber, TotalBytes, DownloadedBytes, Status, ErrorMessage)
            VALUES (@Url, @DestinationPath, @SeriesName, @EpisodeNumber, @TotalBytes, @DownloadedBytes, @Status, @ErrorMessage);
            SELECT last_insert_rowid();
        ", new
        {
            item.Url,
            item.DestinationPath,
            item.SeriesName,
            item.EpisodeNumber,
            item.TotalBytes,
            item.DownloadedBytes,
            Status = (int)item.Status,
            item.ErrorMessage
        });
        item.Id = id;
        return id;
    }

    public async Task UpdateAsync(DownloadItem item)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(@"
            UPDATE Downloads
            SET Url = @Url,
                DestinationPath = @DestinationPath,
                SeriesName = @SeriesName,
                EpisodeNumber = @EpisodeNumber,
                TotalBytes = @TotalBytes,
                DownloadedBytes = @DownloadedBytes,
                Status = @Status,
                ErrorMessage = @ErrorMessage
            WHERE Id = @Id;
        ", new
        {
            item.Id,
            item.Url,
            item.DestinationPath,
            item.SeriesName,
            item.EpisodeNumber,
            item.TotalBytes,
            item.DownloadedBytes,
            Status = (int)item.Status,
            item.ErrorMessage
        });
    }

    public async Task<List<DownloadItem>> GetUnfinishedAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);

        // Includiamo anche Cancelled e Failed SE hanno byte parziali e non sono completi.
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
            TotalBytes = r.TotalBytes,
            DownloadedBytes = r.DownloadedBytes,
            // Al riavvio consideriamo tutto come Pending, così riparte.
            Status = DownloadStatus.Pending,
            ErrorMessage = string.Empty
        }).ToList();
    }

    public async Task DeleteAsync(int id)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM Downloads WHERE Id = @Id;", new { Id = id });
    }

    // --- Chunks per il resume ---

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

    /// <summary>
    /// Classe interna per mappare le righe del DB.
    /// </summary>
    private class DownloadRow
    {
        public int Id { get; set; }
        public string Url { get; set; } = "";
        public string DestinationPath { get; set; } = "";
        public string SeriesName { get; set; } = "";
        public int EpisodeNumber { get; set; }
        public long TotalBytes { get; set; }
        public long DownloadedBytes { get; set; }
        public int Status { get; set; }
        public string ErrorMessage { get; set; } = "";
        public string CreatedAt { get; set; } = "";
    }
}