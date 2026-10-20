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

    /// <summary>Stato calcolato solo dal match con i link (mai Mismatch).</summary>
    public NodeStatus BaseStatus { get; set; } = NodeStatus.None;

    /// <summary>Stato mostrato in UI (può essere Mismatch).</summary>
    [ObservableProperty] private NodeStatus status = NodeStatus.None;

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

        bool hasValid = Children.Any(c => c.BaseStatus is NodeStatus.Valid or NodeStatus.Mixed);
        bool hasInvalid = Children.Any(c => c.BaseStatus is NodeStatus.Invalid or NodeStatus.Mixed);

        BaseStatus = (hasValid, hasInvalid) switch
        {
            (true, true)  => NodeStatus.Mixed,
            (true, false) => NodeStatus.Valid,
            (false, true) => NodeStatus.Invalid,
            _             => NodeStatus.None   // cartella vuota
        };
        Status = BaseStatus;
    }

    /// <summary>
    /// Applica il viola a una cartella associata a un post in Mismatch:
    /// - la cartella e tutte le sottocartelle diventano viola
    /// - i file con match restano verdi
    /// - solo i file orfani (senza match) diventano viola
    /// Gli antenati NON vengono toccati.
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