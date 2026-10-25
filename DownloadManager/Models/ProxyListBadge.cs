namespace DownloadManager.Models;

/// <summary>Piccolo badge "sigla provider + stato" mostrato nella vista Archivio.</summary>
public class ProxyListBadge
{
    public string Short { get; init; } = "";
    public string Title { get; init; } = "";
    public ProxyEntryStatus Status { get; init; }
    public bool IsActive { get; init; }
    /// <summary>Il proxy non è in questa lista: casella vuota, per tenere le sigle allineate in colonna.</summary>
    public bool IsAbsent { get; init; }

    public bool StatusOk => !IsAbsent && Status == ProxyEntryStatus.Ok;
    public bool StatusFailed => !IsAbsent && Status == ProxyEntryStatus.Failed;
    public bool StatusExcluded => !IsAbsent && Status == ProxyEntryStatus.Excluded;
    public bool StatusUntested => !IsAbsent && Status == ProxyEntryStatus.Untested;
    public string Tooltip => IsAbsent
        ? $"{Title}: non in lista"
        : $"{Title}: {Label(Status).ToLowerInvariant()}{(IsActive ? " · attivo" : "")}";

    public static string Label(ProxyEntryStatus s) => s switch
    {
        ProxyEntryStatus.Ok => "Funziona",
        ProxyEntryStatus.Failed => "Fallito",
        ProxyEntryStatus.Excluded => "Escluso",
        _ => "Da testare"
    };
}
