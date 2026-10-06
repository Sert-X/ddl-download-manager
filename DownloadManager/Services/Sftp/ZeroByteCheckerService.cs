using System.Diagnostics;
using DownloadManager.Models;

namespace DownloadManager.Services.Sftp;

public sealed record ZeroByteFolderResult(string FullPath, bool HasZeroByte, string ContentsSummary);

internal enum ScanOutcome
{
    Ok,
    Disconnected,
    DepthLimit,
    FolderLimit,
    Cancelled,
    Error
}

public sealed class ZeroByteCheckerService
{
    private const int MaxDepth = 3;
    private const int MaxFolders = 300;
    private const int MaxConsecutiveErrors = 5;

    private static readonly TimeSpan PerFolderTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan AbortGracePeriod = TimeSpan.FromSeconds(10);

    private readonly ISftpService _sftp;
    private readonly Func<bool> _isConnected;

    private readonly Dictionary<string, bool> _flagCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _contentsCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _zeroFilesByFolder =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _flaggedSubsByFolder =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _cacheLock = new();

    private CancellationTokenSource? _cts;

    public bool IsChecking { get; private set; }
    public string ProgressStatus { get; private set; } = string.Empty;

    /// <summary>
    /// Log sink opzionale. Viene chiamato SOLO per messaggi importanti
    /// (START, FINE, TIMEOUT, errore). I log per-cartella restano su
    /// Debug.WriteLine per non saturare la UI.
    /// </summary>
    public Action<string>? LogSink { get; set; }

    public event EventHandler? StateChanged;
    public event EventHandler<string>? Completed;
    public event EventHandler<ZeroByteFolderResult>? FolderScanned;

    public ZeroByteCheckerService(ISftpService sftp, Func<bool> isConnected)
    {
        _sftp = sftp;
        _isConnected = isConnected;
    }

    /// <summary>
    /// Log interno. Se <paramref name="important"/> è false (default),
    /// va solo su Debug. Se è true, viene propagato anche al LogSink (UI).
    /// </summary>
    private void Log(string message, bool important = false)
    {
        Debug.WriteLine(message);
        if (!important) return;
        try { LogSink?.Invoke(message); } catch { }
    }

    // ============================================================
    //  CACHE API
    // ============================================================

    public bool TryGetFolderFlag(string? path, out bool hasZero)
    {
        hasZero = false;
        if (string.IsNullOrEmpty(path)) return false;
        lock (_cacheLock)
            return _flagCache.TryGetValue(Normalize(path), out hasZero);
    }

    public bool TryGetFolderContents(string? path, out string contents)
    {
        contents = string.Empty;
        if (string.IsNullOrEmpty(path)) return false;
        lock (_cacheLock)
            return _contentsCache.TryGetValue(Normalize(path), out contents!);
    }

