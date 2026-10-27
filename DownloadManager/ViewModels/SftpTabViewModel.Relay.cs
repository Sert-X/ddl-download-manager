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
/// Pannello di sinistra (Locale + schede di server) e copie da server a server.
/// Il pannello di sinistra mostra i file locali oppure uno dei server aperti nelle sue schede
/// (ognuno con la sua connessione). I nomi "Server2..." indicano la scheda aperta a sinistra.
/// I file si copiano tra la scheda di sinistra e quella di destra con la coda "Server ↔ Server".
/// </summary>
public partial class SftpTabViewModel
{
    private SftpRelayQueueService _relayQueue = null!;
    /// <summary>Server aperti nel pannello di sinistra, uno per scheda.</summary>
    public ObservableCollection<RemoteServerTab> LeftTabs { get; } = new();
    [ObservableProperty] private RemoteServerTab? _activeLeftTab;
    /// <summary>Riquadro "connetti un server" aperto a sinistra con il pulsante "+".</summary>
    [ObservableProperty] private bool _isLeftAdding;

    /// <summary>Connessione della scheda aperta a sinistra (usata solo dopo aver controllato IsServer2Connected).</summary>
    private ISftpService _server2 => ActiveLeftTab!.Service;
    private SftpConfig? _server2Config => ActiveLeftTab?.Config;
    private DispatcherTimer _mainRefreshDebounce = null!;
    private DispatcherTimer _server2RefreshDebounce = null!;
    private readonly SemaphoreSlim _server2RefreshLock = new(1, 1);
    private string? _lastListedServer2Path;

    /// <summary>Pannello di sinistra: false = file locali, true = secondo server.</summary>
    [ObservableProperty] private bool _isServer2Mode;
    [ObservableProperty] private SftpConfig? _server2Profile;
    [ObservableProperty] private bool _isServer2Busy;
    [ObservableProperty] private string _server2Path = "/";
    [ObservableProperty] private string _server2Stats = "";
    [ObservableProperty] private SftpRemoteEntry? _selectedServer2Entry;
    [ObservableProperty] private int _sftpMaxRelayConcurrency = 3;
    [ObservableProperty] private string _relayQueueStats = "";
    [ObservableProperty] private bool _hasActiveRelays;

    public bool IsServer2Connected => ActiveLeftTab != null && ActiveLeftTab.Service.IsConnected;
    public bool HasLeftTabs => LeftTabs.Count > 0;

    // Controllo dei file a 0 byte della scheda di sinistra (lo stesso della destra se il server
    // è aperto anche lì: risultati condivisi)
    private ZeroByteCheckerService? _server2Checker => ActiveLeftTab?.Checker;
    /// <summary>La scheda ricorda la sua cartella.</summary>
    partial void OnServer2PathChanged(string value) { if (ActiveLeftTab != null) ActiveLeftTab.Path = value; }
    [ObservableProperty] private bool _isServer2CheckingZeroByte;
    [ObservableProperty] private string _server2ZeroByteStatus = "";
    [ObservableProperty] private bool _isServer2Reading;

    public string Server2StatusText => IsServer2CheckingZeroByte
        ? (string.IsNullOrEmpty(Server2ZeroByteStatus) ? "Avvio check 0-byte in corso..." : Server2ZeroByteStatus)
        : IsServer2Reading ? "Lettura cartella in corso..." : "";
    public bool HasServer2Status => Server2StatusText.Length > 0;
    partial void OnIsServer2CheckingZeroByteChanged(bool value) => NotifyServer2Status();
    partial void OnServer2ZeroByteStatusChanged(string value) => NotifyServer2Status();
    partial void OnIsServer2ReadingChanged(bool value) => NotifyServer2Status();
    private void NotifyServer2Status()
    {
        OnPropertyChanged(nameof(Server2StatusText));
        OnPropertyChanged(nameof(HasServer2Status));
    }

    private void SyncServer2ZeroByteState()
    {
        var checker = _server2Checker;
        IsServer2CheckingZeroByte = checker?.IsChecking == true;
        Server2ZeroByteStatus = checker?.IsChecking == true ? checker.ProgressStatus : "";
    }

