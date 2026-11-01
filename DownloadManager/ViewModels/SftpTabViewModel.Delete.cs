using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Services.Sftp;
using Microsoft.Extensions.Logging;

namespace DownloadManager.ViewModels;

/// <summary>
/// Eliminazione di file e cartelle sul server, in entrambi i pannelli, con avanzamento nella riga
/// di stato e pulsante per fermarla. Le cartelle si eliminano con <see cref="RemoteDeleter"/>
/// (connessione a parte): la sessione del browser non va più in timeout con cartelle enormi.
/// </summary>
public partial class SftpTabViewModel
{
    private CancellationTokenSource? _remoteDeleteCts;
    private CancellationTokenSource? _server2DeleteCts;

    [ObservableProperty] private bool _isDeletingRemote;
    [ObservableProperty] private string _remoteDeleteStatus = string.Empty;
    [ObservableProperty] private bool _isDeletingServer2;
    [ObservableProperty] private string _server2DeleteStatus = string.Empty;

    partial void OnIsDeletingRemoteChanged(bool value) => NotifyRemoteStatus();
    partial void OnRemoteDeleteStatusChanged(string value) => NotifyRemoteStatus();
    partial void OnIsDeletingServer2Changed(bool value) => NotifyServer2Status();
    partial void OnServer2DeleteStatusChanged(string value) => NotifyServer2Status();

    private void NotifyRemoteStatus()
    {
        OnPropertyChanged(nameof(RemoteStatusText));
        OnPropertyChanged(nameof(HasRemoteStatus));
        OnPropertyChanged(nameof(RemoteStatusTooltip));
        NotifyStatusIcons();
    }

    [RelayCommand]
    private void StopRemoteDelete()
    {
        if (_remoteDeleteCts is { IsCancellationRequested: false } cts)
        {
            cts.Cancel();
            RemoteDeleteStatus = "Interruzione dell'eliminazione...";
        }
    }

    [RelayCommand]
    private void StopServer2Delete()
    {
        if (_server2DeleteCts is { IsCancellationRequested: false } cts)
        {
            cts.Cancel();
            Server2DeleteStatus = "Interruzione dell'eliminazione...";
        }
    }

    private sealed record DeleteOutcome(int Deleted, List<string> Errors, bool Stopped);

    /// <summary>Elimina gli elementi uno dopo l'altro; lo stop ferma anche una cartella a metà.</summary>
    private async Task<DeleteOutcome> DeleteEntriesAsync(ISftpService service, SftpConfig server,
        IReadOnlyList<SftpRemoteEntry> list, Action<string> setStatus, Func<SftpRemoteEntry, Task> afterEach,
        CancellationToken ct)
    {
        int deleted = 0, done = 0;
        var errors = new List<string>();
        foreach (var entry in list)
        {
            if (ct.IsCancellationRequested) return new DeleteOutcome(deleted, errors, true);
            var head = $"Eliminazione {++done}/{list.Count} · {entry.Name}";
            setStatus(head);
            try
            {
                if (entry.IsDirectory)
                {
                    var progress = new Progress<string>(p => setStatus($"{head} ({p})"));
                    await RemoteDeleter.DeleteDirectoryAsync(server, entry.FullPath, progress, ct);
                    // Chi segue questa connessione (browser, albero di Videobox) si riallinea
                    service.NotifyRemoteChanged(new SftpRemoteChange(TransferJobBase.GetRemoteParent(entry.FullPath)));
                }
                else
                {
                    await service.DeleteFileAsync(entry.FullPath, ct);
                }
                deleted++;
                await afterEach(entry);
            }
            catch (OperationCanceledException)
            {
                return new DeleteOutcome(deleted, errors, true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[SFTP] Eliminazione non riuscita di {Path}: {Msg}", entry.FullPath, ex.Message);
                errors.Add($"{entry.Name}: {ex.Message}");
            }
        }
        return new DeleteOutcome(deleted, errors, ct.IsCancellationRequested);
    }

    private static string DeleteSummary(DeleteOutcome r, int total, string where)
    {
        var what = r.Deleted == 1 ? "1 elemento eliminato" : $"{r.Deleted} elementi eliminati";
        if (r.Stopped) return $"Eliminazione fermata: {what} {where} su {total}.";
        if (r.Errors.Count == 0) return r.Deleted == 1 ? $"Elemento eliminato {where}." : $"{r.Deleted} elementi eliminati {where}.";
        if (r.Deleted == 0) return $"Eliminazione fallita: {r.Errors[0]}";
        return $"Eliminati {r.Deleted}, falliti {r.Errors.Count}. Primo errore: {r.Errors[0]}";
    }

    public async Task SftpRemoteDeleteManyAsync(IEnumerable<SftpRemoteEntry> entries)
    {
        if (!IsRemoteConnected || RemoteConfig == null) { SftpStatusMessage = "Nessun server aperto: connettine uno nel pannello di destra."; return; }
        if (IsDeletingRemote) { SftpStatusMessage = "Un'eliminazione è già in corso: attendi o fermala."; return; }

        var list = entries.Where(e => e != null).ToList();
        if (list.Count == 0) return;

        var service = Remote;
        var server = RemoteConfig;
        _remoteDeleteCts = new CancellationTokenSource();
        IsDeletingRemote = true;
        DeleteOutcome result;
        try
        {
            result = await DeleteEntriesAsync(service, server, list, s => RemoteDeleteStatus = s, async entry =>
            {
                InvalidateZeroByteCacheRecursive(TransferJobBase.GetRemoteParent(entry.FullPath));
                await WebStreamDeletedAsync(entry.FullPath);
            }, _remoteDeleteCts.Token);
        }
        finally
        {
            IsDeletingRemote = false;
            RemoteDeleteStatus = string.Empty;
            _remoteDeleteCts.Dispose();
            _remoteDeleteCts = null;
        }

        await SftpRefreshInternalAsync(silent: true);
        InvalidateZeroByteCacheRecursive(SftpCurrentRemotePath);
        ServerChanged(server);
        SftpStatusMessage = DeleteSummary(result, list.Count, "dal server");
    }

    public async Task Server2DeleteManyAsync(IReadOnlyList<SftpRemoteEntry> entries)
    {
        if (!IsServer2Connected || _server2Config == null || entries.Count == 0) return;
        if (IsDeletingServer2) { SftpStatusMessage = "Un'eliminazione è già in corso: attendi o fermala."; return; }

        var service = _server2;
        var server = _server2Config;
        _server2DeleteCts = new CancellationTokenSource();
        IsDeletingServer2 = true;
        DeleteOutcome result;
        try
        {
            result = await DeleteEntriesAsync(service, server, entries, s => Server2DeleteStatus = s,
                entry => WebStreamDeletedAsync(entry.FullPath, server), _server2DeleteCts.Token);
        }
        finally
        {
            IsDeletingServer2 = false;
            Server2DeleteStatus = string.Empty;
            _server2DeleteCts.Dispose();
            _server2DeleteCts = null;
        }

        _relayQueue.ForgetCreatedFolders();
        await Server2RefreshInternalAsync(silent: true);
        ServerChanged(server);
        SftpStatusMessage = DeleteSummary(result, entries.Count, $"da '{server.Name}'");
    }
}
