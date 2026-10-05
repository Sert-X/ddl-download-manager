using System.Diagnostics;
using DownloadManager.Models;

namespace DownloadManager.Services.Sftp;

public sealed record ZeroByteFolderResult(string FullPath, bool HasZeroByte, string ContentsSummary);

/// <summary>
/// Esegue il check ricorsivo 0-byte su cartelle SFTP e mantiene le cache
/// (flag "contiene file vuoti" + riepilogo contenuti "N cartelle, M file").
/// La ricorsione è limitata a MaxDepth livelli / MaxFolders cartelle.
/// </summary>
public sealed class ZeroByteCheckerService
{
    private const int MaxDepth = 3;
    private const int MaxFolders = 300;
    private const int MaxConsecutiveErrors = 5;

    private readonly ISftpService _sftp;
    private readonly Func<bool> _isConnected;

    private readonly Dictionary<string, bool> _flagCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _contentsCache = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;

    public bool IsChecking { get; private set; }
    public string ProgressStatus { get; private set; } = string.Empty;

    /// <summary>Scatta quando IsChecking o ProgressStatus cambiano.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Scatta una volta sola al termine del check, con il messaggio finale.</summary>
    public event EventHandler<string>? Completed;

    /// <summary>Scatta per ogni cartella di primo livello scansionata.</summary>
    public event EventHandler<ZeroByteFolderResult>? FolderScanned;

    public ZeroByteCheckerService(ISftpService sftp, Func<bool> isConnected)
    {
        _sftp = sftp;
        _isConnected = isConnected;
    }

    // ---------- Cache API ----------

    public bool TryGetFolderFlag(string? path, out bool hasZero)
    {
        hasZero = false;
        if (string.IsNullOrEmpty(path)) return false;
        return _flagCache.TryGetValue(Normalize(path), out hasZero);
    }

    public bool TryGetFolderContents(string? path, out string contents)
    {
        contents = string.Empty;
        if (string.IsNullOrEmpty(path)) return false;
        return _contentsCache.TryGetValue(Normalize(path), out contents!);
    }

    public void SetFolderFlag(string? path, bool hasZero)
    {
        if (string.IsNullOrEmpty(path)) return;
        _flagCache[Normalize(path)] = hasZero;
    }

    public void SetFolderContents(string? path, string contents)
    {
        if (string.IsNullOrEmpty(path)) return;
        _contentsCache[Normalize(path)] = contents;
    }