    [RelayCommand]
    private void CheckServer2ZeroByte()
    {
        if (!IsServer2Connected || _server2Checker == null) { SftpStatusMessage = "Collega prima il secondo server."; return; }
        if (_server2Checker.IsChecking) { SftpStatusMessage = $"Un check 0-byte su '{Server2Name}' è già in corso."; return; }
        var folders = Server2Entries.Where(e => e.IsDirectory).ToList();
        if (folders.Count == 0) { SftpStatusMessage = "Nessuna cartella da controllare nella cartella corrente."; return; }
        _server2Checker.UseConnection(_server2, () => _server2.IsConnected);
        SftpStatusMessage = $"Check di {folders.Count} cartelle su '{Server2Name}' in corso...";
        _ = _server2Checker.RunCheckAsync(folders);
    }

    [RelayCommand] private void StopServer2ZeroByte() => _server2Checker?.Stop();

    /// <summary>Clic su un profilo nel riquadro del secondo server: si collega subito.</summary>
    [RelayCommand]
    private async Task Server2ConnectProfileAsync(SftpConfig? profile)
    {
        if (profile == null || IsServer2Busy) return;
        Server2Profile = profile;
        await Server2ConnectAsync();
    }

    // Ordinamento della tabella di sinistra (come a destra, ma indipendente)
    [ObservableProperty] private SortOption _server2SortOption = null!;
    [ObservableProperty] private bool _server2SortAscending = true;
    partial void OnServer2SortOptionChanged(SortOption value) { if (value != null) ReorderServer2Entries(); }
    partial void OnServer2SortAscendingChanged(bool value) => ReorderServer2Entries();
    [RelayCommand] private void ToggleServer2SortDirection() => Server2SortAscending = !Server2SortAscending;

    private void ReorderServer2Entries()
    {
        if (Server2SortOption == null || Server2Entries.Count == 0) return;
        var sorted = SftpEntrySorter.SortRemoteEntries(Server2Entries.ToList(), Server2SortOption.Mode, Server2SortAscending);
        var selected = SelectedServer2Entry;
        Server2Entries.Clear();
        foreach (var e in sorted) Server2Entries.Add(e);
        if (selected != null && Server2Entries.Contains(selected)) SelectedServer2Entry = selected;
    }

    public ObservableCollection<SftpRemoteEntry> Server2Entries { get; } = new();
    public ObservableCollection<SftpRemoteEntry> SelectedServer2Entries { get; } = new();
    public ObservableCollection<SftpRelayJob> SftpRelayJobs => _relayQueue.AllJobs;

    public string Server2Name => _server2Config?.Name ?? "Connetti un server";

    /// <summary>Connessione della scheda di sinistra (per la scelta della cartella in "Sposta in...").</summary>
    public ISftpService Server2Service => ActiveLeftTab?.Service ?? _sftpService;
    public bool IsLocalMode => !IsServer2Mode;
    public bool ShowServer2Connect => IsServer2Mode && (ActiveLeftTab == null || IsLeftAdding);
    public bool ShowServer2Browser => IsServer2Mode && !ShowServer2Connect;
    public bool CanCancelLeftAdd => IsLeftAdding && ActiveLeftTab != null;

