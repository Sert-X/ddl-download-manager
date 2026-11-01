using CommunityToolkit.Mvvm.ComponentModel;
using DownloadManager.Models;
using DownloadManager.Services.Sftp;

namespace DownloadManager.ViewModels;

/// <summary>
/// Analisi delle cartelle del PC (pannello di sinistra in modalità locale): file vuoti (0 byte) e
/// contenuto. Il disco è veloce: si legge tutto l'albero delle cartelle scelte, e i risultati restano
/// in memoria (si ricalcolano in un attimo; un file a 0 byte si vede comunque sempre da solo).
/// </summary>
public partial class SftpTabViewModel
{
    /// <summary>Cartelle del PC con file a 0 byte dentro (anche in profondità): quanti.</summary>
    private readonly Dictionary<string, int> _localZeroCounts = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cartelle del PC analizzate per i file vuoti (le loro sottocartelle sono note anche loro).</summary>
    private readonly HashSet<string> _localCheckedFolders = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Contenuto delle cartelle del PC contate ("N cartelle, M file").</summary>
    private readonly Dictionary<string, string> _localContents = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty] private bool _isCheckingLocalZero;
    partial void OnIsCheckingLocalZeroChanged(bool value)
    {
        OnPropertyChanged(nameof(LocalStatusText));
        OnPropertyChanged(nameof(HasLocalStatus));
    }

    private static string LocalKeyOf(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>La cartella è (dentro) una cartella già analizzata per i file vuoti.</summary>
    private bool IsLocalZeroKnown(string key) =>
        _localCheckedFolders.Any(c => key.Equals(c, StringComparison.OrdinalIgnoreCase)
                                      || key.StartsWith(c + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>File vuoti e contenuto sulle righe del PC (per le cartelle già analizzate).</summary>
    private void ApplyLocalZeroFlags(IEnumerable<LocalFileEntry> entries)
    {
        foreach (var e in entries)
        {
            if (!e.IsDirectory)
            {
                e.HasZeroByteIssue = e.Size == 0;
                e.ZeroCount = null;
                continue;
            }
            var key = LocalKeyOf(e.FullPath);
            var count = _localZeroCounts.GetValueOrDefault(key);
            e.ZeroCount = IsLocalZeroKnown(key) ? count : null;
            e.HasZeroByteIssue = e.ZeroCount > 0;
            e.ContentsInfo = _localContents.GetValueOrDefault(key) ?? string.Empty;
        }
    }

    /// <summary>Analisi di tutte le cartelle della cartella aperta del PC.</summary>
    public Task CheckLocalZeroBytesAsync(bool zero, bool contents) =>
        CheckLocalZeroOnSelectionAsync(SftpLocalEntries.Where(e => e.IsDirectory).ToList(), zero, contents);

    /// <summary>Analisi delle cartelle del PC indicate (con tutte le sottocartelle).</summary>
    public async Task CheckLocalZeroOnSelectionAsync(IReadOnlyList<LocalFileEntry> selection, bool zero = true, bool contents = true)
    {
        var folders = selection.Where(e => e.IsDirectory).ToList();
        if (folders.Count == 0) return;
        if (IsCheckingLocalZero) { SftpStatusMessage = "Un'analisi delle cartelle del PC è già in corso."; return; }
        if (!zero && !contents) return;

        IsCheckingLocalZero = true;
        int flagged = 0, totalDirs = 0, totalFiles = 0;
        try
        {
            var results = await Task.Run(() => folders.Select(f => (Folder: f, Scan: ScanLocalFolder(f.FullPath))).ToList());
            foreach (var (folder, scan) in results)
            {
                var key = LocalKeyOf(folder.FullPath);
                if (contents) _localContents[key] = ZeroByteCheckerService.FormatContents(scan.Dirs, scan.Files);
                totalDirs += scan.Dirs;
                totalFiles += scan.Files;
                if (!zero) continue;

                // Dimentica i risultati vecchi di questo albero, poi conta i file vuoti di ogni cartella
                foreach (var old in _localZeroCounts.Keys.Where(p => p.Equals(key, StringComparison.OrdinalIgnoreCase)
                                                       || p.StartsWith(key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
                    _localZeroCounts.Remove(old);
                _localCheckedFolders.Add(key);
                foreach (var file in scan.ZeroFiles)
                {
                    var dir = Path.GetDirectoryName(file);
                    while (!string.IsNullOrEmpty(dir) && dir.Length >= key.Length)
                    {
                        var dk = LocalKeyOf(dir);
                        _localZeroCounts[dk] = _localZeroCounts.GetValueOrDefault(dk) + 1;
                        dir = Path.GetDirectoryName(dir);
                    }
                }
                if (scan.ZeroFiles.Count > 0) flagged++;
            }
            ApplyLocalZeroFlags(SftpLocalEntries);

            var found = new List<string>();
            if (zero) found.Add(flagged == 0 ? "nessuna contiene file vuoti"
                : flagged == 1 ? "1 contiene file vuoti (in rosso)" : $"{flagged} contengono file vuoti (in rosso)");
            if (contents) found.Add($"in tutto {ZeroByteCheckerService.FormatContents(totalDirs, totalFiles)}");
            SftpStatusMessage = $"Analizzate {folders.Count} cartelle sul PC: {string.Join("; ", found)}.";
        }
        catch (Exception ex) { SftpStatusMessage = $"Analisi delle cartelle del PC non riuscita: {ex.Message}"; }
        finally { IsCheckingLocalZero = false; }
    }

    /// <summary>File a 0 byte, cartelle e file sotto una cartella del PC (nascosti e di sistema esclusi).</summary>
    private static (List<string> ZeroFiles, int Dirs, int Files) ScanLocalFolder(string folder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };
        var zero = new List<string>();
        int dirs = 0, files = 0;
        foreach (var item in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
        {
            if (item is FileInfo f)
            {
                files++;
                if (f.Length == 0) zero.Add(f.FullName);
            }
            else dirs++;
        }
        return (zero, dirs, files);
    }
}