    public void InvalidateFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var key = Normalize(path);
        _flagCache.Remove(key);
        _contentsCache.Remove(key);
    }

    public void InvalidateRecursive(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var current = Normalize(path);

        while (!string.IsNullOrEmpty(current))
        {
            _flagCache.Remove(current);
            _contentsCache.Remove(current);

            int lastSlash = current.LastIndexOf('/');
            if (lastSlash <= 0)
            {
                _flagCache.Remove("/");
                _contentsCache.Remove("/");
                break;
            }
            current = current.Substring(0, lastSlash);
        }
    }

    // ---------- Esecuzione ----------

    public void Stop()
    {
        if (_cts == null || _cts.IsCancellationRequested)
        {
            ProgressStatus = "Nessun check in corso.";
            RaiseStateChanged();
            return;
        }

        _cts.Cancel();
        ProgressStatus = "Interruzione in corso...";
        RaiseStateChanged();
    }

    public async Task RunCheckAsync(IReadOnlyList<SftpRemoteEntry> folders)
    {
        if (IsChecking) return;

        if (folders.Count == 0)
        {
            Completed?.Invoke(this, "Nessuna cartella da controllare.");
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsChecking = true;
        ProgressStatus = "Avvio check 0-byte in corso...";
        RaiseStateChanged();

        int scanned = 0;
        int flagged = 0;
        int checkedTop = 0;
        int consecutiveErrors = 0;
        string finalMessage;

        try
        {
            foreach (var folder in folders)
            {
                if (!_isConnected())
                {
                    Debug.WriteLine("[ZERO-BYTE-CHECK] Fermato: SFTP disconnesso");
                    break;
                }
                if (scanned >= MaxFolders) break;
                if (token.IsCancellationRequested) break;

                if (consecutiveErrors >= MaxConsecutiveErrors)
                {
                    Debug.WriteLine($"[ZERO-BYTE-CHECK] Fermato: {consecutiveErrors} errori consecutivi (server giù?)");
                    break;
                }

                checkedTop++;

                try
                {
                    ProgressStatus = $"Controllo {checkedTop}/{folders.Count}: {folder.Name}...";
                    RaiseStateChanged();

                    var (hasZero, fileCount, dirCount, _) = await ScanFolderAsync(
                        folder.FullPath, MaxDepth, MaxFolders,
                        () => scanned, v => scanned = v, token);

                    if (fileCount == 0 && dirCount == 0) consecutiveErrors++;
                    else consecutiveErrors = 0;

                    var summary = $"{dirCount} cartelle, {fileCount} file";

                    SetFolderFlag(folder.FullPath, hasZero);
                    SetFolderContents(folder.FullPath, summary);

                    FolderScanned?.Invoke(this, new ZeroByteFolderResult(folder.FullPath, hasZero, summary));

                    if (hasZero) flagged++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    consecutiveErrors++;
                    Debug.WriteLine($"[ZERO-BYTE-CHECK] Errore su {folder.FullPath}: {ex.Message}");
                }
            }

            if (token.IsCancellationRequested)
                finalMessage = $"Check interrotto: {checkedTop}/{folders.Count} cartelle ({flagged} con file vuoti).";
            else if (scanned >= MaxFolders)
                finalMessage = $"Controllate {checkedTop} cartelle (limite {MaxFolders}): {flagged} con file vuoti.";
            else if (consecutiveErrors >= MaxConsecutiveErrors)
                finalMessage = $"Check interrotto dopo {consecutiveErrors} errori consecutivi. Il server è ancora raggiungibile?";
            else
                finalMessage = flagged == 0
                    ? $"Controllate {checkedTop} cartelle: nessuna contiene file vuoti."
                    : $"Controllate {checkedTop} cartelle: {flagged} contengono file vuoti (evidenziate in rosso).";
        }
        finally
        {
            IsChecking = false;
            ProgressStatus = string.Empty;
            _cts?.Dispose();
            _cts = null;
            RaiseStateChanged();
        }

        Completed?.Invoke(this, finalMessage);
    }

    private async Task<(bool hasZero, int fileCount, int dirCount, DateTime lastModified)>
        ScanFolderAsync(
            string folderPath, int remainingDepth, int maxFolders,
            Func<int> getScanned, Action<int> setScanned,
            CancellationToken ct)
    {
        if (!_isConnected()) return (false, 0, 0, DateTime.MinValue);
        if (remainingDepth <= 0) return (false, 0, 0, DateTime.MinValue);
        if (getScanned() >= maxFolders) return (false, 0, 0, DateTime.MinValue);
        if (ct.IsCancellationRequested) return (false, 0, 0, DateTime.MinValue);

        setScanned(getScanned() + 1);

        List<SftpRemoteEntry> subEntries;
        try
        {
            subEntries = await _sftp.ListDirectoryAsync(folderPath, ct);
        }
        catch (OperationCanceledException)
        {
            return (false, 0, 0, DateTime.MinValue);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZERO-BYTE-SCAN] fallito su {folderPath}: {ex.Message}");
            return (false, 0, 0, DateTime.MinValue);
        }

        if (ct.IsCancellationRequested) return (false, 0, 0, DateTime.MinValue);

        bool hasZero = subEntries.Any(e => !e.IsDirectory && e.Size == 0);
        int fileCount = subEntries.Count(e => !e.IsDirectory);
        int dirCount = subEntries.Count(e => e.IsDirectory);

        DateTime lastModified = subEntries
            .Where(e => e.Modified > DateTime.MinValue)
            .Select(e => e.Modified)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        if (remainingDepth > 1)
        {
            foreach (var sub in subEntries.Where(e => e.IsDirectory))
            {
                if (getScanned() >= maxFolders) break;
                if (ct.IsCancellationRequested) break;

                var (subHas, subFiles, subDirs, subModified) = await ScanFolderAsync(
                    sub.FullPath, remainingDepth - 1, maxFolders,
                    getScanned, setScanned, ct);

                fileCount += subFiles;
                dirCount += subDirs;

                if (subModified > lastModified)
                    lastModified = subModified;

                SetFolderFlag(sub.FullPath, subHas);

                if (subHas) hasZero = true;
            }
        }

        return (hasZero, fileCount, dirCount, lastModified);
    }

    private static string Normalize(string path) => path.TrimEnd('/');

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}