    private void InitRelay(SftpRelayQueueService relayQueue, ILoggerFactory loggerFactory)
    {
        _relayQueue = relayQueue;
        _relayQueue.MaxConcurrency = SftpMaxRelayConcurrency;
        _relayQueue.QueueChanged += () => Dispatcher.UIThread.Post(UpdateSftpBadges);
        _relayQueue.JobCompleted += OnRelayJobCompleted;
        // File arrivato con un upload: si aggiornano i pannelli (destra e sinistra) che mostrano
        // quella cartella di quel server
        _sftpUploadQueue.JobCompleted += job => RefreshPanesShowing(job.Server ?? _mainConfig, job.DestinationFolder);

        // Modifiche fatte da QUALSIASI connessione dell'app (schede di destra e di sinistra,
        // Organizza file, copie...): si aggiornano tutti i pannelli che mostrano quella cartella
        // di quel server. Prima ogni pannello sentiva solo la sua connessione: con lo stesso
        // server aperto a destra e a sinistra, l'altro pannello restava indietro.
        SftpChangeHub.Changed += OnServerChanged;

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

    private void OnServerChanged(string serverKey, SftpRemoteChange change) => Dispatcher.UIThread.Post(() =>
    {
        bool Shows(SftpConfig? server, string path) =>
            server != null && string.Equals(server.ServerKey, serverKey, StringComparison.OrdinalIgnoreCase) &&
            (SamePath(change.DirectoryPath, path) || change.Recursive);
        if (ActiveRemoteTab != null && Shows(RemoteConfig, SftpCurrentRemotePath)) ScheduleMainRefresh();
        if (IsServer2Mode && IsServer2Connected && Shows(_server2Config, Server2Path)) ScheduleServer2Refresh();
    });

    /// <summary>Aggiorna i pannelli che mostrano quella cartella (o una superiore) di quel server.</summary>
    private void RefreshPanesShowing(SftpConfig? server, string folder) => Dispatcher.UIThread.Post(() =>
    {
        if (server == null) return;
        if (IsShownInActiveTab(server, folder)) ScheduleMainRefresh();
        if (IsServer2Mode && IsServer2Connected && _server2Config != null &&
            string.Equals(_server2Config.ServerKey, server.ServerKey, StringComparison.OrdinalIgnoreCase) &&
            IsInOrUnder(folder, Server2Path))
            ScheduleServer2Refresh();
    });

    partial void OnIsServer2ModeChanged(bool value) { UpdateLeftTabHighlight(); NotifyServer2Layout(); }
    partial void OnIsLeftAddingChanged(bool value)
    {
        if (value)
            Server2Profile = SftpProfiles.FirstOrDefault(p => LeftTabs.All(t => t.Config.ServerKey != p.ServerKey)) ?? SftpProfiles.FirstOrDefault();
        UpdateLeftTabHighlight();
        NotifyServer2Layout();
    }

    /// <summary>La scheda di sinistra è evidenziata solo quando il pannello mostra quel server.</summary>
    private void UpdateLeftTabHighlight()
    {
        foreach (var t in LeftTabs) t.IsActive = ReferenceEquals(t, ActiveLeftTab) && IsServer2Mode && !IsLeftAdding;
    }

    partial void OnActiveLeftTabChanged(RemoteServerTab? oldValue, RemoteServerTab? newValue)
    {
        Server2Entries.Clear();
        SelectedServer2Entries.Clear();
        Server2Stats = "";
        _lastListedServer2Path = null;
        IsLeftAdding = false;
        if (newValue != null) Server2Path = newValue.Path;
        UpdateLeftTabHighlight();
        SyncServer2ZeroByteState();
        NotifyServer2Layout();
        if (newValue != null && IsServer2Mode) _ = Server2RefreshInternalAsync(silent: false);
    }

    private void NotifyServer2Layout()
    {
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(ShowServer2Connect));
        OnPropertyChanged(nameof(ShowServer2Browser));
        OnPropertyChanged(nameof(Server2Name));
        OnPropertyChanged(nameof(IsServer2Connected));
        OnPropertyChanged(nameof(CanCancelLeftAdd));
        OnPropertyChanged(nameof(HasLeftTabs));
        OnPropertyChanged(nameof(Server2Service));
    }

    partial void OnSftpMaxRelayConcurrencyChanged(int value)
    {
        if (_relayQueue != null) _relayQueue.MaxConcurrency = value;
    }

    [RelayCommand] private void ShowLocalPane() { IsServer2Mode = false; IsLeftAdding = false; }

    /// <summary>"+ Connetti server" a sinistra.</summary>
    [RelayCommand] private void ShowServer2Pane() { IsLeftAdding = true; IsServer2Mode = true; }

    [RelayCommand] private void CancelLeftAdd() { IsLeftAdding = false; if (ActiveLeftTab == null) IsServer2Mode = false; }

    [RelayCommand]
    private void SelectLeftTab(RemoteServerTab? tab)
    {
        if (tab == null) return;
        bool wasLocal = !IsServer2Mode;
        IsLeftAdding = false;
        IsServer2Mode = true;
        if (!ReferenceEquals(ActiveLeftTab, tab)) ActiveLeftTab = tab;
        else if (wasLocal) _ = Server2RefreshInternalAsync(silent: false);
    }

    // ============================================================
    //  CONNESSIONE AL SECONDO SERVER
    // ============================================================

