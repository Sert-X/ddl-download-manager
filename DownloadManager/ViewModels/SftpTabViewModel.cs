using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Helpers;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Sftp;
using Microsoft.Extensions.Logging;

namespace DownloadManager.ViewModels;

public partial class SftpTabViewModel : ViewModelBase
{
    private readonly ISftpService _sftpService;
    private readonly SftpConfigRepository _sftpRepository;
    private readonly SftpUploadQueueService _sftpUploadQueue;
    private readonly SftpDownloadQueueService _sftpDownloadQueue;
    private readonly ZeroByteCheckerService _zeroByteChecker;
    private readonly ILogger<SftpTabViewModel> _logger;
    private readonly SharedState _shared;

    private readonly SemaphoreSlim _localRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _remoteRefreshLock = new(1, 1);
    private FileSystemWatcher? _localWatcher;
    private DispatcherTimer? _localWatcherDebounce;
    private readonly object _localWatcherLock = new();

    // --- Config ---
    public ObservableCollection<SftpConfig> SftpProfiles { get; } = new();

    [ObservableProperty] private SftpConfig? _selectedSftpProfile;
    [ObservableProperty] private bool _isSftpNewProfile = false;

    [ObservableProperty] private string _sftpName = "Default";
    [ObservableProperty] private string _sftpHost = string.Empty;
    [ObservableProperty] private int _sftpPort = 22;
    [ObservableProperty] private string _sftpUsername = string.Empty;
    [ObservableProperty] private string _sftpPassword = string.Empty;
    [ObservableProperty] private bool _sftpUseKeyAuth = false;
    [ObservableProperty] private string _sftpPrivateKeyPath = string.Empty;
    [ObservableProperty] private string _sftpPrivateKeyPassphrase = string.Empty;
    [ObservableProperty] private string _sftpRemoteBasePath = "/";
    [ObservableProperty] private string _sftpHostKeyFingerprint = string.Empty;

    [ObservableProperty] private string _sftpStatusMessage = string.Empty;
    [ObservableProperty] private bool _isSftpBusy = false;

    // --- Browser remoto ---
    [ObservableProperty] private ObservableCollection<SftpRemoteEntry> _sftpRemoteEntries = new();
    [ObservableProperty] private SftpRemoteEntry? _sftpSelectedRemoteEntry;
    [ObservableProperty] private bool _isRefreshingRemote;
    private bool _isForceReloading;
    [ObservableProperty] private string _remoteBrowserStats = "";

    // --- Browser locale ---
    [ObservableProperty] private ObservableCollection<LocalFileEntry> _sftpLocalEntries = new();
    [ObservableProperty] private LocalFileEntry? _sftpSelectedLocalEntry;
    [ObservableProperty] private string _localBrowserStats = "";

    // --- Sort ---
    public SortOption[] SortOptions { get; } = new[]
    {
        new SortOption("Nome", SftpSortMode.Name),
        new SortOption("Dimensione", SftpSortMode.Size),
        new SortOption("Data", SftpSortMode.Modified),
        new SortOption("Tipo", SftpSortMode.Type),
    };
    [ObservableProperty] private SortOption _localSortOption = null!;
    [ObservableProperty] private bool _localSortAscending = true;
    [ObservableProperty] private SortOption _remoteSortOption = null!;
    [ObservableProperty] private bool _remoteSortAscending = true;

    // --- Upload queue ---
    [ObservableProperty] private string _sftpUploadLocalFolder = string.Empty;
    [ObservableProperty] private string _sftpUploadRemoteFolder = "/";
    [ObservableProperty] private int _sftpMaxUploadConcurrency = 6;
    [ObservableProperty] private string _uploadQueueStats = "";

    // --- Download queue ---
    [ObservableProperty] private string _sftpDownloadRemoteFolder = "/";
    [ObservableProperty] private string _sftpDownloadLocalFolder = string.Empty;
    [ObservableProperty] private int _sftpMaxDownloadConcurrency = 6;
    [ObservableProperty] private string _downloadQueueStats = "";

    // --- Zero-byte ---
    [ObservableProperty] private bool _isCheckingZeroByte;
    [ObservableProperty] private string _zeroByteCheckStatus = string.Empty;

    // --- Badge ---
    [ObservableProperty] private bool _hasActiveUploads;
    [ObservableProperty] private bool _hasActiveDownloads;

    // --- Collezioni ---
    public ObservableCollection<SftpUploadJob> SftpUploadJobs => _sftpUploadQueue.AllJobs;
    public ObservableCollection<SftpDownloadJob> SftpDownloadJobs => _sftpDownloadQueue.AllJobs;
    public ObservableCollection<LocalFileEntry> SelectedLocalEntries { get; } = new();
    public ObservableCollection<SftpRemoteEntry> SelectedRemoteEntries { get; } = new();

    // --- Wrapper Shared ---
    public string SftpCurrentRemotePath
    {
        get => _shared.SftpCurrentRemotePath;
        set => _shared.SftpCurrentRemotePath = value;
    }

    public string SftpCurrentLocalPath
    {
        get => _shared.SftpCurrentLocalPath;
        set
        {
            if (_shared.SftpCurrentLocalPath == value) return;
            _shared.SftpCurrentLocalPath = value;
            SetupLocalWatcher(value);  // ← NUOVO
        }
    }

    public bool IsSftpConnected
    {
        get => _shared.IsSftpConnected;
        set
        {
            if (_shared.IsSftpConnected == value) return;
            _shared.IsSftpConnected = value;
            OnIsSftpConnectedChanged(value);
        }
    }

    // ============================================================
    //  COMPUTED — status bar remoto
    // ============================================================

    public string RemoteStatusText
    {
        get
        {
            if (IsCheckingZeroByte)
            {
                return string.IsNullOrEmpty(ZeroByteCheckStatus)
                    ? "Avvio check 0-byte in corso..."
                    : ZeroByteCheckStatus;
            }
            if (IsRefreshingRemote) return "Lettura cartella in corso...";
            return "";
        }
    }

    public string RemoteStatusIcon => IsCheckingZeroByte ? "🔍" : "🔄";
    public bool HasRemoteStatus => !string.IsNullOrEmpty(RemoteStatusText);
    public string LocalSortDirectionIcon => LocalSortAscending ? "↑" : "↓";
    public string RemoteSortDirectionIcon => RemoteSortAscending ? "↑" : "↓";

