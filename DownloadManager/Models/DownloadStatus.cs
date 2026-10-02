namespace DownloadManager.Models;

public enum DownloadStatus
{
    Pending,       // In coda, non ancora iniziato
    Downloading,   // In corso
    Paused,        // In pausa (l'utente ha messo in pausa)
    Completed,     // Completato con successo
    Failed,        // Fallito per errore
    Cancelled      // Annullato dall'utente
}