using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;

namespace DownloadManager.ViewModels;

public enum NodeStatus { None, Valid, Invalid, Mixed, Mismatch }

public partial class SftpNode : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public ObservableCollection<SftpNode> Children { get; set; } = new();
    public SftpNode? Parent { get; set; }
    /// <summary>Sorgente (server + cartella) a cui appartiene il nodo.</summary>
    public DownloadManager.Models.VideoboxSource? Source { get; set; }
    /// <summary>Radice di una sorgente nell'albero (il suo nome indica sorgente e server).</summary>
    public bool IsSourceRoot { get; set; }
    /// <summary>Radice di una sorgente che non è stato possibile leggere (server o cartella non raggiungibili).</summary>
    public bool HasLoadError { get; set; }
    /// <summary>Configurazione della sorgente quando la radice è stata letta (se cambia, va riletta).</summary>
    public string LoadedSignature { get; set; } = "";

    /// <summary>Stato calcolato solo dal match con i link (mai Mismatch).</summary>
    public NodeStatus BaseStatus { get; set; } = NodeStatus.None;

    /// <summary>Stato mostrato in UI (può essere Mismatch).</summary>
    [ObservableProperty] private NodeStatus status = NodeStatus.None;

    // Chiave di confronto del nodo (percorso relativo alla sorgente, minuscolo): si calcola una
    // volta e si riusa a ogni ricolorazione finché sorgente e cartella radice non cambiano.
    private string? _keyCache;
    private string? _keyStamp;

    public string KeyFor(DownloadManager.Models.VideoboxSource source)
    {
        var stamp = source.Id + "|" + source.RootPath;
        if (_keyCache == null || _keyStamp != stamp)
        {
            _keyCache = source.NodeKey(FullPath);
            _keyStamp = stamp;
        }
        return _keyCache;
    }

    /// <summary>Azzera ricorsivamente tutti gli stati.</summary>
    public void Reset()
    {
        BaseStatus = NodeStatus.None;
        Status = NodeStatus.None;
        foreach (var child in Children) child.Reset();
    }

    /// <summary>
    /// Calcola dal basso verso l'alto i BaseStatus delle cartelle
    /// (i file devono avere già BaseStatus Valid/Invalid) e li copia in Status.
    /// Va chiamato sui nodi radice.
    /// </summary>
    public void RefreshStatus()
    {
        if (!IsDirectory)
        {
            Status = BaseStatus;
            return;
        }

        foreach (var child in Children) child.RefreshStatus();

        bool hasValid = false, hasInvalid = false;
        foreach (var c in Children)
        {
            if (c.BaseStatus is NodeStatus.Valid or NodeStatus.Mixed) hasValid = true;
            if (c.BaseStatus is NodeStatus.Invalid or NodeStatus.Mixed) hasInvalid = true;
            if (hasValid && hasInvalid) break;
        }

        BaseStatus = (hasValid, hasInvalid) switch
        {
            (true, true)  => NodeStatus.Mixed,
            (true, false) => NodeStatus.Valid,
            (false, true) => NodeStatus.Invalid,
            _             => NodeStatus.None   // cartella vuota
        };
        Status = BaseStatus;
    }

    /// <summary>Gravità di uno stato: un antenato mostra sempre il problema più grave dei discendenti.</summary>
    private static int Severity(NodeStatus status) => status switch
    {
        NodeStatus.Valid => 1,
        NodeStatus.Mixed => 2,
        NodeStatus.Invalid => 3,
        NodeStatus.Mismatch => 4,
        _ => 0
    };

    /// <summary>Porta lo stato mostrato a <paramref name="status"/> se è più grave di quello attuale.</summary>
    public void Raise(NodeStatus status)
    {
        if (Severity(status) > Severity(Status)) Status = status;
    }

    /// <summary>Propaga uno stato a tutte le cartelle superiori, fino a quella principale della serie.</summary>
    public void RaiseAncestors(NodeStatus status)
    {
        for (var p = Parent; p != null; p = p.Parent) p.Raise(status);
    }

    /// <summary>
    /// Cartella di un post con link misti (alcuni fuori dai domini impostati): lei e le
    /// sottocartelle diventano gialle per segnalarlo, i file mantengono il proprio colore.
    /// </summary>
    public void ApplyMixed()
    {
        if (!IsDirectory) return;
        Raise(NodeStatus.Mixed);
        foreach (var child in Children) child.ApplyMixed();
    }

    /// <summary>
    /// Applica il viola a una cartella associata a un post in Mismatch:
    /// - la cartella e tutte le sottocartelle diventano viola
    /// - i file con match restano verdi
    /// - solo i file orfani (senza match) diventano viola
    /// Gli antenati si colorano a parte con <see cref="RaiseAncestors"/>.
    /// </summary>
    public void ApplyMismatch()
    {
        if (IsDirectory)
        {
            Status = NodeStatus.Mismatch;
            foreach (var child in Children) child.ApplyMismatch();
        }
        else if (BaseStatus == NodeStatus.Invalid)
        {
            Status = NodeStatus.Mismatch;
        }
    }
}