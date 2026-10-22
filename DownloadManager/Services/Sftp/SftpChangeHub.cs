namespace DownloadManager.Services.Sftp;

/// <summary>
/// Punto unico in cui passano le modifiche fatte dall'app sui server SFTP (tab SFTP,
/// secondo server, copie tra server...). Ogni modifica porta l'identità del server
/// (host:porta:utente), così chi ascolta (l'albero di Videobox Corrector) aggiorna solo
/// le sorgenti che stanno su quel server, qualunque connessione l'abbia fatta.
/// </summary>
public static class SftpChangeHub
{
    /// <summary>(server, modifica). Può arrivare da qualsiasi thread.</summary>
    public static event Action<string, SftpRemoteChange>? Changed;

    public static void Publish(string? serverKey, SftpRemoteChange change)
    {
        if (string.IsNullOrEmpty(serverKey)) return;
        try { Changed?.Invoke(serverKey, change); } catch { /* un ascoltatore non deve rompere l'operazione */ }
    }
}
