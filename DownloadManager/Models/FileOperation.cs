namespace DownloadManager.Models;

public enum FileOperationType
{
    Move,       // Spostamento (include rinomina se la cartella è la stessa)
    Rename,     // Solo rinomina (stessa cartella)
    CreateDir   // Creazione cartella (per undo)
}

/// <summary>
/// Traccia un'operazione sul file system per consentire l'undo.
/// </summary>
public class FileOperation
{
    public int Id { get; set; }

    public FileOperationType Type { get; set; }

    /// <summary>
    /// Percorso originale (prima dell'operazione).
    /// </summary>
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>
    /// Percorso finale (dopo l'operazione).
    /// </summary>
    public string NewPath { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp UTC dell'operazione.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Raggruppa più operazioni in un singolo batch (per undo multiplo).
    /// </summary>
    public string BatchId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Se true, l'operazione è già stata annullata.
    /// </summary>
    public bool IsUndone { get; set; }

    /// <summary>
    /// Se true, l'operazione è avvenuta su un server SFTP (non sul filesystem locale).
    /// </summary>
    public bool IsRemote { get; set; }

    /// <summary>
    /// Descrizione leggibile dell'operazione.
    /// </summary>
    public string Description => Type switch
    {
        FileOperationType.Move => $"Spostato: {Path.GetFileName(OriginalPath)} → {NewPath}",
        FileOperationType.Rename => $"Rinominato: {Path.GetFileName(OriginalPath)} → {Path.GetFileName(NewPath)}",
        FileOperationType.CreateDir => $"Creata cartella: {NewPath}",
        _ => $"{Type}: {OriginalPath} → {NewPath}"
    };
}