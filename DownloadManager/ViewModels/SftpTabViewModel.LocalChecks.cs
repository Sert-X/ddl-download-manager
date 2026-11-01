using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;

namespace DownloadManager.ViewModels;

/// <summary>
/// Check dei file vuoti (0 byte) sui file del PC (pannello di sinistra in modalità locale).
/// Il disco è veloce: si controlla tutto l'albero delle cartelle scelte, e i risultati restano in
/// memoria (si ricalcolano in un attimo; un file a 0 byte si vede comunque sempre da solo).
/// </summary>
public partial class SftpTabViewModel
{
    /// <summary>Cartelle del PC che contengono (anche in profondità) un file a 0 byte.</summary>
    private readonly HashSet<string> _localZeroFolders = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cartelle del PC già controllate (per sapere che sono "pulite").</summary>
    private readonly HashSet<string> _localCheckedFolders = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool _isCheckingLocalZero;
    partial void OnIsCheckingLocalZeroChanged(bool value)
    {
        OnPropertyChanged(nameof(LocalStatusText));
        OnPropertyChanged(nameof(HasLocalStatus));
    }

    private static string LocalKeyOf(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Rosso sui file vuoti e sulle cartelle che ne contengono (se già controllate).</summary>
    private void ApplyLocalZeroFlags(IEnumerable<LocalFileEntry> entries)
    {
        foreach (var e in entries)
        {
            e.HasZeroByteIssue = e.IsDirectory
                ? _localZeroFolders.Contains(LocalKeyOf(e.FullPath))
                : e.Size == 0;
        }
    }

    /// <summary>Check dei file vuoti di tutte le cartelle della cartella aperta del PC.</summary>
    [RelayCommand]
    private Task CheckLocalZeroBytesAsync() =>
        CheckLocalZeroOnSelectionAsync(SftpLocalEntries.Where(e => e.IsDirectory).ToList());

    /// <summary>Check dei file vuoti delle cartelle del PC indicate (con tutte le sottocartelle).</summary>
    public async Task CheckLocalZeroOnSelectionAsync(IReadOnlyList<LocalFileEntry> selection)
    {
        var folders = selection.Where(e => e.IsDirectory).ToList();
        if (folders.Count == 0) { SftpStatusMessage = "Nessuna cartella da controllare."; return; }
        if (IsCheckingLocalZero) { SftpStatusMessage = "Un check 0-byte sul PC è già in corso."; return; }

        IsCheckingLocalZero = true;
        int flagged = 0;
        try
        {
            var results = await Task.Run(() => folders.Select(f => (Folder: f, Zero: ScanLocalFolder(f.FullPath))).ToList());
            foreach (var (folder, zeroFiles) in results)
            {
                var key = LocalKeyOf(folder.FullPath);
                // Dimentica i risultati vecchi di questo albero, poi segna le cartelle con file vuoti
                _localZeroFolders.RemoveWhere(p => p.Equals(key, StringComparison.OrdinalIgnoreCase)
                                                   || p.StartsWith(key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                _localCheckedFolders.Add(key);
                foreach (var file in zeroFiles)
                {
                    var dir = Path.GetDirectoryName(file);
                    while (!string.IsNullOrEmpty(dir) && dir.Length >= key.Length)
                    {
                        _localZeroFolders.Add(LocalKeyOf(dir));
                        dir = Path.GetDirectoryName(dir);
                    }
                }
                if (zeroFiles.Count > 0) flagged++;
            }
            ApplyLocalZeroFlags(SftpLocalEntries);
            SftpStatusMessage = flagged == 0
                ? $"Controllate {folders.Count} cartelle sul PC: nessuna contiene file vuoti."
                : $"Controllate {folders.Count} cartelle sul PC: {flagged} contengono file vuoti (evidenziate in rosso).";
        }
        catch (Exception ex) { SftpStatusMessage = $"Check 0-byte sul PC non riuscito: {ex.Message}"; }
        finally { IsCheckingLocalZero = false; }
    }

    /// <summary>File a 0 byte sotto una cartella del PC (nascosti e di sistema esclusi).</summary>
    private static List<string> ScanLocalFolder(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        return new DirectoryInfo(folder).EnumerateFiles("*", options)
            .Where(f => f.Length == 0)
            .Select(f => f.FullName)
            .ToList();
    }
}
