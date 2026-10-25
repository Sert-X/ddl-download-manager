namespace DownloadManager.Models;

public enum ProxyEntryStatus
{
    Untested = 0,
    Ok = 1,
    Failed = 2,
    /// <summary>Tolto dalla lista del provider: non si testa più e non torna reimportando.</summary>
    Excluded = 3
}

/// <summary>Presenza di un proxy nella lista di un provider, con l'esito dei test per quel provider.</summary>
public class ProxyListEntry
{
    public string Provider { get; set; } = "";
    public ProxyEntryStatus Status { get; set; } = ProxyEntryStatus.Untested;
    public string LastResult { get; set; } = "";
    public DateTime? LastTestedUtc { get; set; }
    public int? LatencyMs { get; set; }

    /// <summary>Usato dal provider (può essercene più d'uno: l'app sceglie il più sano).</summary>
    public bool IsActive { get; set; }

    /// <summary>Disattivato a mano: un test riuscito non lo riattiva da solo.</summary>
    public bool ManuallyDisabled { get; set; }

    public bool IsInList => Status != ProxyEntryStatus.Excluded;
}
