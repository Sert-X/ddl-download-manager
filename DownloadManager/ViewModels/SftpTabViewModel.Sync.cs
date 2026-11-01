using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Sftp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownloadManager.ViewModels;

/// <summary>
/// Allineamento degli esiti (Web, Lingua, file vuoti) alle modifiche fatte da fuori dall'app
/// (WinSCP, altri PC): il server elenca tutti i file sotto la cartella aperta con un solo comando
/// (<see cref="RemoteTreeSync"/>) e lo si confronta con i database:
/// - file spariti → esiti tolti (anche dai conteggi delle cartelle superiori);
/// - stessa dimensione e data in un altro percorso → rinominato o spostato: l'esito lo segue;
/// - dimensione o data cambiate → "da ricontrollare";
/// - file a 0 byte nuovi o spariti → rosso aggiornato in tutto l'albero.
/// Parte all'apertura di una cartella (al massimo ogni minuto per cartella), ogni minuto per
/// le cartelle aperte e dal menu "Esiti" (subito).
/// </summary>
public partial class SftpTabViewModel
{
    /// <summary>Riallineamento automatico della cartella aperta (e al più ogni tanto aprendo una cartella).</summary>
    private static readonly TimeSpan SyncEvery = TimeSpan.FromMinutes(1);
    /// <summary>Dopo una modifica fatta dall'app: riallineamento rapido, ma non più spesso di così.</summary>
    private static readonly TimeSpan SyncSoonMinGap = TimeSpan.FromSeconds(15);
    private DispatcherTimer? _syncSoonTimer;
    private DateTime _lastSyncSoon = DateTime.MinValue;
    private readonly Dictionary<string, DateTime> _lastSync = new(StringComparer.Ordinal);
    /// <summary>Server che non accettano comandi: niente allineamento (resta la pulizia all'apertura).</summary>
    private readonly HashSet<string> _noShellServers = new(StringComparer.OrdinalIgnoreCase);
    private bool _syncRunning;
    private DispatcherTimer? _syncTimer;

    private void InitResultsSync()
    {
        _syncTimer = new DispatcherTimer { Interval = SyncEvery };
        _syncTimer.Tick += (_, _) =>
        {
            _ = SyncResultsAsync(left: false, force: false);
            _ = SyncResultsAsync(left: true, force: false);
        };
        _syncTimer.Start();
    }

    /// <summary>
    /// Modifica fatta dall'app su un server (eliminazione, upload, copia, rinomina, spostamento):
    /// i riassunti delle cartelle sopra (Contenuto, 0 byte, Web…) si riallineano dopo 2 secondi,
    /// senza aspettare il giro automatico. Con tanti upload di fila al massimo uno ogni 15 secondi.
    /// </summary>
    private void ServerChanged(SftpConfig? server)
    {
        if (server == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            var prefix = server.ServerKey + "|";
            foreach (var k in _lastSync.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _lastSync.Remove(k);

            if (_syncSoonTimer == null)
            {
                _syncSoonTimer = new DispatcherTimer();
                _syncSoonTimer.Tick += (_, _) =>
                {
                    _syncSoonTimer.Stop();
                    // Un'altra operazione in corso: si riprova tra poco
                    if (_syncRunning || _webChecker?.IsChecking == true || IsDeletingRemote || IsDeletingServer2)
                    {
                        _syncSoonTimer.Interval = TimeSpan.FromSeconds(3);
                        _syncSoonTimer.Start();
                        return;
                    }
                    _lastSyncSoon = DateTime.Now;
                    _ = RunSoonSyncAsync();
                };
            }
            if (_syncSoonTimer.IsEnabled) return;   // già in programma
            var wait = SyncSoonMinGap - (DateTime.Now - _lastSyncSoon);
            _syncSoonTimer.Interval = wait > TimeSpan.FromSeconds(2) ? wait : TimeSpan.FromSeconds(2);
            _syncSoonTimer.Start();
        });
    }

    private async Task RunSoonSyncAsync()
    {
        await SyncResultsAsync(left: false, force: false);
        if (IsServer2Mode && IsServer2Connected) await SyncResultsAsync(left: true, force: false);
    }

    /// <summary>Riallinea subito gli esiti delle cartelle aperte (menu "Esiti").</summary>
    [RelayCommand]
    private async Task RealignResultsAsync()
    {
        var right = await SyncResultsAsync(left: false, force: true);
        var left = IsServer2Mode && IsServer2Connected ? await SyncResultsAsync(left: true, force: true) : null;
        SftpStatusMessage = string.Join("  ·  ", new[] { right, left }.Where(m => !string.IsNullOrEmpty(m)));
    }