    public void SetFolderFlag(string? path, bool hasZero)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (_cacheLock)
            _flagCache[Normalize(path)] = hasZero;
    }

    public void SetFolderContents(string? path, string contents)
    {
        if (string.IsNullOrEmpty(path)) return;
        lock (_cacheLock)
            _contentsCache[Normalize(path)] = contents;
    }

    public void InvalidateFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var key = Normalize(path);
        lock (_cacheLock)
        {
            _flagCache.Remove(key);
            _contentsCache.Remove(key);
            _zeroFilesByFolder.Remove(key);
            _flaggedSubsByFolder.Remove(key);
        }
    }

    public void InvalidateRecursive(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var current = Normalize(path);

        lock (_cacheLock)
        {
            while (!string.IsNullOrEmpty(current))
            {
                _flagCache.Remove(current);
                _contentsCache.Remove(current);
                _zeroFilesByFolder.Remove(current);
                _flaggedSubsByFolder.Remove(current);

                int lastSlash = current.LastIndexOf('/');
                if (lastSlash <= 0)
                {
                    _flagCache.Remove("/");
                    _contentsCache.Remove("/");
                    _zeroFilesByFolder.Remove("/");
                    _flaggedSubsByFolder.Remove("/");
                    break;
                }
                current = current.Substring(0, lastSlash);
            }
        }
    }

    // ============================================================
    //  PATCH IN-MEMORY POST-UPLOAD
    // ============================================================

    public List<string> PatchAfterUpload(string remoteFilePath)
    {
        var changed = new List<string>();
        if (string.IsNullOrEmpty(remoteFilePath)) return changed;

        var parent = GetParent(remoteFilePath);
        if (string.IsNullOrEmpty(parent)) return changed;

        var fileName = GetLeaf(remoteFilePath);

        lock (_cacheLock)
        {
            var parentKey = Normalize(parent);
            if (_zeroFilesByFolder.TryGetValue(parentKey, out var files))
                files.Remove(fileName);

            bool hadZero = _flagCache.TryGetValue(parentKey, out var prev) && prev;
            bool hasZero = ComputeHasZeroLocked(parentKey);

            if (hadZero != hasZero)
            {
                _flagCache[parentKey] = hasZero;
                changed.Add(parent);
            }
            else if (!_flagCache.ContainsKey(parentKey))
            {
                _flagCache[parentKey] = hasZero;
            }

            if (!hasZero)
            {
                var currentChild = parent;

                while (true)
                {
                    var grandParent = GetParent(currentChild);
                    if (string.IsNullOrEmpty(grandParent) || grandParent == currentChild) break;

                    var gpKey = Normalize(grandParent);
                    var childName = GetLeaf(currentChild);

                    if (_flaggedSubsByFolder.TryGetValue(gpKey, out var subs))
                        subs.Remove(childName);

                    bool gpHad = _flagCache.TryGetValue(gpKey, out var p2) && p2;
                    bool gpHas = ComputeHasZeroLocked(gpKey);

                    if (gpHad != gpHas)
                    {
                        _flagCache[gpKey] = gpHas;
                        changed.Add(grandParent);
                    }
                    else if (!_flagCache.ContainsKey(gpKey))
                    {
                        _flagCache[gpKey] = gpHas;
                    }

                    if (gpHas) break;
                    currentChild = grandParent;
                }
            }
        }

        return changed;
    }

    private bool ComputeHasZeroLocked(string folderKey)
    {
        if (_zeroFilesByFolder.TryGetValue(folderKey, out var files) && files.Count > 0)
            return true;

        if (_flaggedSubsByFolder.TryGetValue(folderKey, out var subs) && subs.Count > 0)
            return true;

        return false;
    }

    private static string GetParent(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var trimmed = path.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash <= 0 ? "/" : trimmed.Substring(0, lastSlash);
    }

    private static string GetLeaf(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var trimmed = path.TrimEnd('/');
        int lastSlash = trimmed.LastIndexOf('/');
        return lastSlash < 0 ? trimmed : trimmed.Substring(lastSlash + 1);
    }

    // ============================================================
    //  ESECUZIONE CHECK
    // ============================================================

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
        bool abort = false;
        string finalMessage;

        // Un solo log importante: START del check
        Log($"[ZERO-BYTE] START: {folders.Count} cartelle da controllare", important: true);

        try
        {
            foreach (var folder in folders)
            {
                if (abort) break;
                if (scanned >= MaxFolders) { abort = true; break; }
                if (token.IsCancellationRequested) { abort = true; break; }

                if (!_isConnected())
                {
                    Log("[ZERO-BYTE] SFTP disconnesso, attendo riconnessione...", important: true);
                    var ok = await WaitForReconnectAsync(TimeSpan.FromSeconds(15), token);
                    if (!ok)
                    {
                        Log("[ZERO-BYTE] Fermato: SFTP non riconnesso entro 15s", important: true);
                        abort = true;
                        break;
                    }
                }

                checkedTop++;

                ProgressStatus = $"Controllo {checkedTop}/{folders.Count}: {folder.Name}...";
                RaiseStateChanged();

                // Log per-cartella: solo Debug (importante=false)
                Log($"[ZERO-BYTE] → {checkedTop}/{folders.Count} {folder.FullPath}");

                var sw = Stopwatch.StartNew();

                var (outcome, hasZero, fileCount, dirCount) = await ScanWithWatchdogAsync(
                    folder.FullPath, token, folder.FullPath);

                sw.Stop();

                Log($"[ZERO-BYTE] ← {checkedTop}/{folders.Count} {folder.Name}: {sw.ElapsedMilliseconds}ms, outcome={outcome}");

                switch (outcome)
                {
                    case ScanOutcome.Cancelled:
                    case ScanOutcome.FolderLimit:
                        abort = true;
                        continue;

                    case ScanOutcome.Disconnected:
                        if (!await WaitForReconnectAsync(TimeSpan.FromSeconds(15), token))
                        {
                            Log("[ZERO-BYTE] Fermato: SFTP non riconnesso", important: true);
                            abort = true;
                        }
                        continue;

                    case ScanOutcome.DepthLimit:
                    case ScanOutcome.Ok:
                        consecutiveErrors = 0;
                        break;

                    case ScanOutcome.Error:
                        consecutiveErrors++;
                        Log($"[ZERO-BYTE] Errore #{consecutiveErrors} su {folder.FullPath}", important: true);
                        if (consecutiveErrors >= MaxConsecutiveErrors)
                        {
                            Log($"[ZERO-BYTE] Fermato: {consecutiveErrors} errori consecutivi", important: true);
                            abort = true;
                        }
                        continue;
                }

                var summary = $"{dirCount} cartelle, {fileCount} file";
                SetFolderFlag(folder.FullPath, hasZero);
                SetFolderContents(folder.FullPath, summary);
                FolderScanned?.Invoke(this, new ZeroByteFolderResult(folder.FullPath, hasZero, summary));

                if (hasZero) flagged++;
            }

            if (token.IsCancellationRequested)
                finalMessage = $"Check interrotto: {checkedTop}/{folders.Count} cartelle ({flagged} con file vuoti).";
            else if (scanned >= MaxFolders)
                finalMessage = $"Controllate {checkedTop} cartelle (limite {MaxFolders}): {flagged} con file vuoti.";
            else if (consecutiveErrors >= MaxConsecutiveErrors)
                finalMessage = $"Check interrotto dopo {consecutiveErrors} errori consecutivi.";
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

        Log($"[ZERO-BYTE] FINE: {finalMessage}", important: true);
        Completed?.Invoke(this, finalMessage);
    }

    private async Task<(ScanOutcome outcome, bool hasZero, int fileCount, int dirCount)>
        ScanWithWatchdogAsync(string path, CancellationToken token, string folderFullPath)
    {
        int scannedDummy = 0;

        var scanTask = ScanFolderAsync(
            path, MaxDepth, MaxFolders,
            () => scannedDummy, v => scannedDummy = v, token);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        var watchdog = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(PerFolderTimeout, timeoutCts.Token);
                Log($"[ZERO-BYTE] TIMEOUT {PerFolderTimeout.TotalSeconds}s su {folderFullPath}, abort...", important: true);
                try { _sftp.AbortCurrentOperation(); } catch { }
            }
            catch (OperationCanceledException) { }
        }, timeoutCts.Token);

        var completed = await Task.WhenAny(scanTask, watchdog);

        if (completed == watchdog && !timeoutCts.IsCancellationRequested)
        {
            try
            {
                await Task.WhenAny(scanTask, Task.Delay(AbortGracePeriod, token));
            }
            catch { }

            return (ScanOutcome.Error, false, 0, 0);
        }

        timeoutCts.Cancel();

        try
        {
            var (outcome, hasZero, fileCount, dirCount, _) = await scanTask;
            return (outcome, hasZero, fileCount, dirCount);
        }
        catch (OperationCanceledException)
        {
            return (ScanOutcome.Cancelled, false, 0, 0);
        }
        catch (Exception ex)
        {
            Log($"[ZERO-BYTE] Eccezione su {folderFullPath}: {ex.Message}", important: true);
            return (ScanOutcome.Error, false, 0, 0);
        }
    }

    private async Task<bool> WaitForReconnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            if (_isConnected()) return true;
            try { await Task.Delay(500, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return _isConnected();
    }

    private async Task<(ScanOutcome outcome, bool hasZero, int fileCount, int dirCount, DateTime lastModified)>
        ScanFolderAsync(
            string folderPath, int remainingDepth, int maxFolders,
            Func<int> getScanned, Action<int> setScanned,
            CancellationToken ct)
    {
        if (!_isConnected())
            return (ScanOutcome.Disconnected, false, 0, 0, DateTime.MinValue);
        if (remainingDepth <= 0)
            return (ScanOutcome.DepthLimit, false, 0, 0, DateTime.MinValue);
        if (getScanned() >= maxFolders)
            return (ScanOutcome.FolderLimit, false, 0, 0, DateTime.MinValue);
        if (ct.IsCancellationRequested)
            return (ScanOutcome.Cancelled, false, 0, 0, DateTime.MinValue);

        setScanned(getScanned() + 1);

        List<SftpRemoteEntry> subEntries;
        try
        {
            subEntries = await _sftp.ListDirectoryAsync(folderPath, ct);
        }
        catch (OperationCanceledException)
        {
            return (ScanOutcome.Cancelled, false, 0, 0, DateTime.MinValue);
        }
        catch (Exception ex)
        {
            Log($"[ZERO-BYTE] scan fallito su {folderPath}: {ex.Message}");
            return (ScanOutcome.Error, false, 0, 0, DateTime.MinValue);
        }

        if (ct.IsCancellationRequested)
            return (ScanOutcome.Cancelled, false, 0, 0, DateTime.MinValue);

        var folderKey = Normalize(folderPath);

        var zeroFiles = subEntries
            .Where(e => !e.IsDirectory && e.Size == 0)
            .Select(e => e.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        lock (_cacheLock)
            _zeroFilesByFolder[folderKey] = zeroFiles;

        bool hasZero = zeroFiles.Count > 0;
        int fileCount = subEntries.Count(e => !e.IsDirectory);
        int dirCount = subEntries.Count(e => e.IsDirectory);

        DateTime lastModified = subEntries
            .Where(e => e.Modified > DateTime.MinValue)
            .Select(e => e.Modified)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        var flaggedSubs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (remainingDepth > 1)
        {
            foreach (var sub in subEntries.Where(e => e.IsDirectory))
            {
                if (getScanned() >= maxFolders) break;
                if (ct.IsCancellationRequested) break;

                var (subOutcome, subHas, subFiles, subDirs, subModified) = await ScanFolderAsync(
                    sub.FullPath, remainingDepth - 1, maxFolders,
                    getScanned, setScanned, ct);

                if (subOutcome == ScanOutcome.Error ||
                    subOutcome == ScanOutcome.Cancelled ||
                    subOutcome == ScanOutcome.FolderLimit ||
                    subOutcome == ScanOutcome.Disconnected)
                {
                    if (subOutcome == ScanOutcome.Cancelled ||
                        subOutcome == ScanOutcome.FolderLimit ||
                        subOutcome == ScanOutcome.Disconnected)
                        break;
                    continue;
                }

                fileCount += subFiles;
                dirCount += subDirs;

                if (subModified > lastModified)
                    lastModified = subModified;

                SetFolderFlag(sub.FullPath, subHas);

                if (subHas)
                {
                    hasZero = true;
                    flaggedSubs.Add(sub.Name);
                }
            }
        }

        lock (_cacheLock)
            _flaggedSubsByFolder[folderKey] = flaggedSubs;

        return (ScanOutcome.Ok, hasZero, fileCount, dirCount, lastModified);
    }

    private static string Normalize(string path) => path.TrimEnd('/');

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}