    /// <summary>Apre a sinistra una scheda col profilo scelto (o passa a quella già aperta).</summary>
    [RelayCommand]
    private async Task Server2ConnectAsync()
    {
        var profile = Server2Profile;
        if (profile == null) { SftpStatusMessage = "Scegli il profilo del server da aprire."; return; }

        var existing = LeftTabs.FirstOrDefault(t => string.Equals(t.Config.ServerKey, profile.ServerKey, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectLeftTab(existing);
            SftpStatusMessage = $"'{profile.Name}' è già aperto a sinistra: passato alla sua scheda.";
            return;
        }

        SftpWinScpService? service = null;
        try
        {
            IsServer2Busy = true;
            SftpStatusMessage = $"Connessione a '{profile.Name}'...";
            // Connessione propria: non toglie posti né blocca le altre
            service = new SftpWinScpService(_loggerFactory.CreateLogger<SftpWinScpService>(), new SftpSessionLimiter(2));
            await service.ConnectAsync(profile);
            var start = string.IsNullOrWhiteSpace(profile.RemoteBasePath) || profile.RemoteBasePath == "/"
                ? service.GetWorkingDirectory()
                : profile.RemoteBasePath;
            var tab = new RemoteServerTab(profile, service, isMain: false)
            {
                Path = string.IsNullOrWhiteSpace(start) ? "/" : start,
                Checker = GetChecker(profile, service)
            };
            LeftTabs.Add(tab);
            _logger.LogInformation("[SFTP] Aperta a sinistra la scheda '{Name}' ({Host})", profile.Name, profile.Host);
            IsServer2Mode = true;
            ActiveLeftTab = tab;
            IsLeftAdding = false;
            SftpStatusMessage = $"Connesso a '{profile.Name}'.";
        }
        catch (Exception ex)
        {
            try { service?.Dispose(); } catch { }
            SftpStatusMessage = $"Connessione a '{profile.Name}' fallita: {ex.Message}";
            _logger.LogWarning(ex, "[SFTP] Connessione a sinistra fallita");
        }
        finally { IsServer2Busy = false; }
    }

    /// <summary>Disconnette la scheda aperta a sinistra (pulsante nella barra).</summary>
    [RelayCommand]
    private Task Server2DisconnectAsync() => CloseLeftTabAsync(ActiveLeftTab);

    [RelayCommand]
    private async Task CloseLeftTabAsync(RemoteServerTab? tab)
    {
        if (tab == null) return;
        int index = LeftTabs.IndexOf(tab);
        if (index < 0) return;

        RemoteServerTab? next = null;
        if (ReferenceEquals(tab, ActiveLeftTab))
            next = index + 1 < LeftTabs.Count ? LeftTabs[index + 1] : index > 0 ? LeftTabs[index - 1] : null;
        LeftTabs.Remove(tab);
        if (ReferenceEquals(tab, ActiveLeftTab))
        {
            ActiveLeftTab = next;
            if (next == null) IsServer2Mode = false;   // nessun server a sinistra: si torna ai file locali
        }
        if (!IsServerOpen(tab.Config.ServerKey)) tab.Checker?.Stop();

        try { await tab.Service.DisconnectAsync(); } catch { }
        try { (tab.Service as IDisposable)?.Dispose(); } catch { }
        _logger.LogInformation("[SFTP] Chiusa a sinistra la scheda '{Name}'", tab.Name);
        SftpStatusMessage = $"Disconnesso da '{tab.Name}'.";
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
            var tab = ActiveLeftTab;
            if (tab == null) return;
            var path = string.IsNullOrWhiteSpace(Server2Path) ? "/" : Server2Path;
            if (!silent) { IsServer2Busy = true; IsServer2Reading = true; SftpStatusMessage = $"Lettura {Server2Name}: {path}..."; }

            var entries = await tab.Service.ListDirectoryAsync(path);
            // Nel frattempo si è cambiata scheda o cartella: questa lettura non serve più
            if (!ReferenceEquals(tab, ActiveLeftTab) || !SamePath(Server2Path, path)) return;
            var checker = _server2Checker;
            checker?.UpdateFromListing(path, entries);

            // Modifiche fatte da fuori: l'albero di Videobox si riallinea (come per il server principale)
            bool samePath = SamePath(_lastListedServer2Path, path);
            bool differs = !samePath || !SameListing(Server2Entries, entries);

            foreach (var e in entries)
            {
                if (!e.IsDirectory) { e.HasZeroByteIssue = e.Size == 0; continue; }
                // Risultati del controllo 0 byte di questo server (anche se fatto dalla scheda di destra)
                var normalized = e.FullPath.TrimEnd('/');
                e.HasZeroByteIssue = checker != null && checker.TryGetFolderFlag(normalized, out var flag) && flag;
                if (checker != null && checker.TryGetFolderContents(normalized, out var info)) e.ContentsInfo = info;
            }
            var sorted = SftpEntrySorter.SortRemoteEntries(entries, Server2SortOption.Mode, Server2SortAscending);
            SftpEntrySorter.SyncCollection(Server2Entries, sorted, e => e.FullPath, SftpEntrySorter.UpdateRemoteEntryFromSource);
            AutoCheckNewFolders(checker, tab.Service, tab.Config, path, entries);
            _lastListedServer2Path = path;
            if (!silent || (samePath && differs)) tab.Service.NotifyRemoteChanged(new SftpRemoteChange(path));

            int dirs = Server2Entries.Count(e => e.IsDirectory);
            Server2Stats = Server2Entries.Count == 0
                ? "Cartella vuota"
                : $"{dirs} cartelle, {Server2Entries.Count - dirs} file ({Server2Entries.Count} totali)";
            if (!silent) SftpStatusMessage = $"{entries.Count} elementi in {Server2Name}: {path}";
        }
        catch (Exception ex)
        {
            if (!silent) SftpStatusMessage = $"Errore su '{Server2Name}': {ex.Message}";
            NotifyServer2Layout();
        }
        finally
        {
            if (!silent) { IsServer2Busy = false; IsServer2Reading = false; }
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

    /// <summary>Dal secondo server (sinistra) alla cartella aperta della scheda di destra.</summary>
    [RelayCommand]
    private async Task CopyServer2ToMainAsync()
    {
        if (!IsRemoteConnected || RemoteConfig == null || !IsServer2Connected || _server2Config == null)
        { SftpStatusMessage = "Collega entrambi i server."; return; }

        var selected = SelectedServer2Entries.Count > 0
            ? SelectedServer2Entries.ToList()
            : SelectedServer2Entry != null ? new List<SftpRemoteEntry> { SelectedServer2Entry } : new();

        await EnqueueRelayAsync(selected, _server2, _server2Config, RemoteConfig, SftpCurrentRemotePath);
    }

    /// <summary>Dalla scheda di destra alla cartella aperta del secondo server (sinistra).</summary>
    [RelayCommand]
    private async Task CopyMainToServer2Async()
    {
        if (!IsRemoteConnected || RemoteConfig == null || !IsServer2Connected || _server2Config == null)
        { SftpStatusMessage = "Collega entrambi i server."; return; }

        var selected = SelectedRemoteEntries.Count > 0
            ? SelectedRemoteEntries.ToList()
            : SftpSelectedRemoteEntry != null ? new List<SftpRemoteEntry> { SftpSelectedRemoteEntry } : new();

        await EnqueueRelayAsync(selected, Remote, RemoteConfig, _server2Config, Server2Path);
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
        SftpChangeHub.Publish(job.DestinationConfig.ServerKey, new SftpRemoteChange(folder));
        RefreshPanesShowing(job.DestinationConfig, folder);
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
    [RelayCommand] private void RelayQueueRemoveAll() => _relayQueue.RemoveAll();

    public void PauseRelayJob(SftpRelayJob job) => _relayQueue.PauseJob(job);
    public void ResumeRelayJob(SftpRelayJob job) => _relayQueue.ResumeJob(job);
    public void CancelRelayJob(SftpRelayJob job) => _relayQueue.CancelJob(job);
    public void RemoveRelayJob(SftpRelayJob job) => _relayQueue.RemoveJob(job);

    private void DisposeRelay()
    {
        try { _relayQueue.CancelAll(); } catch { }
        SftpChangeHub.Changed -= OnServerChanged;
        foreach (var tab in LeftTabs.ToList())
        {
            try { tab.Service.DisconnectAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
            try { (tab.Service as IDisposable)?.Dispose(); } catch { }
        }
    }
}
