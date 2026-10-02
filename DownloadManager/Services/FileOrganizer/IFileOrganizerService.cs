using DownloadManager.Models;

namespace DownloadManager.Services.FileOrganizer;

public enum SplitNumberingMode
{
    /// <summary>Reset per ogni sottocartella: 1-20, 1-20, 1-20...</summary>
    Reset,

    /// <summary>Continua tra le sottocartelle: 1-20, 21-40, 41-60...</summary>
    Continue
}

/// <summary>
/// Una singola operazione proposta (anteprima).
/// </summary>
public class ProposedOperation
{
    public FileOperationType Type { get; set; }
    public string OriginalPath { get; set; } = string.Empty;
    public string NewPath { get; set; } = string.Empty;

    public bool HasConflict { get; set; }
    public string ConflictReason { get; set; } = string.Empty;

    public bool IsDuplicate { get; set; }
    public string SourceFolder { get; set; } = string.Empty;

    public string DisplayText => Type switch
    {
        FileOperationType.Move => $"Sposta: {Path.GetFileName(OriginalPath)} → {GetRelativeNewPath()}",
        FileOperationType.Rename => $"Rinomina: {Path.GetFileName(OriginalPath)} → {Path.GetFileName(NewPath)}",
        FileOperationType.CreateDir => $"Crea cartella: {NewPath}",
        _ => $"{OriginalPath} → {NewPath}"
    };

    private string GetRelativeNewPath()
    {
        try
        {
            var dir = Path.GetDirectoryName(NewPath) ?? "";
            return Path.Combine(Path.GetFileName(dir), Path.GetFileName(NewPath));
        }
        catch
        {
            return NewPath;
        }
    }
}

public interface IFileOrganizerService
{
    // ============================================================
    //  LOCALE
    // ============================================================

    Task<List<ProposedOperation>> PreviewAsync(
        string folder,
        string pattern,
        bool createSubfolders,
        CancellationToken ct = default);

    Task<List<ProposedOperation>> PreviewMergeAsync(
        List<MergeSourceFolder> sourceFolders,
        string destinationFolder,
        string pattern,
        bool createSubfolders,
        CancellationToken ct = default);

    Task<List<ProposedOperation>> PreviewSplitAsync(
        string sourceFolder,
        string destinationFolder,
        List<int> segmentSizes,
        string filePattern,
        string folderPattern,
        SplitNumberingMode mode,
        CancellationToken ct = default);

    Task<int> ApplyAsync(List<ProposedOperation> operations, CancellationToken ct = default);
    Task<int> UndoLastBatchAsync(CancellationToken ct = default);

    // ============================================================
    //  REMOTO (SFTP)
    // ============================================================

    Task<List<ProposedOperation>> PreviewRemoteFolderAsync(
        string remoteFolder,
        string pattern,
        bool createSubfolders,
        CancellationToken ct = default);

    Task<List<ProposedOperation>> PreviewRemoteMergeAsync(
        List<MergeSourceFolder> sourceFolders,
        string remoteDestinationFolder,
        string pattern,
        bool createSubfolders,
        CancellationToken ct = default);

    Task<List<ProposedOperation>> PreviewRemoteSplitAsync(
        string remoteSourceFolder,
        string remoteDestinationFolder,
        List<int> segmentSizes,
        string filePattern,
        string folderPattern,
        SplitNumberingMode mode,
        CancellationToken ct = default);

    Task<int> ApplyRemoteAsync(List<ProposedOperation> operations, CancellationToken ct = default);
    Task<int> UndoLastRemoteBatchAsync(CancellationToken ct = default);

    // ============================================================
    //  COMUNE
    // ============================================================

    Task<List<FileOperation>> GetHistoryAsync(int limit = 200, CancellationToken ct = default);
    Task ClearHistoryAsync(CancellationToken ct = default);
}