    // ============================================================
    //  COSTRUTTORE
    // ============================================================

    public SftpTabViewModel(
        ISftpService sftpService,
        SftpConfigRepository sftpRepository,
        SftpUploadQueueService sftpUploadQueue,
        SftpDownloadQueueService sftpDownloadQueue,
        ZeroByteCheckerService zeroByteChecker,
        ILogger<SftpTabViewModel> logger,
        SharedState shared)
    {
        _sftpService = sftpService;
        _sftpRepository = sftpRepository;
        _sftpUploadQueue = sftpUploadQueue;
        _sftpDownloadQueue = sftpDownloadQueue;
        _zeroByteChecker = zeroByteChecker;
        _logger = logger;
        _shared = shared;

        // Ordinamento default
        LocalSortOption = SortOptions[0];
        RemoteSortOption = SortOptions[0];

        // Debounce per evitare refresh multipli quando il file system
        // scatena eventi a raffica (es. molti file creati insieme).
        _localWatcherDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _localWatcherDebounce.Tick += async (_, _) =>
        {
            _localWatcherDebounce?.Stop();
            try { await SftpRefreshLocalInternalAsync(silent: true); } catch { }
        };

        _sftpUploadQueue.QueueChanged += OnUploadQueueChanged;
        _sftpDownloadQueue.QueueChanged += UpdateSftpBadges;

        _sftpUploadQueue.MaxConcurrency = SftpMaxUploadConcurrency;
        _sftpDownloadQueue.MaxConcurrency = SftpMaxDownloadConcurrency;

        _zeroByteChecker.StateChanged += OnZeroByteCheckerStateChanged;
        _zeroByteChecker.Completed += OnZeroByteCheckerCompleted;
        _zeroByteChecker.FolderScanned += OnZeroByteCheckerFolderScanned;

        _shared.PropertyChanged += OnSharedPropertyChanged;

        _ = Task.Run(SftpLoadAllProfilesAsync);

        SftpCurrentLocalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        Task.Delay(300).ContinueWith(_ => SftpRefreshLocalAsync(),
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnSharedPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SharedState.SftpCurrentRemotePath):
                OnPropertyChanged(nameof(SftpCurrentRemotePath));
                break;
            case nameof(SharedState.SftpCurrentLocalPath):
                OnPropertyChanged(nameof(SftpCurrentLocalPath));
                break;
            case nameof(SharedState.IsSftpConnected):
                OnPropertyChanged(nameof(IsSftpConnected));
                break;
        }
    }

    private void OnIsSftpConnectedChanged(bool value)
    {
        if (!value) SftpRemoteEntries.Clear();
    }

    // ============================================================
    //  PROPERTY CHANGED HANDLERS
    // ============================================================

    partial void OnLocalSortOptionChanged(SortOption value)
    {
        if (value == null) return;
        ReorderLocalEntries();
    }

    partial void OnLocalSortAscendingChanged(bool value)
    {
        OnPropertyChanged(nameof(LocalSortDirectionIcon));
        ReorderLocalEntries();
    }

    partial void OnRemoteSortOptionChanged(SortOption value)
    {
        if (value == null) return;
        ReorderRemoteEntries();
    }

    partial void OnRemoteSortAscendingChanged(bool value)
    {
        OnPropertyChanged(nameof(RemoteSortDirectionIcon));
        ReorderRemoteEntries();
    }
    /// <summary>
    /// Attiva/riattiva il FileSystemWatcher sulla cartella locale corrente.
    /// Viene chiamato quando cambia il path locale o dopo un refresh completo.
    /// </summary>
    private void SetupLocalWatcher(string path)
    {
        lock (_localWatcherLock)
        {
            // Ferma watcher precedente
            if (_localWatcher != null)
            {
                try
                {
                    _localWatcher.EnableRaisingEvents = false;
                    _localWatcher.Dispose();
                }
                catch { }
                _localWatcher = null;
            }

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;

            try
            {
                var w = new FileSystemWatcher(path)
                {
                    NotifyFilter = NotifyFilters.FileName
                                 | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite
                                 | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = false
                };

                FileSystemEventHandler handler = (_, _) => ScheduleLocalRefresh();
                RenamedEventHandler renamedHandler = (_, _) => ScheduleLocalRefresh();

                w.Created += handler;
                w.Deleted += handler;
                w.Changed += handler;
                w.Renamed += renamedHandler;

                w.EnableRaisingEvents = true;
                _localWatcher = w;

                _logger.LogInformation("[SFTP] Watcher locale attivo su {Path}", path);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[SFTP] Impossibile creare watcher su {Path}", path);
            }
        }
    }

    /// <summary>
    /// Schedula un refresh locale silenzioso con debounce.
    /// </summary>
    private void ScheduleLocalRefresh()
    {
        // Il timer deve essere toccato sul thread UI
        Dispatcher.UIThread.Post(() =>
        {
            _localWatcherDebounce?.Stop();
            _localWatcherDebounce?.Start();
        });
    }

    /// <summary>
    /// Ferma il watcher locale (chiamato alla chiusura del VM o su disconnessione).
    /// </summary>
    private void StopLocalWatcher()
    {
        lock (_localWatcherLock)
        {
            if (_localWatcher != null)
            {
                try
                {
                    _localWatcher.EnableRaisingEvents = false;
                    _localWatcher.Dispose();
                }
                catch { }
                _localWatcher = null;
            }
        }
    }

    /// <summary>
    /// Riordina la lista locale GIÀ in memoria. Ricostruisce la collezione
    /// (invece di spostare item in-place) perché la DataGrid di Avalonia
    /// non sempre reagisce visivamente ai "Move" puri su ObservableCollection.
    /// </summary>
    private void ReorderLocalEntries()
    {
        if (LocalSortOption == null) return;
        if (SftpLocalEntries.Count == 0) return;

        // Ricorda la selezione corrente per ripristinarla dopo
        var selectedPaths = SftpLocalEntries
            .Where(e => e == SftpSelectedLocalEntry)
            .Select(e => e.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var current = SftpLocalEntries.ToList();
        var sorted = SftpEntrySorter.SortLocalEntries(
            current, LocalSortOption.Mode, LocalSortAscending);

        // Ricostruzione: svuota e ripopola → la DataGrid aggiorna l'ordine visivo
        SftpLocalEntries.Clear();
        foreach (var e in sorted)
            SftpLocalEntries.Add(e);

        // Ripristina la selezione (l'item è lo stesso oggetto, basta riassegnarlo)
        if (SftpSelectedLocalEntry != null
            && !SftpLocalEntries.Contains(SftpSelectedLocalEntry))
        {
            SftpSelectedLocalEntry = SftpLocalEntries.FirstOrDefault();
        }

        _logger.LogInformation("[SFTP] Locale riordinato per {Mode} ({Dir})",
            LocalSortOption.Label, LocalSortAscending ? "↑" : "↓");
    }

    /// <summary>
    /// Riordina la lista remota GIÀ in memoria (stesso approccio del locale).
    /// </summary>
    private void ReorderRemoteEntries()
    {
        if (RemoteSortOption == null) return;
        if (SftpRemoteEntries.Count == 0) return;

        var current = SftpRemoteEntries.ToList();
        var sorted = SftpEntrySorter.SortRemoteEntries(
            current, RemoteSortOption.Mode, RemoteSortAscending);

        SftpRemoteEntries.Clear();
        foreach (var e in sorted)
            SftpRemoteEntries.Add(e);

        if (SftpSelectedRemoteEntry != null
            && !SftpRemoteEntries.Contains(SftpSelectedRemoteEntry))
        {
            SftpSelectedRemoteEntry = SftpRemoteEntries.FirstOrDefault();
        }

        _logger.LogInformation("[SFTP] Remoto riordinato per {Mode} ({Dir})",
            RemoteSortOption.Label, RemoteSortAscending ? "↑" : "↓");
    }
    
    partial void OnSftpMaxUploadConcurrencyChanged(int value)
    {
        if (_sftpUploadQueue != null) _sftpUploadQueue.MaxConcurrency = value;
    }

    partial void OnSftpMaxDownloadConcurrencyChanged(int value)
    {
        if (_sftpDownloadQueue != null) _sftpDownloadQueue.MaxConcurrency = value;
    }

    partial void OnSelectedSftpProfileChanged(SftpConfig? value)
    {
        if (value == null) return;

        IsSftpNewProfile = false;
        SftpName = value.Name;
        SftpHost = value.Host;
        SftpPort = value.Port;
        SftpUsername = value.Username;
        SftpPassword = value.Password;
        SftpUseKeyAuth = value.UseKeyAuth;
        SftpPrivateKeyPath = value.PrivateKeyPath;
        SftpPrivateKeyPassphrase = value.PrivateKeyPassphrase;
        SftpRemoteBasePath = value.RemoteBasePath;
        SftpHostKeyFingerprint = value.SshHostKeyFingerprint;

        SftpStatusMessage = $"Profilo '{value.Name}' caricato.";
    }

    partial void OnIsCheckingZeroByteChanged(bool value)
    {
        OnPropertyChanged(nameof(RemoteStatusText));
        OnPropertyChanged(nameof(RemoteStatusIcon));
        OnPropertyChanged(nameof(HasRemoteStatus));
    }

    partial void OnZeroByteCheckStatusChanged(string value)
        => OnPropertyChanged(nameof(RemoteStatusText));

    partial void OnIsRefreshingRemoteChanged(bool value)
    {
        OnPropertyChanged(nameof(RemoteStatusText));
        OnPropertyChanged(nameof(RemoteStatusIcon));
        OnPropertyChanged(nameof(HasRemoteStatus));
    }

    // ============================================================
    //  BADGE / STATS
    // ============================================================

    private void UpdateSftpBadges()
    {
        HasActiveUploads = _sftpUploadQueue.AllJobs.Any(j =>
            j.Status == SftpJobStatus.Uploading || j.Status == SftpJobStatus.Pending);
        HasActiveDownloads = _sftpDownloadQueue.AllJobs.Any(j =>
            j.Status == SftpJobStatus.Downloading || j.Status == SftpJobStatus.Pending);

        UpdateUploadQueueStats();
        UpdateDownloadQueueStats();
    }

    private void UpdateRemoteBrowserStats()
    {
        int dirs = SftpRemoteEntries.Count(e => e.IsDirectory);
        int files = SftpRemoteEntries.Count - dirs;

        RemoteBrowserStats = SftpRemoteEntries.Count == 0
            ? "Cartella vuota"
            : $"{dirs} cartelle, {files} file ({SftpRemoteEntries.Count} totali)";
    }

    private void UpdateLocalBrowserStats()
    {
        int dirs = SftpLocalEntries.Count(e => e.IsDirectory);
        int files = SftpLocalEntries.Count - dirs;

        LocalBrowserStats = SftpLocalEntries.Count == 0
            ? "Cartella vuota"
            : $"{dirs} cartelle, {files} file ({SftpLocalEntries.Count} totali)";
    }

    private void UpdateUploadQueueStats()
    {
        var jobs = _sftpUploadQueue.AllJobs;
        int total = jobs.Count;
        if (total == 0) { UploadQueueStats = ""; return; }

        int pending = jobs.Count(j => j.Status == SftpJobStatus.Pending);
        int uploading = jobs.Count(j => j.Status == SftpJobStatus.Uploading);
        int paused = jobs.Count(j => j.Status == SftpJobStatus.Paused);
        int completed = jobs.Count(j => j.Status == SftpJobStatus.Completed);
        int failed = jobs.Count(j => j.Status == SftpJobStatus.Failed);
        int cancelled = jobs.Count(j => j.Status == SftpJobStatus.Cancelled);

        var parts = new List<string> { $"{total} totali" };
        if (uploading > 0) parts.Add($"{uploading} in corso");
        if (pending > 0) parts.Add($"{pending} in attesa");
        if (paused > 0) parts.Add($"{paused} in pausa");
        if (completed > 0) parts.Add($"{completed} completati");
        if (failed > 0) parts.Add($"{failed} falliti");
        if (cancelled > 0) parts.Add($"{cancelled} annullati");

        UploadQueueStats = string.Join(" · ", parts);
    }

    private void UpdateDownloadQueueStats()
    {
        var jobs = _sftpDownloadQueue.AllJobs;
        int total = jobs.Count;
        if (total == 0) { DownloadQueueStats = ""; return; }

        int pending = jobs.Count(j => j.Status == SftpJobStatus.Pending);
        int downloading = jobs.Count(j => j.Status == SftpJobStatus.Downloading);
        int paused = jobs.Count(j => j.Status == SftpJobStatus.Paused);
        int completed = jobs.Count(j => j.Status == SftpJobStatus.Completed);
        int failed = jobs.Count(j => j.Status == SftpJobStatus.Failed);
        int cancelled = jobs.Count(j => j.Status == SftpJobStatus.Cancelled);

        var parts = new List<string> { $"{total} totali" };
        if (downloading > 0) parts.Add($"{downloading} in corso");
        if (pending > 0) parts.Add($"{pending} in attesa");
        if (paused > 0) parts.Add($"{paused} in pausa");
        if (completed > 0) parts.Add($"{completed} completati");
        if (failed > 0) parts.Add($"{failed} falliti");
        if (cancelled > 0) parts.Add($"{cancelled} annullati");

        DownloadQueueStats = string.Join(" · ", parts);
    }
    private void OnUploadQueueChanged()
    {
        UpdateSftpBadges();
    }

    // ============================================================
    //  CONFIG
    // ============================================================

    [RelayCommand]
    private async Task SftpLoadAllProfilesAsync()
    {
        try
        {
            var configs = await _sftpRepository.GetAllAsync();
            SftpProfiles.Clear();
            foreach (var c in configs) SftpProfiles.Add(c);

            if (SftpProfiles.Count > 0)
            {
                SelectedSftpProfile = SftpProfiles[0];
                SftpStatusMessage = $"{SftpProfiles.Count} profili disponibili.";
            }
            else
            {
                SftpStatusMessage = "Nessun profilo. Clicca 'Nuovo'.";
                IsSftpNewProfile = true;
            }
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    [RelayCommand]
    private void SftpNewProfile()
    {
        SelectedSftpProfile = null;
        IsSftpNewProfile = true;
        SftpName = "Nuovo"; SftpHost = ""; SftpPort = 22; SftpUsername = ""; SftpPassword = "";
        SftpUseKeyAuth = false; SftpPrivateKeyPath = ""; SftpPrivateKeyPassphrase = "";
        SftpRemoteBasePath = "/"; SftpHostKeyFingerprint = "";
        SftpStatusMessage = "Nuovo profilo.";
    }

    [RelayCommand]
    private async Task SftpDeleteProfileAsync()
    {
        if (IsSftpNewProfile || SelectedSftpProfile == null) { SftpStatusMessage = "Nessun profilo selezionato."; return; }

        try
        {
            var id = SelectedSftpProfile.Id;
            await _sftpRepository.DeleteAsync(id);

            var toRemove = SftpProfiles.FirstOrDefault(p => p.Id == id);
            if (toRemove != null) SftpProfiles.Remove(toRemove);

            if (SftpProfiles.Count > 0) SelectedSftpProfile = SftpProfiles[0];
            else SftpNewProfile();

            SftpStatusMessage = "Profilo eliminato.";
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SftpSaveConfigAsync()
    {
        if (string.IsNullOrWhiteSpace(SftpHost)) { SftpStatusMessage = "Host obbligatorio."; return; }
        if (string.IsNullOrWhiteSpace(SftpUsername)) { SftpStatusMessage = "Username obbligatorio."; return; }

        try
        {
            var config = IsSftpNewProfile || SelectedSftpProfile == null ? new SftpConfig() : SelectedSftpProfile;

            config.Name = SftpName; config.Host = SftpHost; config.Port = SftpPort;
            config.Username = SftpUsername; config.Password = SftpPassword;
            config.UseKeyAuth = SftpUseKeyAuth; config.PrivateKeyPath = SftpPrivateKeyPath;
            config.PrivateKeyPassphrase = SftpPrivateKeyPassphrase; config.RemoteBasePath = SftpRemoteBasePath;
            config.SshHostKeyFingerprint = SftpHostKeyFingerprint;

            if (config.Id == 0)
            {
                await _sftpRepository.InsertAsync(config);
                SftpProfiles.Add(config);
                SelectedSftpProfile = config;
                IsSftpNewProfile = false;
            }
            else await _sftpRepository.UpdateAsync(config);

            SftpStatusMessage = $"Profilo '{config.Name}' salvato.";
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SftpTestAsync()
    {
        if (string.IsNullOrWhiteSpace(SftpHost) || string.IsNullOrWhiteSpace(SftpUsername))
        { SftpStatusMessage = "Compila Host e Username."; return; }

        try
        {
            IsSftpBusy = true;
            SftpStatusMessage = "Test connessione...";
            await _sftpService.TestConnectionAsync(BuildCurrentSftpConfig());
            SftpStatusMessage = "Connessione riuscita!";
        }
        catch (Exception ex) { SftpStatusMessage = $"Test fallito: {ex.Message}"; }
        finally { IsSftpBusy = false; }
    }

    [RelayCommand]
    private async Task SftpConnectAsync()
    {
        try
        {
            IsSftpBusy = true;
            SftpStatusMessage = "Connessione...";

            var config = BuildCurrentSftpConfig();
            await _sftpService.ConnectAsync(config);

            IsSftpConnected = true;
            SftpCurrentRemotePath = _sftpService.CurrentRemotePath;

            _sftpUploadQueue.Configure(config);
            _sftpUploadQueue.MaxConcurrency = SftpMaxUploadConcurrency;

            _sftpDownloadQueue.Configure(config);
            _sftpDownloadQueue.MaxConcurrency = SftpMaxDownloadConcurrency;

            await SftpRefreshAsync();
            SftpStatusMessage = $"Connesso a {SftpCurrentRemotePath}";
        }
        catch (Exception ex) { IsSftpConnected = false; SftpStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsSftpBusy = false; }
    }

    [RelayCommand]
    private async Task SftpDisconnectAsync()
    {
        try
        {
            await _sftpService.DisconnectAsync();
            IsSftpConnected = false;
            SftpRemoteEntries.Clear();
            RemoteBrowserStats = "";
            SftpStatusMessage = "Disconnesso.";
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SftpRefreshAsync() => await SftpRefreshInternalAsync(silent: false);

    private async Task SftpRefreshInternalAsync(bool silent)
    {
        if (!IsSftpConnected) return;

        await _remoteRefreshLock.WaitAsync();
        try
        {
            var pathToRead = SftpCurrentRemotePath;
            if (string.IsNullOrWhiteSpace(pathToRead)) pathToRead = "/";
            if (IsRefreshingRemote && !silent) return;

            try
            {
                IsRefreshingRemote = true;
                if (!silent) { IsSftpBusy = true; SftpStatusMessage = $"Lettura {pathToRead}..."; }

                var entries = await _sftpService.ListDirectoryAsync(pathToRead);

                if (SftpCurrentRemotePath != pathToRead) return;

                foreach (var e in entries)
                {
                    if (!e.IsDirectory)
                    {
                        e.HasZeroByteIssue = e.Size == 0;
                    }
                    else
                    {
                        var normalized = e.FullPath.TrimEnd('/');
                        e.HasZeroByteIssue = _zeroByteChecker.TryGetFolderFlag(normalized, out var flag) && flag;
                        if (_zeroByteChecker.TryGetFolderContents(normalized, out var cachedInfo))
                            e.ContentsInfo = cachedInfo;
                    }
                }

                var sorted = SftpEntrySorter.SortRemoteEntries(entries, RemoteSortOption.Mode, RemoteSortAscending);
                SftpEntrySorter.SyncCollection(SftpRemoteEntries, sorted, e => e.FullPath, SftpEntrySorter.UpdateRemoteEntryFromSource);

                UpdateRemoteBrowserStats();

                if (!silent)
                    SftpStatusMessage = $"{entries.Count} elementi in {pathToRead} ({DateTime.Now:HH:mm:ss})";
            }
            catch (Exception ex)
            {
                if (!silent) SftpStatusMessage = $"Errore: {ex.Message}";
            }
            finally
            {
                IsRefreshingRemote = false;
                if (!silent) IsSftpBusy = false;
            }
        }
        finally { _remoteRefreshLock.Release(); }
    }

    [RelayCommand]
    private async Task SftpGoUpAsync()
    {
        if (!IsSftpConnected) return;
        var path = SftpCurrentRemotePath.TrimEnd('/');
        if (string.IsNullOrEmpty(path)) { SftpCurrentRemotePath = "/"; await SftpRefreshAsync(); return; }
        int lastSlash = path.LastIndexOf('/');
        SftpCurrentRemotePath = lastSlash <= 0 ? "/" : path.Substring(0, lastSlash);
        await SftpRefreshAsync();
    }

    [RelayCommand]
    private async Task SftpGoHomeAsync()
    {
        if (!IsSftpConnected) return;
        SftpCurrentRemotePath = _sftpService.GetWorkingDirectory();
        await SftpRefreshAsync();
    }

    [RelayCommand]
    private async Task SftpForceReloadAsync()
    {
        if (!IsSftpConnected) return;
        if (_isForceReloading) return;      // evita doppio force reload concorrente
        if (IsRefreshingRemote) return;     // evita conflitto con refresh in corso

        _isForceReloading = true;
        try
        {
            SftpStatusMessage = "Riconnessione per ricaricare dati freschi...";
            var config = BuildCurrentSftpConfig();
            var savedPath = SftpCurrentRemotePath;

            await _sftpService.DisconnectAsync();
            await _sftpService.ConnectAsync(config);
            SftpCurrentRemotePath = savedPath;

            _logger.LogInformation("[SFTP] Force reload: riconnesso a {Path}", savedPath);
        }
        catch (Exception ex)
        {
            SftpStatusMessage = $"Errore riconnessione: {ex.Message}";
            _logger.LogWarning(ex, "[SFTP] Errore force reload");
            return;
        }
        finally
        {
            _isForceReloading = false;
        }

        // Ora il refresh: gestisce da solo IsRefreshingRemote
        await SftpRefreshInternalAsync(silent: false);
    }

    [RelayCommand] private void ToggleLocalSortDirection() => LocalSortAscending = !LocalSortAscending;
    [RelayCommand] private void ToggleRemoteSortDirection() => RemoteSortAscending = !RemoteSortAscending;

    public void SetSftpUploadLocalFolder(string folder) => SftpUploadLocalFolder = folder;
    public void SetSftpUploadRemoteFolder(string folder) => SftpUploadRemoteFolder = folder;
    public void SetSftpDownloadLocalFolder(string folder) => SftpDownloadLocalFolder = folder;

    private SftpConfig BuildCurrentSftpConfig() => new()
    {
        Name = SftpName, Host = SftpHost, Port = SftpPort, Username = SftpUsername,
        Password = SftpPassword, UseKeyAuth = SftpUseKeyAuth,
        PrivateKeyPath = SftpPrivateKeyPath, PrivateKeyPassphrase = SftpPrivateKeyPassphrase,
        RemoteBasePath = SftpRemoteBasePath, SshHostKeyFingerprint = SftpHostKeyFingerprint
    };

    // ============================================================
    //  BROWSER LOCALE
    // ============================================================

    [RelayCommand]
    private async Task SftpRefreshLocalAsync() => await SftpRefreshLocalInternalAsync(silent: false);

    private async Task SftpRefreshLocalInternalAsync(bool silent)
    {
        await _localRefreshLock.WaitAsync();
        try
        {
            var pathToRead = SftpCurrentLocalPath;

            if (string.IsNullOrWhiteSpace(pathToRead))
            {
                pathToRead = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                SftpCurrentLocalPath = pathToRead;
            }
            else if (!Directory.Exists(pathToRead))
            {
                if (!silent) SftpStatusMessage = $"Cartella non trovata: {pathToRead}";
                return;
            }

            try
            {
                var entries = await Task.Run(() =>
                {
                    var list = new List<LocalFileEntry>();
                    foreach (var dir in Directory.GetDirectories(pathToRead))
                    {
                        try
                        {
                            var info = new DirectoryInfo(dir);
                            list.Add(new LocalFileEntry { Name = info.Name, FullPath = info.FullName, IsDirectory = true, Size = 0, Modified = info.LastWriteTime });
                        }
                        catch { }
                    }
                    foreach (var file in Directory.GetFiles(pathToRead))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            list.Add(new LocalFileEntry { Name = info.Name, FullPath = info.FullName, IsDirectory = false, Size = info.Length, Modified = info.LastWriteTime });
                        }
                        catch { }
                    }
                    return list;
                });

                if (SftpCurrentLocalPath != pathToRead) return;

                var sorted = SftpEntrySorter.SortLocalEntries(entries, LocalSortOption.Mode, LocalSortAscending);
                SftpEntrySorter.SyncCollection(SftpLocalEntries, sorted, e => e.FullPath, SftpEntrySorter.UpdateLocalEntryFromSource);
                UpdateLocalBrowserStats();
                // Assicura che il watcher punti alla cartella corrente
                SetupLocalWatcher(pathToRead);
            }
            catch (Exception ex)
            {
                if (!silent) SftpStatusMessage = $"Errore lettura locale: {ex.Message}";
            }
        }
        finally { _localRefreshLock.Release(); }
    }

    [RelayCommand]
    private void CheckZeroByteFolders()
    {
        if (!IsSftpConnected) { SftpStatusMessage = "Non connesso."; return; }
        if (_zeroByteChecker.IsChecking) return;

        var folders = SftpRemoteEntries.Where(e => e.IsDirectory).ToList();
        if (folders.Count == 0)
        {
            SftpStatusMessage = "Nessuna cartella da controllare.";
            return;
        }

        SftpStatusMessage = $"Check di {folders.Count} cartelle in corso...";

        _logger.LogInformation("[ZERO-BYTE] Check manuale su {Count} cartelle", folders.Count);

        _ = _zeroByteChecker.RunCheckAsync(folders);
    }

    [RelayCommand]
    private void StopZeroByteCheck() => _zeroByteChecker.Stop();

    private void OnZeroByteCheckerStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsCheckingZeroByte = _zeroByteChecker.IsChecking;
            ZeroByteCheckStatus = _zeroByteChecker.IsChecking ? _zeroByteChecker.ProgressStatus : string.Empty;
        });
    }

    private void OnZeroByteCheckerCompleted(object? sender, string message)
        => Dispatcher.UIThread.Post(() => SftpStatusMessage = message);

    private void OnZeroByteCheckerFolderScanned(object? sender, ZeroByteFolderResult result)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var normalized = result.FullPath.TrimEnd('/');
            foreach (var entry in SftpRemoteEntries)
            {
                if (string.Equals(entry.FullPath.TrimEnd('/'), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    entry.HasZeroByteIssue = result.HasZeroByte;
                    entry.ContentsInfo = result.ContentsSummary;
                }
            }
        });
    }

    private void InvalidateZeroByteCacheForFolder(string? folderPath) => _zeroByteChecker.InvalidateFolder(folderPath);
    private void InvalidateZeroByteCacheRecursive(string? folderPath) => _zeroByteChecker.InvalidateRecursive(folderPath);

    [RelayCommand]
    private async Task SftpLocalGoUpAsync()
    {
        if (string.IsNullOrWhiteSpace(SftpCurrentLocalPath)) return;
        var parent = Directory.GetParent(SftpCurrentLocalPath);
        if (parent == null) return;
        SftpCurrentLocalPath = parent.FullName;
        await SftpRefreshLocalAsync();
    }

    [RelayCommand]
    private async Task SftpLocalHomeAsync()
    {
        SftpCurrentLocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        await SftpRefreshLocalAsync();
    }

    [RelayCommand]
    private async Task SftpLocalEnterAsync(LocalFileEntry? entry)
    {
        if (entry == null) return;
        if (entry.IsDirectory)
        {
            SftpCurrentLocalPath = entry.FullPath;
            await SftpRefreshLocalAsync();
        }
    }

    public void SetSftpCurrentLocalPath(string path)
    {
        SftpCurrentLocalPath = path;
        _ = SftpRefreshLocalAsync();
    }

    // ============================================================
    //  CRUD LOCALE
    // ============================================================

    public async Task SftpLocalDeleteManyAsync(IEnumerable<LocalFileEntry> entries)
    {
        var list = entries.Where(e => e != null).ToList();
        if (list.Count == 0) return;

        int success = 0, failed = 0;
        string? firstError = null;

        foreach (var entry in list)
        {
            try
            {
                if (entry.IsDirectory) Directory.Delete(entry.FullPath, true);
                else File.Delete(entry.FullPath);
                success++;
            }
            catch (Exception ex) { failed++; firstError ??= ex.Message; }
        }

        await SftpRefreshLocalInternalAsync(silent: true);

        if (failed == 0)
            SftpStatusMessage = success == 1 ? "Elemento eliminato." : $"{success} elementi eliminati.";
        else if (success == 0)
            SftpStatusMessage = $"Eliminazione fallita: {firstError}";
        else
            SftpStatusMessage = $"Eliminati {success}, falliti {failed}: {firstError}";
    }

    public async Task SftpLocalCreateFolderAsync(string name)
    {
        try
        {
            var full = Path.Combine(SftpCurrentLocalPath, name);
            if (Directory.Exists(full)) { SftpStatusMessage = "Cartella già esistente."; return; }
            Directory.CreateDirectory(full);
            SftpStatusMessage = $"Cartella creata: {name}";
            await SftpRefreshLocalAsync();
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    public async Task SftpLocalRenameAsync(LocalFileEntry entry, string newName)
    {
        try
        {
            var dir = Path.GetDirectoryName(entry.FullPath) ?? SftpCurrentLocalPath;
            var newPath = Path.Combine(dir, newName);

            if (entry.IsDirectory) Directory.Move(entry.FullPath, newPath);
            else File.Move(entry.FullPath, newPath);

            SftpStatusMessage = $"Rinominato in: {newName}";
            await SftpRefreshLocalAsync();
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    // ============================================================
    //  CRUD REMOTO
    // ============================================================

    public async Task SftpRemoteDeleteManyAsync(IEnumerable<SftpRemoteEntry> entries)
    {
        if (!IsSftpConnected) { SftpStatusMessage = "Non connesso."; return; }

        var list = entries.Where(e => e != null).ToList();
        if (list.Count == 0) return;

        int success = 0, failed = 0;
        var errors = new List<string>();

        foreach (var entry in list)
        {
            try
            {
                if (entry.IsDirectory) await _sftpService.DeleteDirectoryAsync(entry.FullPath);
                else await _sftpService.DeleteFileAsync(entry.FullPath);
                success++;
                InvalidateZeroByteCacheRecursive(TransferJobBase.GetRemoteParent(entry.FullPath));
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{entry.Name}: {ex.Message}");
            }
        }

        await SftpRefreshInternalAsync(silent: true);
        InvalidateZeroByteCacheRecursive(SftpCurrentRemotePath);

        if (failed == 0)
            SftpStatusMessage = success == 1 ? "Elemento eliminato dal server." : $"{success} elementi eliminati dal server.";
        else if (success == 0)
            SftpStatusMessage = $"Eliminazione fallita: {errors.First()}";
        else
            SftpStatusMessage = $"Eliminati {success}, falliti {failed}. Primo errore: {errors.First()}";
    }

    public async Task SftpRemoteCreateFolderAsync(string name)
    {
        if (!IsSftpConnected) { SftpStatusMessage = "Non connesso."; return; }

        try
        {
            var full = $"{SftpCurrentRemotePath.TrimEnd('/')}/{name}";
            await _sftpService.CreateDirectoryAsync(full);
            SftpStatusMessage = $"Cartella remota creata: {name}";
            await SftpRefreshAsync();
            InvalidateZeroByteCacheRecursive(SftpCurrentRemotePath);
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    public async Task SftpRemoteRenameAsync(SftpRemoteEntry entry, string newName)
    {
        if (!IsSftpConnected) { SftpStatusMessage = "Non connesso."; return; }

        try
        {
            var dir = SftpCurrentRemotePath.TrimEnd('/');
            var newPath = $"{dir}/{newName}";

            await _sftpService.RenameAsync(entry.FullPath, newPath);
            SftpStatusMessage = $"Rinominato in: {newName}";
            await SftpRefreshAsync();
            InvalidateZeroByteCacheRecursive(SftpCurrentRemotePath);
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    // ============================================================
    //  UPLOAD / DOWNLOAD
    // ============================================================

    [RelayCommand]
    private async Task SftpUploadSelectedAsync()
    {
        if (!IsSftpConnected) { SftpStatusMessage = "Non connesso."; return; }

        var selected = SelectedLocalEntries.Count > 0
            ? SelectedLocalEntries.ToList()
            : (SftpSelectedLocalEntry != null ? new List<LocalFileEntry> { SftpSelectedLocalEntry } : new List<LocalFileEntry>());

        if (selected.Count == 0) { SftpStatusMessage = "Seleziona almeno un file/cartella."; return; }

        try
        {
            _sftpUploadQueue.Configure(BuildCurrentSftpConfig());
            _sftpUploadQueue.MaxConcurrency = SftpMaxUploadConcurrency;

            int totalQueued = 0;
            foreach (var local in selected)
            {
                if (local.IsDirectory)
                {
                    var count = await _sftpUploadQueue.EnqueueFolderAsync(local.FullPath, SftpCurrentRemotePath);
                    totalQueued += count;
                }
                else
                {
                    var remotePath = $"{SftpCurrentRemotePath.TrimEnd('/')}/{local.Name}";
                    var job = new SftpUploadJob
                    {
                        LocalPath = local.FullPath,
                        RemotePath = remotePath,
                        TotalBytes = new FileInfo(local.FullPath).Length,
                        Status = SftpJobStatus.Pending
                    };
                    await _sftpUploadQueue.EnqueueJobAsync(job);
                    totalQueued++;
                }
            }

            SftpStatusMessage = $"{totalQueued} file accodati per upload.";
            InvalidateZeroByteCacheForFolder(SftpCurrentRemotePath);
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    public async Task EnqueueUploadsFromPathsAsync(IEnumerable<string> localPaths, string? remoteDir = null)
    {
        if (!IsSftpConnected) { SftpStatusMessage = "Non connesso."; return; }

        var targetDir = string.IsNullOrWhiteSpace(remoteDir) ? SftpCurrentRemotePath : remoteDir;
        if (string.IsNullOrWhiteSpace(targetDir)) { SftpStatusMessage = "Nessuna cartella remota di destinazione."; return; }

        try
        {
            _sftpUploadQueue.Configure(BuildCurrentSftpConfig());
            _sftpUploadQueue.MaxConcurrency = SftpMaxUploadConcurrency;

            int totalQueued = 0;
            foreach (var path in localPaths)
            {
                if (Directory.Exists(path))
                    totalQueued += await _sftpUploadQueue.EnqueueFolderAsync(path, targetDir);
                else if (File.Exists(path))
                {
                    var remotePath = $"{targetDir.TrimEnd('/')}/{Path.GetFileName(path)}";
                    var job = new SftpUploadJob
                    {
                        LocalPath = path,
                        RemotePath = remotePath,
                        TotalBytes = new FileInfo(path).Length,
                        Status = SftpJobStatus.Pending
                    };
                    await _sftpUploadQueue.EnqueueJobAsync(job);
                    totalQueued++;
                }
            }

            if (totalQueued == 0) { SftpStatusMessage = "Nessun file valido da caricare."; return; }

            SftpStatusMessage = $"{totalQueued} file accodati per upload (drag & drop).";
            _sftpUploadQueue.StartAll();
            InvalidateZeroByteCacheRecursive(targetDir);
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore accodamento upload: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SftpDownloadSelectedAsync()
    {
        if (!IsSftpConnected) { SftpStatusMessage = "Non connesso."; return; }

        var selected = SelectedRemoteEntries.Count > 0
            ? SelectedRemoteEntries.ToList()
            : (SftpSelectedRemoteEntry != null ? new List<SftpRemoteEntry> { SftpSelectedRemoteEntry } : new List<SftpRemoteEntry>());

        if (selected.Count == 0) { SftpStatusMessage = "Seleziona almeno un file/cartella."; return; }

        try
        {
            _sftpDownloadQueue.Configure(BuildCurrentSftpConfig());
            _sftpDownloadQueue.MaxConcurrency = SftpMaxDownloadConcurrency;

            var targetDir = string.IsNullOrWhiteSpace(SftpDownloadLocalFolder) ? SftpCurrentLocalPath : SftpDownloadLocalFolder;
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            int queued = 0;
            foreach (var remote in selected)
            {
                if (remote.IsDirectory)
                {
                    await DownloadRemoteFolderRecursiveAsync(remote.FullPath, targetDir);
                    queued++;
                }
                else
                {
                    var localPath = Path.Combine(targetDir, remote.Name);
                    var job = new SftpDownloadJob
                    {
                        RemotePath = remote.FullPath,
                        LocalPath = localPath,
                        TotalBytes = remote.Size,
                        Status = SftpJobStatus.Pending
                    };
                    await _sftpDownloadQueue.EnqueueJobAsync(job);
                    queued++;
                }
            }

            SftpStatusMessage = $"{queued} elementi accodati per download.";
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    private async Task DownloadRemoteFolderRecursiveAsync(string remoteFolder, string localTarget)
    {
        var remoteName = Path.GetFileName(remoteFolder.TrimEnd('/'));
        var localFolder = Path.Combine(localTarget, remoteName);
        Directory.CreateDirectory(localFolder);

        var entries = await _sftpService.ListDirectoryAsync(remoteFolder);
        foreach (var entry in entries)
        {
            if (entry.IsDirectory)
                await DownloadRemoteFolderRecursiveAsync(entry.FullPath, localFolder);
            else
            {
                var localPath = Path.Combine(localFolder, entry.Name);
                var job = new SftpDownloadJob
                {
                    RemotePath = entry.FullPath,
                    LocalPath = localPath,
                    TotalBytes = entry.Size,
                    Status = SftpJobStatus.Pending
                };
                await _sftpDownloadQueue.EnqueueJobAsync(job);
            }
        }
    }

    // ============================================================
    //  UPLOAD QUEUE
    // ============================================================

    [RelayCommand]
    private async Task SftpEnqueueFolderAsync()
    {
        if (!IsSftpConnected) { SftpStatusMessage = "Non connesso."; return; }
        if (string.IsNullOrWhiteSpace(SftpUploadLocalFolder) || !Directory.Exists(SftpUploadLocalFolder))
        { SftpStatusMessage = "Seleziona una cartella locale valida."; return; }

        try
        {
            _sftpUploadQueue.Configure(BuildCurrentSftpConfig());
            _sftpUploadQueue.MaxConcurrency = SftpMaxUploadConcurrency;

            var count = await _sftpUploadQueue.EnqueueFolderAsync(SftpUploadLocalFolder, SftpUploadRemoteFolder);
            SftpStatusMessage = $"{count} file accodati per upload.";
            InvalidateZeroByteCacheRecursive(SftpUploadRemoteFolder);
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    [RelayCommand] private void SftpQueueStartAll() { _sftpUploadQueue.StartAll(); SftpStatusMessage = "Coda upload avviata."; }
    [RelayCommand] private void SftpQueuePauseAll() { _sftpUploadQueue.PauseAll(); SftpStatusMessage = "Coda upload in pausa."; }
    [RelayCommand] private void SftpQueueCancelAll() { _sftpUploadQueue.CancelAll(); SftpStatusMessage = "Coda upload annullata."; }
    [RelayCommand] private void SftpQueueClearCompleted() { _sftpUploadQueue.ClearCompleted(); SftpStatusMessage = "Completati rimossi."; }
    [RelayCommand] private void SftpQueueClearCancelled() { _sftpUploadQueue.ClearCancelled(); SftpStatusMessage = "Annullati rimossi."; }
    [RelayCommand] private void SftpQueueClearFailed() { _sftpUploadQueue.ClearFailed(); SftpStatusMessage = "Failed rimossi."; }

    public void PauseSftpJobPublic(SftpUploadJob job) => _sftpUploadQueue.PauseJob(job);
    public void ResumeSftpJobPublic(SftpUploadJob job) => _sftpUploadQueue.ResumeJob(job);
    public void CancelSftpJobPublic(SftpUploadJob job) => _sftpUploadQueue.CancelJob(job);
    public void RemoveSftpJobPublic(SftpUploadJob job) => _sftpUploadQueue.RemoveJob(job);

    // ============================================================
    //  DOWNLOAD QUEUE
    // ============================================================

    [RelayCommand] private void SftpDownloadQueueStartAll() { _sftpDownloadQueue.StartAll(); SftpStatusMessage = "Coda download avviata."; }
    [RelayCommand] private void SftpDownloadQueuePauseAll() { _sftpDownloadQueue.PauseAll(); SftpStatusMessage = "Coda download in pausa."; }
    [RelayCommand] private void SftpDownloadQueueCancelAll() { _sftpDownloadQueue.CancelAll(); SftpStatusMessage = "Coda download annullata."; }
    [RelayCommand] private void SftpDownloadQueueClearCompleted() { _sftpDownloadQueue.ClearCompleted(); SftpStatusMessage = "Completati rimossi."; }
    [RelayCommand] private void SftpDownloadQueueClearCancelled() { _sftpDownloadQueue.ClearCancelled(); SftpStatusMessage = "Annullati rimossi."; }
    [RelayCommand] private void SftpDownloadQueueClearFailed() { _sftpDownloadQueue.ClearFailed(); SftpStatusMessage = "Failed rimossi."; }

    public void PauseSftpDownloadJobPublic(SftpDownloadJob job) => _sftpDownloadQueue.PauseJob(job);
    public void ResumeSftpDownloadJobPublic(SftpDownloadJob job) => _sftpDownloadQueue.ResumeJob(job);
    public void CancelSftpDownloadJobPublic(SftpDownloadJob job) => _sftpDownloadQueue.CancelJob(job);
    public void RemoveSftpDownloadJobPublic(SftpDownloadJob job) => _sftpDownloadQueue.RemoveJob(job);
    public void Dispose()
    {
        StopLocalWatcher();
        _localWatcherDebounce?.Stop();
    }
}