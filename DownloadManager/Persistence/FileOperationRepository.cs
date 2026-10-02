using System.Globalization;
using Dapper;
using DownloadManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence;

public class FileOperationRepository
{
    private readonly ILogger<FileOperationRepository> _logger;
    private readonly string _connectionString;

    public FileOperationRepository(ILogger<FileOperationRepository> logger)
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
            CREATE TABLE IF NOT EXISTS FileOperations (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Type INTEGER NOT NULL,
                OriginalPath TEXT NOT NULL,
                NewPath TEXT NOT NULL,
                Timestamp TEXT NOT NULL,
                BatchId TEXT NOT NULL,
                IsUndone INTEGER NOT NULL DEFAULT 0,
                IsRemote INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS IX_FileOperations_Timestamp
                ON FileOperations(Timestamp DESC);

            CREATE INDEX IF NOT EXISTS IX_FileOperations_BatchId
                ON FileOperations(BatchId);
        ");

        // Migrazione per DB esistenti senza IsRemote.
        try
        {
            await conn.ExecuteAsync(
                "ALTER TABLE FileOperations ADD COLUMN IsRemote INTEGER NOT NULL DEFAULT 0;");
        }
        catch { /* colonna già presente */ }

        _logger.LogInformation("Tabella FileOperations pronta");
    }

    public async Task<int> InsertAsync(FileOperation op)
    {
        await using var conn = new SqliteConnection(_connectionString);
        var id = await conn.ExecuteScalarAsync<int>(@"
            INSERT INTO FileOperations (Type, OriginalPath, NewPath, Timestamp, BatchId, IsUndone, IsRemote)
            VALUES (@Type, @OriginalPath, @NewPath, @Timestamp, @BatchId, @IsUndone, @IsRemote);
            SELECT last_insert_rowid();
        ", new
        {
            Type = (int)op.Type,
            op.OriginalPath,
            op.NewPath,
            Timestamp = op.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            op.BatchId,
            IsUndone = op.IsUndone ? 1 : 0,
            IsRemote = op.IsRemote ? 1 : 0
        });

        op.Id = id;
        return id;
    }

    public async Task InsertManyAsync(IEnumerable<FileOperation> ops)
    {
        var list = ops.ToList();
        if (list.Count == 0) return;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        foreach (var op in list)
        {
            var id = await conn.ExecuteScalarAsync<int>(@"
                INSERT INTO FileOperations (Type, OriginalPath, NewPath, Timestamp, BatchId, IsUndone, IsRemote)
                VALUES (@Type, @OriginalPath, @NewPath, @Timestamp, @BatchId, @IsUndone, @IsRemote);
                SELECT last_insert_rowid();
            ", new
            {
                Type = (int)op.Type,
                op.OriginalPath,
                op.NewPath,
                Timestamp = op.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                op.BatchId,
                IsUndone = op.IsUndone ? 1 : 0,
                IsRemote = op.IsRemote ? 1 : 0
            }, tx);

            op.Id = id;
        }

        await tx.CommitAsync();
    }

    public async Task MarkUndoneAsync(int id)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE FileOperations SET IsUndone = 1 WHERE Id = @Id;",
            new { Id = id });
    }

    public async Task MarkBatchUndoneAsync(string batchId)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync(
            "UPDATE FileOperations SET IsUndone = 1 WHERE BatchId = @BatchId;",
            new { BatchId = batchId });
    }

    public async Task<List<FileOperation>> GetRecentOperationsAsync(int limit = 100)
    {
        await using var conn = new SqliteConnection(_connectionString);
        var rows = await conn.QueryAsync<FileOperationRow>(@"
            SELECT * FROM FileOperations
            WHERE IsUndone = 0
            ORDER BY Id DESC
            LIMIT @Limit;
        ", new { Limit = limit });

        return rows.Select(ToOperation).ToList();
    }

    /// <summary>
    /// Ritorna l'ultimo batch non annullato, filtrando per IsRemote.
    /// </summary>
    public async Task<List<FileOperation>> GetLastBatchAsync(bool isRemote)
    {
        await using var conn = new SqliteConnection(_connectionString);

        var lastBatchId = await conn.ExecuteScalarAsync<string?>(@"
            SELECT BatchId FROM FileOperations
            WHERE IsUndone = 0 AND IsRemote = @IsRemote
            ORDER BY Id DESC
            LIMIT 1;
        ", new { IsRemote = isRemote ? 1 : 0 });

        if (string.IsNullOrEmpty(lastBatchId))
            return new List<FileOperation>();

        var rows = await conn.QueryAsync<FileOperationRow>(@"
            SELECT * FROM FileOperations
            WHERE BatchId = @BatchId AND IsUndone = 0
            ORDER BY Id DESC;
        ", new { BatchId = lastBatchId });

        return rows.Select(ToOperation).ToList();
    }

    public async Task ClearHistoryAsync()
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.ExecuteAsync("DELETE FROM FileOperations;");
    }

    private static FileOperation ToOperation(FileOperationRow r) => new()
    {
        Id = r.Id,
        Type = (FileOperationType)r.Type,
        OriginalPath = r.OriginalPath,
        NewPath = r.NewPath,
        Timestamp = DateTime.Parse(r.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        BatchId = r.BatchId,
        IsUndone = r.IsUndone == 1,
        IsRemote = r.IsRemote == 1
    };

    private class FileOperationRow
    {
        public int Id { get; set; }
        public int Type { get; set; }
        public string OriginalPath { get; set; } = string.Empty;
        public string NewPath { get; set; } = string.Empty;
        public string Timestamp { get; set; } = string.Empty;
        public string BatchId { get; set; } = string.Empty;
        public int IsUndone { get; set; }
        public int IsRemote { get; set; }
    }
}