    /// <returns>Messaggio per l'utente (solo se forzato), null se non è partito.</returns>
    private async Task<string?> SyncResultsAsync(bool left, bool force)
    {
        var server = left ? _server2Config : RemoteConfig;
        bool connected = left ? IsServer2Connected : IsRemoteConnected;
        var folder = left ? (Server2Path ?? "/") : SftpCurrentRemotePath;
        if (server == null || !connected || string.IsNullOrWhiteSpace(folder) || folder.TrimEnd('/').Length == 0)
            return force ? "Allineamento non possibile: apri una cartella del server." : null;
        if (_noShellServers.Contains(server.ServerKey) && !force) return null;

        var key = server.ServerKey + "|" + folder.TrimEnd('/');
        if (!force && _lastSync.TryGetValue(key, out var lastAt) && DateTime.Now - lastAt < SyncEvery) return null;
        // Mai insieme a un controllo o a un'eliminazione: scrivono anche loro gli esiti
        if (_syncRunning || _webChecker?.IsChecking == true || IsDeletingRemote || IsDeletingServer2)
            return force ? "Allineamento rimandato: c'è un'altra operazione in corso." : null;

        _syncRunning = true;
        _lastSync[key] = DateTime.Now;
        try
        {
            var items = await RemoteTreeSync.ListTreeAsync(server, folder, CancellationToken.None);
            if (items == null)
            {
                _noShellServers.Add(server.ServerKey);
                return force ? "Allineamento non disponibile: il server non accetta comandi (o la cartella ha troppi file)." : null;
            }
            _noShellServers.Remove(server.ServerKey);

            var report = await ReconcileAsync(server.ServerKey, folder, items);

            // Le tabelle si ricolorano con i dati nuovi
            await ApplyWebStreamToCurrentAsync();
            if (_checkers.TryGetValue(server.ServerKey, out var checker)) await checker.ReloadFromStoreAsync();

            _logger.LogInformation("[ALLINEAMENTO] {Folder} ({Server}): {Report}", folder, server.Name, report);
            return force ? $"Esiti allineati in {folder}: {report}" : null;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("[ALLINEAMENTO] Non riuscito su {Folder}: {Msg}", folder, ex.Message);
            return force ? $"Allineamento non riuscito: {ex.Message}" : null;
        }
        finally { _syncRunning = false; }
    }

    /// <summary>Confronta l'elenco completo del server con gli esiti salvati e li corregge.</summary>
    private async Task<string> ReconcileAsync(string serverKey, string folder, List<RemoteTreeSync.RemoteItem> items)
    {
        var files = items.Where(i => !i.IsDirectory).ToDictionary(i => i.Path, StringComparer.Ordinal);
        int removed = 0, moved = 0, changed = 0;

        // ---- Web / Lingua ----
        if (_webChecker != null)
        {
            var repo = _webChecker.Repository;
            var saved = await repo.GetUnderAsync(serverKey, folder);
            var known = saved.Select(r => r.FullPath).ToHashSet(StringComparer.Ordinal);

            // File senza esito, raggruppati per dimensione: candidati per le rinomine
            var unclaimed = files.Values
                .Where(f => !known.Contains(f.Path) && WebStreamCheckerService.VideoExtensions.Contains(Path.GetExtension(f.Path)))
                .GroupBy(f => f.Size)
                .ToDictionary(g => g.Key, g => g.ToList());

            var toDelete = new List<string>();
            var toMark = new List<string>();
            foreach (var r in saved)
            {
                if (files.TryGetValue(r.FullPath, out var now))
                {
                    if (r.Size != WebStreamCheckRepository.ChangedMarker && !r.Matches(now.Size, now.Modified))
                        toMark.Add(r.FullPath);
                    continue;
                }
                // Sparito: rinominato/spostato (stessa dimensione e data, un solo candidato) o eliminato
                if (r.Size > 0 && unclaimed.TryGetValue(r.Size, out var sameSize))
                {
                    var match = sameSize.Where(f => r.Matches(f.Size, f.Modified)).ToList();
                    if (match.Count == 1)
                    {
                        await repo.MoveAsync(serverKey, r.FullPath, match[0].Path);
                        sameSize.Remove(match[0]);
                        moved++;
                        continue;
                    }
                }
                toDelete.Add(r.FullPath);
            }
            await repo.DeleteManyAsync(serverKey, toDelete);
            await repo.MarkChangedAsync(serverKey, toMark);
            removed = toDelete.Count;
            changed = toMark.Count;
        }

        // ---- File vuoti (0 byte) e colonna Contenuto ----
        int zeros = 0;
        if (App.Services?.GetService<ZeroByteCheckRepository>() is { } zeroRepo)
        {
            var zeroFiles = files.Values.Where(f => f.Size == 0).Select(f => f.Path).ToList();
            zeros = zeroFiles.Count;
            await zeroRepo.ReplaceZeroFilesUnderAsync(serverKey, folder, zeroFiles);

            // Riassunti delle cartelle già controllate, ricontati dall'elenco
            var checkedFolders = await zeroRepo.GetCheckedFoldersUnderAsync(serverKey, folder);
            if (checkedFolders.Count > 0)
            {
                var counts = checkedFolders.ToDictionary(f => f.TrimEnd('/'), _ => (Dirs: 0, Files: 0), StringComparer.Ordinal);
                foreach (var item in items)
                {
                    var parent = TransferJobBase.GetRemoteParent(item.Path).TrimEnd('/');
                    while (parent.Length > 0)
                    {
                        if (counts.TryGetValue(parent, out var c))
                            counts[parent] = item.IsDirectory ? (c.Dirs + 1, c.Files) : (c.Dirs, c.Files + 1);
                        var up = TransferJobBase.GetRemoteParent(parent).TrimEnd('/');
                        if (up == parent) break;
                        parent = up;
                    }
                }
                await zeroRepo.UpdateContentsAsync(serverKey,
                    counts.ToDictionary(kv => kv.Key, kv => $"{kv.Value.Dirs} cartelle, {kv.Value.Files} file"));
            }
        }

        var parts = new List<string>();
        if (removed > 0) parts.Add($"{removed} esiti di file non più presenti tolti");
        if (moved > 0) parts.Add($"{moved} seguono file rinominati o spostati");
        if (changed > 0) parts.Add($"{changed} file cambiati da ricontrollare");
        parts.Add(zeros == 1 ? "1 file vuoto" : $"{zeros} file vuoti");
        return string.Join(", ", parts) + $" ({files.Count:N0} file letti).";
    }
}
