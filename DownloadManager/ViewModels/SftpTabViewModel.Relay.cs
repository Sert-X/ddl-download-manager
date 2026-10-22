using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Helpers;
using DownloadManager.Models;
using DownloadManager.Services.Sftp;
using Microsoft.Extensions.Logging;

namespace DownloadManager.ViewModels;

/// <summary>
/// Secondo server SFTP nel pannello di sinistra e copie da server a server.
/// Il pannello di sinistra mostra i file locali oppure un secondo server (connessione
/// propria, indipendente da quella principale); i file si copiano tra i due server
/// con la coda "Server ↔ Server".
/// </summary>
public partial class SftpTabViewModel
{
    private SftpRelayQueueService _relayQueue = null!;
    private SftpWinScpService _server2 = null!;
    private SftpConfig? _server2Config;
    private DispatcherTimer _mainRefreshDebounce = null!;
    private DispatcherTimer _server2RefreshDebounce = null!;
    private readonly SemaphoreSlim _server2RefreshLock = new(1, 1);
    private string? _lastListedServer2Path;

    /// <summary>Pannello di sinistra: false = file locali, true = secondo server.</summary>
    [ObservableProperty] private bool _isServer2Mode;
    [ObservableProperty] private SftpConfig? _server2Profile;
    [ObservableProperty] private bool _isServer2Connected;
    [ObservableProperty] private bool _isServer2Busy;
    [ObservableProperty] private string _server2Path = "/";
    [ObservableProperty] private string _server2Stats = "";
    [ObservableProperty] private SftpRemoteEntry? _selectedServer2Entry;
    [ObservableProperty] private int _sftpMaxRelayConcurrency = 3;
    [ObservableProperty] private string _relayQueueStats = "";
    [ObservableProperty] private bool _hasActiveRelays;

    public ObservableCollection<SftpRemoteEntry> Server2Entries { get; } = new();
    public ObservableCollection<SftpRemoteEntry> SelectedServer2Entries { get; } = new();
    public ObservableCollection<SftpRelayJob> SftpRelayJobs => _relayQueue.AllJobs;

    public string Server2Name => _server2Config?.Name ?? "Secondo server";

    /// <summary>Connessione del secondo server (per la scelta della cartella in "Sposta in...").</summary>
    public ISftpService Server2Service => _server2;
    public bool IsLocalMode => !IsServer2Mode;
    public bool ShowServer2Connect => IsServer2Mode && !IsServer2Connected;
    public bool ShowServer2Browser => IsServer2Mode && IsServer2Connected;

    private void InitRelay(SftpRelayQueueService relayQueue, ILoggerFactory loggerFactory)
    {
        _relayQueue = relayQueue;
        _relayQueue.MaxConcurrency = SftpMaxRelayConcurrency;
        _relayQueue.QueueChanged += () => Dispatcher.UIThread.Post(UpdateSftpBadges);
        _relayQueue.JobCompleted += OnRelayJobCompleted;
        _sftpUploadQueue.JobCompleted += job =>
        {
            if (IsInOrUnder(job.DestinationFolder, SftpCurrentRemotePath)) ScheduleMainRefresh();
        };

        // Connessione propria: non toglie slot né blocca quella principale
        _server2 = new SftpWinScpService(loggerFactory.CreateLogger<SftpWinScpService>(), new SftpSessionLimiter(2));
        _server2.RemoteChanged += change =>
        {
            if (SamePath(change.DirectoryPath, Server2Path) || change.Recursive) ScheduleServer2Refresh();
        };

        _mainRefreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _mainRefreshDebounce.Tick += async (_, _) =>
        {
            _mainRefreshDebounce.Stop();
            try { await SftpRefreshInternalAsync(silent: true); } catch { }
        };
        _server2RefreshDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _server2RefreshDebounce.Tick += async (_, _) =>
        {
            _server2RefreshDebounce.Stop();
            try { await Server2RefreshInternalAsync(silent: true); } catch { }
        };
    }

