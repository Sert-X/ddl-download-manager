namespace DownloadManager.Services.Download;

/// <summary>
/// La playlist delle tracce (o i sottotitoli scelti) non si è potuta leggere: si riprova,
/// anche con un altro proxy, invece di consegnare un file senza le lingue scelte.
/// </summary>
public sealed class TracksUnavailableException : Exception
{
    public TracksUnavailableException(string message) : base(message) { }
}

/// <summary>
/// Il download è fallito con tutti i proxy disponibili della lista: errore definitivo,
/// la coda non lo riprova più da sola.
/// </summary>
public sealed class ProxiesExhaustedException : Exception
{
    public ProxiesExhaustedException(string message, Exception inner) : base(message, inner) { }
}
