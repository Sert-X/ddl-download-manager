namespace DownloadManager.Models;

public enum DomainStatus
{
    Unknown,      // mai testato
    Ok,           // ha risposto HTTP (qualunque codice)
    Unreachable   // DNS fail, timeout, connection refused
}