    partial void OnIsServer2ModeChanged(bool value)
    {
        if (value && Server2Profile == null)
            Server2Profile = SftpProfiles.FirstOrDefault(p => p.Id != SelectedSftpProfile?.Id) ?? SftpProfiles.FirstOrDefault();
        NotifyServer2Layout();
    }

    partial void OnIsServer2ConnectedChanged(bool value) => NotifyServer2Layout();

    private void NotifyServer2Layout()
    {
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(ShowServer2Connect));
        OnPropertyChanged(nameof(ShowServer2Browser));
        OnPropertyChanged(nameof(Server2Name));
    }

    partial void OnSftpMaxRelayConcurrencyChanged(int value)
    {
        if (_relayQueue != null) _relayQueue.MaxConcurrency = value;
    }

    [RelayCommand] private void ShowLocalPane() => IsServer2Mode = false;
    [RelayCommand] private void ShowServer2Pane() => IsServer2Mode = true;

    // ============================================================
    //  CONNESSIONE AL SECONDO SERVER
    // ============================================================

    [RelayCommand]
    private async Task Server2ConnectAsync()
    {
        var profile = Server2Profile;
        if (profile == null) { SftpStatusMessage = "Scegli il profilo del secondo server."; return; }

        try
        {
            IsServer2Busy = true;
            SftpStatusMessage = $"Connessione a '{profile.Name}'...";
            await _server2.ConnectAsync(profile);
            _server2Config = profile;
            IsServer2Connected = true;

            var start = string.IsNullOrWhiteSpace(profile.RemoteBasePath) || profile.RemoteBasePath == "/"
                ? _server2.GetWorkingDirectory()
                : profile.RemoteBasePath;
            Server2Path = string.IsNullOrWhiteSpace(start) ? "/" : start;
            _logger.LogInformation("[SFTP-2] Connesso al secondo server '{Name}' ({Host})", profile.Name, profile.Host);
            await Server2RefreshInternalAsync(silent: false);
        }
        catch (Exception ex)
        {
            IsServer2Connected = false;
            SftpStatusMessage = $"Connessione a '{profile.Name}' fallita: {ex.Message}";
            _logger.LogWarning(ex, "[SFTP-2] Connessione fallita");
        }
        finally { IsServer2Busy = false; }
    }

    [RelayCommand]
    private async Task Server2DisconnectAsync()
    {
        try { await _server2.DisconnectAsync(); } catch { }
        IsServer2Connected = false;
        Server2Entries.Clear();
        Server2Stats = "";
        SftpStatusMessage = $"Disconnesso da '{Server2Name}'.";
    }

    // ============================================================
    //  NAVIGAZIONE
    // ============================================================

    [RelayCommand] private Task Server2RefreshAsync() => Server2RefreshInternalAsync(silent: false);

    [RelayCommand]
    private async Task Server2GoUpAsync()
    {
        if (!IsServer2Connected) return;
        Server2Path = TransferJobBase.GetRemoteParent(Server2Path);
        await Server2RefreshInternalAsync(silent: false);
    }

    [RelayCommand]
    private async Task Server2GoHomeAsync()
    {
        if (!IsServer2Connected) return;
        Server2Path = _server2.GetWorkingDirectory();
        await Server2RefreshInternalAsync(silent: false);
    }

    public async Task Server2OpenAsync(SftpRemoteEntry? entry)
    {
        if (entry is not { IsDirectory: true } || !IsServer2Connected) return;
        Server2Path = entry.FullPath;
        await Server2RefreshInternalAsync(silent: false);
    }

    // Aggiornamento "a intervalli": il primo evento fa partire il timer, quelli che arrivano
    // prima che scatti vengono raccolti nella stessa lettura. Con 100 upload che finiscono
    // uno dopo l'altro la cartella si rilegge al massimo una volta ogni intervallo (e non
    // resta mai indietro, come succederebbe facendo ripartire il timer a ogni evento).
    private void ScheduleServer2Refresh() => Dispatcher.UIThread.Post(() =>
    {
        if (!_server2RefreshDebounce.IsEnabled) _server2RefreshDebounce.Start();
    });

    private void ScheduleMainRefresh() => Dispatcher.UIThread.Post(() =>
    {
        if (!_mainRefreshDebounce.IsEnabled) _mainRefreshDebounce.Start();
    });

    /// <summary>Il file è finito in una cartella aperta (o in una sua sottocartella, per le cartelle nuove)?</summary>
    private static bool IsInOrUnder(string folder, string shownFolder)
    {
        var f = (folder ?? "").TrimEnd('/');
        var s = (shownFolder ?? "").TrimEnd('/');
        return f == s || f.StartsWith(s + "/", StringComparison.Ordinal);
    }

    private async Task Server2RefreshInternalAsync(bool silent)
    {
        if (!IsServer2Connected) return;

        await _server2RefreshLock.WaitAsync();
        try
        {
            var path = string.IsNullOrWhiteSpace(Server2Path) ? "/" : Server2Path;
            if (!silent) { IsServer2Busy = true; SftpStatusMessage = $"Lettura {Server2Name}: {path}..."; }

            var entries = await _server2.ListDirectoryAsync(path);
            if (!SamePath(Server2Path, path)) return;

            // Modifiche fatte da fuori: l'albero di Videobox si riallinea (come per il server principale)
            bool samePath = SamePath(_lastListedServer2Path, path);
            bool differs = !samePath || !SameListing(Server2Entries, entries);

            foreach (var e in entries) e.HasZeroByteIssue = !e.IsDirectory && e.Size == 0;
            var sorted = SftpEntrySorter.SortRemoteEntries(entries, RemoteSortOption.Mode, RemoteSortAscending);
            SftpEntrySorter.SyncCollection(Server2Entries, sorted, e => e.FullPath, SftpEntrySorter.UpdateRemoteEntryFromSource);
            _lastListedServer2Path = path;
            if (!silent || (samePath && differs)) _server2.NotifyRemoteChanged(new SftpRemoteChange(path));

            int dirs = Server2Entries.Count(e => e.IsDirectory);
            Server2Stats = Server2Entries.Count == 0
                ? "Cartella vuota"
                : $"{dirs} cartelle, {Server2Entries.Count - dirs} file ({Server2Entries.Count} totali)";
            if (!silent) SftpStatusMessage = $"{entries.Count} elementi in {Server2Name}: {path}";
        }
        catch (Exception ex)
        {
            if (!silent) SftpStatusMessage = $"Errore su '{Server2Name}': {ex.Message}";
            if (!_server2.IsConnected) IsServer2Connected = false;
        }
        finally
        {
            if (!silent) IsServer2Busy = false;
            _server2RefreshLock.Release();
        }
    }

    // ============================================================
    //  GESTIONE FILE SUL SECONDO SERVER
    // ============================================================

    public async Task Server2CreateFolderAsync(string name)
    {
        if (!IsServer2Connected || string.IsNullOrWhiteSpace(name)) return;
        try
        {
            SftpStatusMessage = $"Creazione della cartella '{name.Trim()}'...";
            await _server2.CreateDirectoryAsync($"{Server2Path.TrimEnd('/')}/{name.Trim()}");
            SftpStatusMessage = $"Cartella creata su '{Server2Name}': {name}";
            await Server2RefreshInternalAsync(silent: true);
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    public void PlayServer2File(SftpRemoteEntry entry)
    {
        if (!IsServer2Connected || _server2Config == null) return;
        PlayRemoteFile(entry, _server2Config);
    }

    public async Task Server2RenameAsync(SftpRemoteEntry entry, string newName)
    {
        if (!IsServer2Connected) return;
        if (Server2Entries.Any(e => string.Equals(e.Name, newName.Trim(), StringComparison.Ordinal)))
        { SftpStatusMessage = $"Esiste già un elemento chiamato '{newName}'."; return; }
        try
        {
            SftpStatusMessage = $"Rinomina in corso: {entry.Name} → {newName.Trim()}";
            var target = $"{TransferJobBase.GetRemoteParent(entry.FullPath).TrimEnd('/')}/{newName.Trim()}";
            await _server2.RenameAsync(entry.FullPath, target);
            SftpStatusMessage = $"Rinominato in: {newName}";
            await Server2RefreshInternalAsync(silent: true);
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    public async Task Server2MoveManyAsync(IReadOnlyList<SftpRemoteEntry> entries, string destinationFolder)
    {
        if (!IsServer2Connected || entries.Count == 0 || string.IsNullOrWhiteSpace(destinationFolder)) return;

        var dest = destinationFolder.Replace('\\', '/').TrimEnd('/');
        int moved = 0, done = 0;
        var errors = new List<string>();
        IsServer2Busy = true;
        foreach (var entry in entries)
        {
            SftpStatusMessage = $"Spostamento in corso su '{Server2Name}' ({++done}/{entries.Count}): {entry.Name}";
            try
            {
                var target = $"{dest}/{entry.Name}";
                if (target == entry.FullPath) continue;
                if (entry.IsDirectory && (dest + "/").StartsWith(entry.FullPath.TrimEnd('/') + "/", StringComparison.Ordinal))
                    throw new InvalidOperationException("non si può spostare una cartella dentro sé stessa");
                await _server2.RenameAsync(entry.FullPath, target);
                moved++;
            }
            catch (Exception ex) { errors.Add($"{entry.Name}: {ex.Message}"); }
        }
        IsServer2Busy = false;
        _relayQueue.ForgetCreatedFolders();
        SftpStatusMessage = errors.Count == 0
            ? $"Spostati {moved} elementi in {(dest.Length == 0 ? "/" : dest)}"
            : $"Spostati {moved} elementi, {errors.Count} errori: {string.Join("; ", errors.Take(3))}";
        await Server2RefreshInternalAsync(silent: true);
    }

    public async Task Server2DuplicateAsync(SftpRemoteEntry entry, string newName)
    {
        if (!IsServer2Connected) return;
        if (Server2Entries.Any(e => string.Equals(e.Name, newName.Trim(), StringComparison.Ordinal)))
        { SftpStatusMessage = $"Esiste già un elemento chiamato '{newName}'."; return; }

        try
        {
            IsServer2Busy = true;
            SftpStatusMessage = $"Duplicazione di '{entry.Name}' su '{Server2Name}'...";
            var target = $"{TransferJobBase.GetRemoteParent(entry.FullPath).TrimEnd('/')}/{newName.Trim()}";
            await _server2.DuplicateAsync(entry.FullPath, target);
            SftpStatusMessage = $"Creata la copia: {newName}";
            await Server2RefreshInternalAsync(silent: true);
        }
        catch (Exception ex) { SftpStatusMessage = $"Duplicazione non riuscita: {ex.Message}"; }
        finally { IsServer2Busy = false; }
    }

    public async Task Server2DeleteManyAsync(IReadOnlyList<SftpRemoteEntry> entries)
    {
        if (!IsServer2Connected || entries.Count == 0) return;

        int ok = 0, done = 0;
        var errors = new List<string>();
        IsServer2Busy = true;
        foreach (var entry in entries)
        {
            SftpStatusMessage = $"Eliminazione in corso su '{Server2Name}' ({++done}/{entries.Count}): {entry.Name}";
            try
            {
                if (entry.IsDirectory) await _server2.DeleteDirectoryAsync(entry.FullPath);
                else await _server2.DeleteFileAsync(entry.FullPath);
                ok++;
            }
            catch (Exception ex) { errors.Add($"{entry.Name}: {ex.Message}"); }
        }
        IsServer2Busy = false;
        _relayQueue.ForgetCreatedFolders();
        await Server2RefreshInternalAsync(silent: true);

        SftpStatusMessage = errors.Count == 0
            ? $"{ok} elementi eliminati da '{Server2Name}'."
            : $"Eliminati {ok}, falliti {errors.Count}. Primo errore: {errors[0]}";
    }

    // ============================================================
    //  COPIA TRA SERVER
    // ============================================================

    /// <summary>Dal secondo server (sinistra) alla cartella aperta del server principale (destra).</summary>
    [RelayCommand]
    private async Task CopyServer2ToMainAsync()
    {
        if (!IsSftpConnected || !IsServer2Connected || _server2Config == null)
        { SftpStatusMessage = "Collega entrambi i server."; return; }

        var selected = SelectedServer2Entries.Count > 0
            ? SelectedServer2Entries.ToList()
            : SelectedServer2Entry != null ? new List<SftpRemoteEntry> { SelectedServer2Entry } : new();

        await EnqueueRelayAsync(selected, _server2, _server2Config, BuildMainConfigForRelay(), SftpCurrentRemotePath);
    }

    /// <summary>Dal server principale (destra) alla cartella aperta del secondo server (sinistra).</summary>
    [RelayCommand]
    private async Task CopyMainToServer2Async()
    {
        if (!IsSftpConnected || !IsServer2Connected || _server2Config == null)
        { SftpStatusMessage = "Collega entrambi i server."; return; }

        var selected = SelectedRemoteEntries.Count > 0
            ? SelectedRemoteEntries.ToList()
            : SftpSelectedRemoteEntry != null ? new List<SftpRemoteEntry> { SftpSelectedRemoteEntry } : new();

        await EnqueueRelayAsync(selected, _sftpService, BuildMainConfigForRelay(), _server2Config, Server2Path);
    }

    private SftpConfig BuildMainConfigForRelay()
    {
        var config = BuildCurrentSftpConfig();
        if (string.IsNullOrWhiteSpace(config.Name)) config.Name = "Server principale";
        return config;
    }

    private async Task EnqueueRelayAsync(List<SftpRemoteEntry> entries, ISftpService sourceService,
                                         SftpConfig source, SftpConfig destination, string destinationDir)
    {
        if (entries.Count == 0) { SftpStatusMessage = "Seleziona almeno un file o una cartella da copiare."; return; }
        if (string.IsNullOrWhiteSpace(destinationDir)) destinationDir = "/";

        try
        {
            SftpStatusMessage = "Preparazione della copia...";
            _relayQueue.MaxConcurrency = SftpMaxRelayConcurrency;

            int queued = 0;
            foreach (var entry in entries)
            {
                var target = $"{destinationDir.TrimEnd('/')}/{entry.Name}";
                if (entry.IsDirectory)
                    queued += await EnqueueRelayFolderAsync(sourceService, source, destination, entry.FullPath, target);
                else
                {
                    await _relayQueue.EnqueueJobAsync(NewRelayJob(source, destination, entry.FullPath, target, entry.Size));
                    queued++;
                }
            }

            SftpStatusMessage = queued == 0
                ? "Nessun file da copiare (cartelle vuote)."
                : $"{queued} file accodati: {source.Name} → {destination.Name} (vedi \"Server ↔ Server\").";
            _logger.LogInformation("[SFTP-2] {Count} file accodati da {From} a {To}:{Dir}", queued, source.Name, destination.Name, destinationDir);
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore accodamento copia: {ex.Message}"; }
    }

    private async Task<int> EnqueueRelayFolderAsync(ISftpService sourceService, SftpConfig source, SftpConfig destination,
                                                    string sourceFolder, string destinationFolder)
    {
        int count = 0;
        foreach (var entry in await sourceService.ListDirectoryAsync(sourceFolder))
        {
            var target = $"{destinationFolder.TrimEnd('/')}/{entry.Name}";
            if (entry.IsDirectory)
                count += await EnqueueRelayFolderAsync(sourceService, source, destination, entry.FullPath, target);
            else
            {
                await _relayQueue.EnqueueJobAsync(NewRelayJob(source, destination, entry.FullPath, target, entry.Size));
                count++;
            }
        }
        return count;
    }

    private static SftpRelayJob NewRelayJob(SftpConfig source, SftpConfig destination, string sourcePath, string destinationPath, long size) => new()
    {
        SourceConfig = source,
        DestinationConfig = destination,
        SourcePath = sourcePath,
        DestinationPath = destinationPath,
        TotalBytes = size,
        Status = SftpJobStatus.Pending
    };

    /// <summary>Il file è arrivato: si aggiorna il pannello del server di destinazione (e l'albero di Videobox).</summary>
    private void OnRelayJobCompleted(SftpRelayJob job)
    {
        var folder = TransferJobBase.GetRemoteParent(job.DestinationPath);
        bool toServer2 = _server2Config != null && SameServer(job.DestinationConfig, _server2Config);
        bool toMain = SameServer(job.DestinationConfig, BuildCurrentSftpConfig());

        SftpChangeHub.Publish(job.DestinationConfig.ServerKey, new SftpRemoteChange(folder));
        if (toMain && IsInOrUnder(folder, SftpCurrentRemotePath)) ScheduleMainRefresh();
        if (toServer2 && IsInOrUnder(folder, Server2Path)) ScheduleServer2Refresh();
    }

    private static bool SameServer(SftpConfig a, SftpConfig b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port &&
        string.Equals(a.Username, b.Username, StringComparison.Ordinal);

    private static bool SamePath(string? a, string? b) =>
        string.Equals((a ?? "").TrimEnd('/'), (b ?? "").TrimEnd('/'), StringComparison.Ordinal);

    // ============================================================
    //  CODA SERVER ↔ SERVER
    // ============================================================

    private void UpdateRelayQueueStats()
    {
        var jobs = _relayQueue.AllJobs;
        HasActiveRelays = jobs.Any(j => j.Status is SftpJobStatus.Transferring or SftpJobStatus.Pending);
        if (jobs.Count == 0) { RelayQueueStats = ""; return; }

        var parts = new List<string> { $"{jobs.Count} totali" };
        void Add(SftpJobStatus s, string label) { int n = jobs.Count(j => j.Status == s); if (n > 0) parts.Add($"{n} {label}"); }
        Add(SftpJobStatus.Transferring, "in corso");
        Add(SftpJobStatus.Pending, "in attesa");
        Add(SftpJobStatus.Paused, "in pausa");
        Add(SftpJobStatus.Completed, "completati");
        Add(SftpJobStatus.Failed, "falliti");
        Add(SftpJobStatus.Cancelled, "annullati");
        RelayQueueStats = string.Join(" · ", parts);
    }

    [RelayCommand] private void RelayQueueStartAll() { _relayQueue.StartAll(); SftpStatusMessage = "Copie tra server avviate."; }
    [RelayCommand] private void RelayQueuePauseAll() { _relayQueue.PauseAll(); SftpStatusMessage = "Copie tra server in pausa."; }
    [RelayCommand] private void RelayQueueCancelAll() { _relayQueue.CancelAll(); SftpStatusMessage = "Copie tra server annullate."; }
    [RelayCommand] private void RelayQueueClearCompleted() => _relayQueue.ClearCompleted();
    [RelayCommand] private void RelayQueueClearCancelled() => _relayQueue.ClearCancelled();
    [RelayCommand] private void RelayQueueClearFailed() => _relayQueue.ClearFailed();

    public void PauseRelayJob(SftpRelayJob job) => _relayQueue.PauseJob(job);
    public void ResumeRelayJob(SftpRelayJob job) => _relayQueue.ResumeJob(job);
    public void CancelRelayJob(SftpRelayJob job) => _relayQueue.CancelJob(job);
    public void RemoveRelayJob(SftpRelayJob job) => _relayQueue.RemoveJob(job);

    private void DisposeRelay()
    {
        try { _relayQueue.CancelAll(); } catch { }
        try { _server2.DisconnectAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _server2.Dispose(); } catch { }
    }
}
