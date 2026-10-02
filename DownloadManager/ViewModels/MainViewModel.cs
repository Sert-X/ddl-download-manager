using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.AnimeWorld;
using DownloadManager.Services.Download;
using DownloadManager.Services.FileOrganizer;
using DownloadManager.Services.Logging;
using DownloadManager.Services.Sftp;

namespace DownloadManager.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IAnimeWorldService _animeWorldService;
    private readonly DownloadQueueService _queueService;
    private readonly FileNameBuilder _fileNameBuilder;
    private readonly SettingsService _settings;
    private readonly IFileOrganizerService _fileOrganizer;
    private readonly SftpConfigRepository _sftpRepository;
    private readonly ISftpService _sftpService;
    private readonly SftpUploadQueueService _sftpUploadQueue;
    private readonly SftpDownloadQueueService _sftpDownloadQueue;
    private readonly DownloadManager.Services.Proxy.ProxyService _proxyService;
    private readonly LogService _logService;

    private Episode? _currentEpisode;
    private CancellationTokenSource? _batchCts;
    private readonly DispatcherTimer _statsTimer;
    private readonly DispatcherTimer _sftpLocalAutoRefreshTimer;
    private readonly DispatcherTimer _sftpRemoteAutoRefreshTimer;
    // Cache dell'esito del check 0-byte per le cartelle già controllate.
    // Chiave: FullPath della cartella. Valore: true se contiene file da 0 byte.
    private readonly Dictionary<string, bool> _zeroByteFolderCache = new(StringComparer.OrdinalIgnoreCase);

    // --- Ricerca ---
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _statusMessage = "Pronto. Premi 'Cerca' per iniziare.";
    [ObservableProperty] private ObservableCollection<AnimeSearchResult> _searchResults = new();
    [ObservableProperty] private AnimeSearchResult? _selectedAnime;
    [ObservableProperty] private ObservableCollection<Episode> _episodes = new();

    // --- Download (Anime) ---
    [ObservableProperty] private int _maxConcurrency = 3;
    [ObservableProperty] private string _namingPattern = "{OriginalFileName}";
    [ObservableProperty] private string _baseDownloadFolder = string.Empty;
    [ObservableProperty] private bool _createSeriesFolder = true;
    [ObservableProperty] private bool _isBatchActive;
    [ObservableProperty] private string _themeVariant = "Default";

    // --- Organizza: modalità + singola cartella ---
    [ObservableProperty] private bool _organizeModeIsLocal = true;
    [ObservableProperty] private string _organizeFolder = string.Empty;
    [ObservableProperty] private string _organizePattern = "{Series}/{Series} - {Episode:D3}";
    [ObservableProperty] private ObservableCollection<ProposedOperation> _previewOperations = new();
    [ObservableProperty] private ObservableCollection<FileOperation> _operationHistory = new();
    [ObservableProperty] private ProposedOperation? _selectedPreviewOperation;
    [ObservableProperty] private string _organizeStatusMessage = string.Empty;
    [ObservableProperty] private bool _isOrganizeBusy;

    // --- Merge ---
    [ObservableProperty] private MergeSourceFolder? _selectedMergeFolder;
    [ObservableProperty] private string _mergeDestinationFolder = string.Empty;
    [ObservableProperty] private string _mergePattern = "{Series}/{Series} - {Episode:D3}";
    [ObservableProperty] private ObservableCollection<ProposedOperation> _mergePreviewOperations = new();
    [ObservableProperty] private string _mergeStatusMessage = string.Empty;
    [ObservableProperty] private bool _isMergeBusy;

    // --- Split ---
    [ObservableProperty] private string _splitSourceFolder = string.Empty;
    [ObservableProperty] private string _splitDestinationFolder = string.Empty;
    [ObservableProperty] private string _splitFilePattern = "{Series} - {Episode:D3}";
    [ObservableProperty] private string _splitFolderPattern = "Puntate {Number}";
    [ObservableProperty] private bool _splitNumberingContinue = false;
    [ObservableProperty] private bool _splitUniformMode = true;
    [ObservableProperty] private int _splitUniformEpisodes = 50;
    [ObservableProperty] private ObservableCollection<ProposedOperation> _splitPreviewOperations = new();
    [ObservableProperty] private string _splitStatusMessage = string.Empty;
    [ObservableProperty] private bool _isSplitBusy;

    // --- SFTP ---
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
    [ObservableProperty] private bool _isSftpConnected = false;
    [ObservableProperty] private bool _isSftpBusy = false;

    [ObservableProperty] private string _sftpCurrentRemotePath = "/";
    [ObservableProperty] private ObservableCollection<SftpRemoteEntry> _sftpRemoteEntries = new();
    [ObservableProperty] private SftpRemoteEntry? _sftpSelectedRemoteEntry;
    [ObservableProperty] private bool _isRefreshingRemote;

    [ObservableProperty] private string _sftpUploadLocalFolder = string.Empty;
    [ObservableProperty] private string _sftpUploadRemoteFolder = "/";
    [ObservableProperty] private int _sftpMaxUploadConcurrency = 6;

    // --- SFTP: browser locale ---
    [ObservableProperty] private string _sftpCurrentLocalPath = string.Empty;
    [ObservableProperty] private ObservableCollection<LocalFileEntry> _sftpLocalEntries = new();
    [ObservableProperty] private LocalFileEntry? _sftpSelectedLocalEntry;

    // --- SFTP: download queue ---
    [ObservableProperty] private string _sftpDownloadRemoteFolder = "/";
    [ObservableProperty] private string _sftpDownloadLocalFolder = string.Empty;
    [ObservableProperty] private int _sftpMaxDownloadConcurrency = 6;

    // --- Ordinamento liste SFTP ---
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

    // --- Controllo file vuoti (on-demand) ---
    [ObservableProperty] private bool _isCheckingZeroByte;
    [ObservableProperty] private string _zeroByteCheckStatus = string.Empty;

    // --- Proxy ---
    public ObservableCollection<ProxyConfig> Proxies { get; } = new();

    [ObservableProperty] private ProxyConfig? _selectedProxy;
    [ObservableProperty] private bool _isProxyBusy;
    [ObservableProperty] private string _proxyStatusMessage = string.Empty;

    [ObservableProperty] private int _proxyTestConcurrency = 5;

    private CancellationTokenSource? _proxyTestCts;

    // --- Badge / notifiche ---
    [ObservableProperty] private bool _hasActiveUploads = false;
    [ObservableProperty] private bool _hasActiveDownloads = false;

    // --- Collezioni ---
    public ObservableCollection<MergeSourceFolder> MergeSourceFolders { get; } = new();
    public ObservableCollection<string> SearchHistory { get; } = new();
    public ObservableCollection<SplitSegment> SplitSegments { get; } = new();

    public ObservableCollection<SftpUploadJob> SftpUploadJobs => _sftpUploadQueue.AllJobs;
    public ObservableCollection<SftpDownloadJob> SftpDownloadJobs => _sftpDownloadQueue.AllJobs;

    public ObservableCollection<LocalFileEntry> SelectedLocalEntries { get; } = new();
    public ObservableCollection<SftpRemoteEntry> SelectedRemoteEntries { get; } = new();

    // --- Log globale ---
    public ObservableCollection<LogEntry> Logs => _logService.Logs;

    // --- ETA globali ---
    public string GlobalEtaText
    {
        get
        {
            double totalSpeed = _queueService.AllItems.Sum(i => i.SpeedBytesPerSecond);
            if (totalSpeed <= 0) return "-";

            long totalBytes = _queueService.AllItems.Sum(i => i.TotalBytes);
            long downloaded = _queueService.AllItems.Sum(i => i.DownloadedBytes);
            long remaining = totalBytes - downloaded;

            if (remaining <= 0) return "0s";

            var seconds = remaining / totalSpeed;
            if (seconds < 1) return "<1s";
            if (seconds < 60) return $"{(int)seconds}s";
            if (seconds < 3600) return $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s";
            return $"{(int)(seconds / 3600)}h {(int)((seconds % 3600) / 60)}m";
        }
    }

    public string GlobalSpeedText
    {
        get
        {
            double speed = _queueService.AllItems.Sum(i => i.SpeedBytesPerSecond);
            return speed > 0 ? $"{speed / 1024 / 1024:F2} MB/s" : "-";
        }
    }

    public string GlobalSizeText
    {
        get
        {
            long total = _queueService.AllItems.Sum(i => i.TotalBytes);
            long done = _queueService.AllItems.Sum(i => i.DownloadedBytes);
            return total > 0 ? $"{done / 1024.0 / 1024 / 1024:F2} / {total / 1024.0 / 1024 / 1024:F2} GB" : "-";
        }
    }

    public string OrganizeFolderLabel => OrganizeModeIsLocal ? "Cartella:" : "Cartella remota:";
    public string MergeDestinationLabel => OrganizeModeIsLocal ? "Destinazione:" : "Destinazione remota:";
    public string SplitSourceLabel => OrganizeModeIsLocal ? "Cartella sorgente:" : "Cartella sorgente remota:";
    public string SplitDestinationLabel => OrganizeModeIsLocal ? "Destinazione:" : "Destinazione remota:";

    public string[] NamingPresets { get; } = new[]
    {
        "{OriginalFileName}", "{Episode}", "{Episode:D2}", "{Episode:D3}",
        "{Series} - {Episode:D3}", "{Series} - {Episode:D3} - {Title}"
    };

    public string[] OrganizePatterns { get; } = new[]
    {
        "{Series} - {Episode:D3}", "{Series}/{Series} - {Episode:D3}",
        "{Series}/S01/{Series} - {Episode:D3}", "{Series}/{Episode}",
        "{Series}/{Episode:D2}", "{Series}/{Episode:D3}",
        "{Series}/{Series} - {Episode:D3} - {Title}", "{OriginalFileName}"
    };

    public string[] SplitFolderPatterns { get; } = new[]
    {
        "{Number}", "{Number:D2}", "{Number:D3}", "Puntate {Number}", "Parte {Number}",
        "Puntate {Number} ({Start}-{End})", "Episodi {Start}-{End}", "{Number} - Episodi {Start}-{End}"
    };

    public string PatternPreview
    {
        get
        {
            try
            {
                var preview = FileNameBuilder.ApplyPattern(
                    NamingPattern, "Naruto Shippuden", 1, "Episodio 1", "NarutoShippuden_Ep_001_SUB_ITA");
                return preview + ".mp4";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string OrganizePatternPreview
    {
        get
        {
            try
            {
                var preview = FileNameBuilder.ApplyPattern(
                    OrganizePattern, "Naruto Shippuden", 1, "Episodio 1", "NarutoShippuden_Ep_001_SUB_ITA");
                return preview + ".mp4";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string MergePatternPreview
    {
        get
        {
            try
            {
                var preview = FileNameBuilder.ApplyPattern(
                    MergePattern, "Naruto Shippuden", 1, "Episodio 1", "NarutoShippuden_Ep_001_SUB_ITA");
                return preview + ".mp4";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string SplitFilePatternPreview
    {
        get
        {
            try
            {
                var preview = FileNameBuilder.ApplyPattern(
                    SplitFilePattern, "Naruto Shippuden", 1, "Episodio 1", "NarutoShippuden_Ep_001_SUB_ITA");
                return preview + ".mp4";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string SplitSegmentsSummary
    {
        get
        {
            if (SplitUniformMode)
                return $"Modalità uniforme: {SplitUniformEpisodes} episodi per sottocartella.";

            if (SplitSegments.Count == 0)
                return "Nessun segmento definito.";

            int total = SplitSegments.Sum(s => s.EpisodeCount);
            return $"{SplitSegments.Count} segmenti, {total} episodi programmati.";
        }
    }

    public string ActiveProxyBadgeText => Proxies.Any(p => p.IsEnabled)
        ? $"🌐 {Proxies.First(p => p.IsEnabled).Name}"
        : "🌐 off";

    public bool IsProxyActive => Proxies.Any(p => p.IsEnabled);

    // Chiama OnPropertyChanged manualmente in ApplyActiveProxy:

    public ObservableCollection<DownloadItem> Downloads => _queueService.AllItems;
    public ObservableCollection<SeriesGroup> SeriesGroups { get; } = new();

    public MainWindowViewModel(
        IAnimeWorldService animeWorldService,
        DownloadQueueService queueService,
        FileNameBuilder fileNameBuilder,
        SettingsService settings,
        IFileOrganizerService fileOrganizer,
        SftpConfigRepository sftpRepository,
        ISftpService sftpService,
        SftpUploadQueueService sftpUploadQueue,
        SftpDownloadQueueService sftpDownloadQueue,
        DownloadManager.Services.Proxy.ProxyService proxyService,
        LogService logService)
    {
        _animeWorldService = animeWorldService;
        _queueService = queueService;
        _fileNameBuilder = fileNameBuilder;
        _settings = settings;
        _fileOrganizer = fileOrganizer;
        _sftpRepository = sftpRepository;
        _sftpService = sftpService;
        _sftpUploadQueue = sftpUploadQueue;
        _sftpDownloadQueue = sftpDownloadQueue;
        _proxyService = proxyService;
        _logService = logService;

        MaxConcurrency = _settings.Current.MaxConcurrency;
        _queueService.MaxConcurrency = MaxConcurrency;

        NamingPattern = _settings.Current.NamingPattern;
        BaseDownloadFolder = _settings.Current.BaseDownloadFolder;
        CreateSeriesFolder = _settings.Current.CreateSeriesFolder;
        ThemeVariant = _settings.Current.ThemeVariant;

        OrganizeFolder = _settings.Current.BaseDownloadFolder;
        MergeDestinationFolder = _settings.Current.BaseDownloadFolder;
        SplitDestinationFolder = _settings.Current.BaseDownloadFolder;

        foreach (var term in _settings.Current.SearchHistory)
            SearchHistory.Add(term);

        _queueService.AllItems.CollectionChanged += OnAllItemsChanged;
        _sftpUploadQueue.QueueChanged += OnUploadQueueChanged;
        _sftpDownloadQueue.QueueChanged += UpdateSftpBadges;

        foreach (var item in _queueService.AllItems)
            AddToGroup(item);

        // Ordinamento default
        LocalSortOption = SortOptions[0];   // Nome
        RemoteSortOption = SortOptions[0];  // Nome

        _statsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _statsTimer.Tick += (s, e) =>
        {
            // FIX: aggiorna SEMPRE tutti i gruppi, altrimenti quando l'ultimo item
            // passa a Completed il gruppo resta bloccato sull'ultimo valore "attivo".
            foreach (var g in SeriesGroups)
                g.RefreshStats();

            OnPropertyChanged(nameof(GlobalEtaText));
            OnPropertyChanged(nameof(GlobalSpeedText));
            OnPropertyChanged(nameof(GlobalSizeText));
        };
        _statsTimer.Start();

        // Auto-refresh locale (ogni 3s)
        _sftpLocalAutoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _sftpLocalAutoRefreshTimer.Tick += async (s, e) =>
        {
            try { await SftpAutoRefreshLocalAsync(); } catch { }
        };
        _sftpLocalAutoRefreshTimer.Start();

        // Auto-refresh remoto (ogni 5s)
        _sftpRemoteAutoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _sftpRemoteAutoRefreshTimer.Tick += async (s, e) =>
        {
            try { await SftpAutoRefreshRemoteAsync(); } catch { }
        };
        _sftpRemoteAutoRefreshTimer.Start();

        AddSplitSegment();

        _sftpUploadQueue.MaxConcurrency = SftpMaxUploadConcurrency;
        _sftpDownloadQueue.MaxConcurrency = SftpMaxDownloadConcurrency;

        _ = Task.Run(LoadHistoryAsync);
        _ = Task.Run(SftpLoadAllProfilesAsync);

        _ = Task.Run(ProxyLoadAllAsync);

        SftpCurrentLocalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        _ = Task.Run(SftpRefreshLocalAsync);
    }

    // ============================================================
    //  PROPERTY CHANGED HANDLERS
    // ============================================================

    partial void OnMaxConcurrencyChanged(int value)
    {
        if (_queueService != null) _queueService.MaxConcurrency = value;
        if (_settings != null) { _settings.Current.MaxConcurrency = value; _settings.Save(); }
    }

    partial void OnNamingPatternChanged(string value) => OnPropertyChanged(nameof(PatternPreview));
    partial void OnOrganizePatternChanged(string value) => OnPropertyChanged(nameof(OrganizePatternPreview));
    partial void OnMergePatternChanged(string value) => OnPropertyChanged(nameof(MergePatternPreview));
    partial void OnSplitFilePatternChanged(string value) => OnPropertyChanged(nameof(SplitFilePatternPreview));
    partial void OnSplitUniformModeChanged(bool value) => OnPropertyChanged(nameof(SplitSegmentsSummary));
    partial void OnSplitUniformEpisodesChanged(int value) => OnPropertyChanged(nameof(SplitSegmentsSummary));

    partial void OnLocalSortOptionChanged(SortOption value)
    {
        if (value == null) return;
        _ = SftpRefreshLocalInternalAsync(silent: true);
    }

    partial void OnLocalSortAscendingChanged(bool value)
    {
        _ = SftpRefreshLocalInternalAsync(silent: true);
    }

    partial void OnRemoteSortOptionChanged(SortOption value)
    {
        if (value == null) return;
        _ = SftpRefreshInternalAsync(silent: true);
    }

    partial void OnRemoteSortAscendingChanged(bool value)
    {
        _ = SftpRefreshInternalAsync(silent: true);
    }

    partial void OnOrganizeModeIsLocalChanged(bool value)
    {
        OnPropertyChanged(nameof(OrganizeFolderLabel));
        OnPropertyChanged(nameof(MergeDestinationLabel));
        OnPropertyChanged(nameof(SplitSourceLabel));
        OnPropertyChanged(nameof(SplitDestinationLabel));

        if (!value && IsSftpConnected && !string.IsNullOrWhiteSpace(SftpCurrentRemotePath))
        {
            OrganizeFolder = SftpCurrentRemotePath;
            MergeDestinationFolder = SftpCurrentRemotePath;
            SplitSourceFolder = SftpCurrentRemotePath;
            SplitDestinationFolder = SftpCurrentRemotePath;
        }
    }

    partial void OnIsSftpConnectedChanged(bool value)
    {
        if (!value) OrganizeModeIsLocal = true;
    }

    partial void OnThemeVariantChanged(string value)
    {
        App.ApplyTheme(value);

        if (_settings != null)
        {
            _settings.Current.ThemeVariant = value;
            _settings.Save();
        }
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

    private void UpdateSftpBadges()
    {
        HasActiveUploads = _sftpUploadQueue.AllJobs.Any(j =>
            j.Status == SftpJobStatus.Uploading || j.Status == SftpJobStatus.Pending);

        HasActiveDownloads = _sftpDownloadQueue.AllJobs.Any(j =>
            j.Status == SftpJobStatus.Downloading || j.Status == SftpJobStatus.Pending);
    }

    private int _lastCompletedUploads = 0;

    private void OnUploadQueueChanged()
    {
        UpdateSftpBadges();

        var completed = _sftpUploadQueue.AllJobs.Count(j => j.Status == SftpJobStatus.Completed);
        if (completed > _lastCompletedUploads)
        {
            _lastCompletedUploads = completed;

            _ = Task.Run(async () =>
            {
                await SftpRefreshInternalAsync(silent: true);
                await Dispatcher.UIThread.InvokeAsync(RefreshZeroByteFlagsAsync);
            });
        }
    }

    private void OnAllItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            SeriesGroups.Clear();
            return;
        }

        if (e.NewItems != null)
            foreach (DownloadItem item in e.NewItems)
                AddToGroup(item);

        if (e.OldItems != null)
            foreach (DownloadItem item in e.OldItems)
                RemoveFromGroup(item);
    }

    private void AddToGroup(DownloadItem item)
    {
        var seriesName = string.IsNullOrWhiteSpace(item.SeriesName) ? "Senza serie" : item.SeriesName;

        var group = SeriesGroups.FirstOrDefault(g => g.SeriesName == seriesName);
        if (group == null)
        {
            group = new SeriesGroup { SeriesName = seriesName, IsExpanded = false };
            SeriesGroups.Add(group);
        }

        if (!group.Items.Contains(item))
            group.Items.Add(item);
    }

    private void RemoveFromGroup(DownloadItem item)
    {
        var seriesName = string.IsNullOrWhiteSpace(item.SeriesName) ? "Senza serie" : item.SeriesName;

        var group = SeriesGroups.FirstOrDefault(g => g.SeriesName == seriesName);
        if (group != null)
        {
            group.Items.Remove(item);
            if (group.Items.Count == 0)
                SeriesGroups.Remove(group);
        }
    }

    // ============================================================
    //  LOG
    // ============================================================

    [RelayCommand]
    private void ClearLogs() => _logService.Clear();

    // ============================================================
    //  RICERCA
    // ============================================================

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            StatusMessage = "Inserisci un termine di ricerca.";
            return;
        }
        await SearchWithTermAsync(SearchQuery);
    }

    public async Task SearchWithTermAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) { StatusMessage = "Inserisci un termine."; return; }

        SearchQuery = term;
        SaveSearchToHistory(term.Trim());

        try
        {
            StatusMessage = "Inizializzazione browser...";
            await _animeWorldService.InitializeAsync(headless: true);

            StatusMessage = $"Ricerca in corso per '{term}'...";
            var results = await _animeWorldService.SearchAsync(term);

            SearchResults.Clear();
            foreach (var result in results) SearchResults.Add(result);

            StatusMessage = $"Trovati {results.Count} risultati per '{term}'.";
        }
        catch (Exception ex) { StatusMessage = $"Errore: {ex.Message}"; }
    }

    [RelayCommand]
    private void ClearSearchHistory()
    {
        SearchHistory.Clear();
        _settings.Current.SearchHistory = new List<string>();
        _settings.Save();
        StatusMessage = "Cronologia ricerche svuotata.";
    }

    public void SetSearchQueryAndSearch(string query) => _ = SearchWithTermAsync(query);

    private void SaveSearchToHistory(string term)
    {
        if (string.IsNullOrEmpty(term)) return;

        var existing = SearchHistory.FirstOrDefault(h =>
            string.Equals(h, term, StringComparison.OrdinalIgnoreCase));
        if (existing != null) SearchHistory.Remove(existing);

        SearchHistory.Insert(0, term);

        while (SearchHistory.Count > 20)
            SearchHistory.RemoveAt(SearchHistory.Count - 1);

        _settings.Current.SearchHistory = SearchHistory.ToList();
        _settings.Save();
    }

    [RelayCommand]
    private async Task LoadEpisodesAsync(AnimeSearchResult? anime)
    {
        if (anime == null) return;

        try
        {
            SelectedAnime = anime;
            StatusMessage = $"Caricamento episodi di '{anime.Name}'...";

            var episodes = await _animeWorldService.GetEpisodesAsync(anime.Link);

            Episodes.Clear();
            foreach (var ep in episodes) Episodes.Add(ep);

            StatusMessage = $"Trovati {episodes.Count} episodi per '{anime.Name}'.";
        }
        catch (Exception ex) { StatusMessage = $"Errore: {ex.Message}"; }
    }

    // ============================================================
    //  DOWNLOAD ANIME
    // ============================================================

    [RelayCommand]
    private async Task DownloadEpisodeAsync(Episode? episode)
    {
        if (episode == null) return;
        await DownloadEpisodeInternalAsync(episode);
    }

    private async Task DownloadEpisodeInternalAsync(Episode episode, CancellationToken ct = default)
    {
        _currentEpisode = episode;

        ct.ThrowIfCancellationRequested();
        var videoUrl = await _animeWorldService.GetVideoUrlAsync(episode.Link);

        if (string.IsNullOrEmpty(videoUrl)) return;
        ct.ThrowIfCancellationRequested();

        var episodeNumber = int.TryParse(episode.Number, out var n) ? n : 0;

        var item = new DownloadItem
        {
            Url = videoUrl,
            SeriesName = SelectedAnime?.Name ?? "",
            EpisodeNumber = episodeNumber,
            EpisodeTitle = episode.Title,
            Status = DownloadStatus.Pending
        };

        _settings.Current.NamingPattern = NamingPattern;
        _settings.Current.BaseDownloadFolder = BaseDownloadFolder;
        _settings.Current.CreateSeriesFolder = CreateSeriesFolder;

        item.DestinationPath = _fileNameBuilder.BuildDestinationPath(item, videoUrl, episode.Title);

        await _queueService.EnqueueAsync(item);
    }

    [RelayCommand]
    private async Task DownloadSelectedEpisodesAsync()
    {
        var selected = Episodes.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0) { StatusMessage = "Nessun episodio selezionato."; return; }
        if (IsBatchActive) { StatusMessage = "Un accodamento è già in corso."; return; }

        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;
        IsBatchActive = true;

        int queued = 0, skipped = 0, errors = 0;

        try
        {
            var seriesName = SelectedAnime?.Name ?? "";
            var toDownload = new List<Episode>();

            foreach (var ep in selected)
            {
                var epNum = int.TryParse(ep.Number, out var n) ? n : 0;
                if (_queueService.ContainsEpisode(seriesName, epNum)) { skipped++; continue; }
                toDownload.Add(ep);
            }

            if (toDownload.Count == 0)
            {
                StatusMessage = $"Tutti i {skipped} episodi selezionati sono già in coda.";
                return;
            }

            StatusMessage = $"Recupero URL di {toDownload.Count} episodi...";
            var urls = await _animeWorldService.GetVideoUrlsBatchAsync(toDownload, token);

            foreach (var episode in toDownload)
            {
                token.ThrowIfCancellationRequested();

                if (!urls.TryGetValue(episode.Link, out var videoUrl) || string.IsNullOrEmpty(videoUrl))
                { errors++; continue; }

                var epNum = int.TryParse(episode.Number, out var n) ? n : 0;

                var item = new DownloadItem
                {
                    Url = videoUrl,
                    SeriesName = seriesName,
                    EpisodeNumber = epNum,
                    EpisodeTitle = episode.Title,
                    Status = DownloadStatus.Pending
                };

                _settings.Current.NamingPattern = NamingPattern;
                _settings.Current.BaseDownloadFolder = BaseDownloadFolder;
                _settings.Current.CreateSeriesFolder = CreateSeriesFolder;

                item.DestinationPath = _fileNameBuilder.BuildDestinationPath(item, videoUrl, episode.Title);

                await _queueService.EnqueueAsync(item);
                queued++;
                StatusMessage = $"Accodati: {queued}/{toDownload.Count}";
            }

            var msg = $"{queued} episodi accodati";
            if (skipped > 0) msg += $", {skipped} già in coda";
            if (errors > 0) msg += $", {errors} falliti";
            StatusMessage = msg + ".";
        }
        catch (OperationCanceledException) { StatusMessage = $"Accodamento annullato ({queued})."; }
        catch (Exception ex) { StatusMessage = $"Errore: {ex.Message}"; }
        finally
        {
            _batchCts?.Dispose();
            _batchCts = null;
            IsBatchActive = false;
        }
    }

    [RelayCommand]
    private void CancelBatch() { _batchCts?.Cancel(); StatusMessage = "Annullamento accodamento..."; }

    [RelayCommand]
    private void SelectAllEpisodes() { foreach (var ep in Episodes) ep.IsSelected = true; StatusMessage = $"Selezionati {Episodes.Count} episodi."; }

    [RelayCommand]
    private void DeselectAllEpisodes() { foreach (var ep in Episodes) ep.IsSelected = false; StatusMessage = "Deselezionati."; }

    [RelayCommand]
    private void InvertEpisodeSelection() { foreach (var ep in Episodes) ep.IsSelected = !ep.IsSelected; StatusMessage = "Selezione invertita."; }

    // ============================================================
    //  CONTROLLI GLOBALI CODA ANIME
    // ============================================================

    [RelayCommand]
    private void StartAll()
    {
        var count = _queueService.AllItems.Count(i =>
            i.Status == DownloadStatus.Pending || i.Status == DownloadStatus.Paused || i.Status == DownloadStatus.Failed);

        if (count == 0) { StatusMessage = "Nessun download da avviare."; return; }
        _queueService.StartAll();
        StatusMessage = $"Avvio di {count} download.";
    }

    [RelayCommand]
    private void PauseAll()
    {
        _batchCts?.Cancel();
        var active = _queueService.ActiveCount;
        var pending = _queueService.PendingCount;
        if (active == 0 && pending == 0) { StatusMessage = "Nessun download in corso o in attesa."; return; }
        _queueService.PauseAll();
        StatusMessage = $"In pausa {active} download attivi.";
    }

    [RelayCommand]
    private void CancelAllDownloads()
    {
        var cancellable = _queueService.AllItems.Count(i =>
            i.Status == DownloadStatus.Downloading ||
            i.Status == DownloadStatus.Pending ||
            i.Status == DownloadStatus.Paused ||
            i.Status == DownloadStatus.Failed);

        if (cancellable == 0)
        {
            StatusMessage = "Nessun download da annullare.";
            return;
        }

        _queueService.CancelAll();
        StatusMessage = $"Annullati {cancellable} download.";
    }

    [RelayCommand]
    private async Task DeleteEverythingAsync()
    {
        var count = _queueService.AllItems.Count;
        if (count == 0) { StatusMessage = "La lista è già vuota."; return; }
        await _queueService.DeleteEverythingAsync();
        StatusMessage = $"Eliminati {count} download.";
    }

    [RelayCommand]
    private async Task DeleteAllAsync()
    {
        _batchCts?.Cancel();
        await Task.Delay(300);
        var count = _queueService.AllItems.Count;
        if (count == 0) { StatusMessage = "La lista è già vuota."; return; }
        await _queueService.DeleteAllAsync();
        StatusMessage = $"Rimossi {count} download.";
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        var completed = _queueService.AllItems.Count(d => d.Status == DownloadStatus.Completed);
        if (completed == 0) { StatusMessage = "Nessun completato."; return; }
        _queueService.ClearCompleted();
        StatusMessage = $"Rimossi {completed} completati.";
    }

    [RelayCommand]
    private void ClearCancelledDownloads()
    {
        var cancelled = _queueService.AllItems.Count(d => d.Status == DownloadStatus.Cancelled);
        if (cancelled == 0) { StatusMessage = "Nessun annullato."; return; }
        _queueService.ClearCancelled();
        StatusMessage = $"Rimossi {cancelled} annullati.";
    }

    [RelayCommand]
    private void ClearFailedDownloads()
    {
        var failed = _queueService.AllItems.Count(d => d.Status == DownloadStatus.Failed);
        if (failed == 0) { StatusMessage = "Nessun failed."; return; }
        _queueService.ClearFailed();
        StatusMessage = $"Rimossi {failed} failed.";
    }

    // ============================================================
    //  CONTROLLI ITEM / GRUPPO ANIME
    // ============================================================

    public void PauseItemPublic(DownloadItem item) => _queueService.PauseItem(item);
    public void ResumeItemPublic(DownloadItem item) => _queueService.ResumeItem(item);
    public void ForceItemPublic(DownloadItem item) => _queueService.ForceQueueItem(item);
    public void UnforceItemPublic(DownloadItem item) => _queueService.UnforceQueueItem(item);
    public void CancelItemPublic(DownloadItem item) => _queueService.CancelItem(item);
    public Task RemoveItemPublicAsync(DownloadItem item) => _queueService.RemoveItemAsync(item);

    public void PauseGroupPublic(SeriesGroup group) { foreach (var item in group.Items.ToList()) _queueService.PauseItem(item); }
    public void ResumeGroupPublic(SeriesGroup group) { foreach (var item in group.Items.ToList()) _queueService.ResumeItem(item); }
    public void ForceGroupPublic(SeriesGroup group) => _queueService.ForceQueueGroup(group);
    public void UnforceGroupPublic(SeriesGroup group) => _queueService.UnforceQueueGroup(group);

    public void CancelGroupPublic(SeriesGroup group)
    {
        _queueService.CancelGroup(group);
        StatusMessage = $"Annullata la serie: {group.SeriesName}";
    }

    public async Task RemoveGroupPublicAsync(SeriesGroup group)
    {
        foreach (var item in group.Items.ToList())
            await _queueService.RemoveItemAsync(item);
    }

    // ============================================================
    //  IMPOSTAZIONI
    // ============================================================

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.Current.NamingPattern = NamingPattern;
        _settings.Current.BaseDownloadFolder = BaseDownloadFolder;
        _settings.Current.CreateSeriesFolder = CreateSeriesFolder;
        _settings.Current.MaxConcurrency = MaxConcurrency;
        _settings.Current.ThemeVariant = ThemeVariant;
        _settings.Save();
        StatusMessage = "Impostazioni salvate.";
    }

    // ============================================================
    //  Aggiornamenti
    // ============================================================

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        try
        {
            StatusMessage = "Controllo aggiornamenti...";
            System.Diagnostics.Debug.WriteLine("[UPDATE] Avvio check aggiornamenti...");

            var mgr = new Velopack.UpdateManager(
                new Velopack.Sources.GithubSource(
                    "https://github.com/Sert-X/ddl-download-manager",
                    accessToken: null,
                    prerelease: false));

            var update = await mgr.CheckForUpdatesAsync();

            if (update == null)
            {
                StatusMessage = "Nessun aggiornamento disponibile.";
                System.Diagnostics.Debug.WriteLine("[UPDATE] Nessun aggiornamento disponibile");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[UPDATE] Trovata versione {update.TargetFullRelease.Version}");
            StatusMessage = $"Aggiornamento {update.TargetFullRelease.Version} disponibile. Download in corso...";

            await mgr.DownloadUpdatesAsync(update);

            System.Diagnostics.Debug.WriteLine("[UPDATE] Download completato, riavvio...");
            mgr.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore update: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[UPDATE] ECCEZIONE: {ex}");
        }
    }

    // ============================================================
    //  ORGANIZZA — ROUTER LOCALE/REMOTO
    // ============================================================

    [RelayCommand]
    private async Task PreviewOrganizeAsync()
    {
        if (OrganizeModeIsLocal) await PreviewOrganizeLocalInternalAsync();
        else await PreviewOrganizeRemoteInternalAsync();
    }

    [RelayCommand]
    private async Task ApplyOrganizeAsync()
    {
        if (OrganizeModeIsLocal) await ApplyOrganizeLocalInternalAsync();
        else await ApplyOrganizeRemoteInternalAsync();
    }

    [RelayCommand]
    private async Task UndoLastBatchAsync()
    {
        if (OrganizeModeIsLocal) await UndoLastLocalBatchInternalAsync();
        else await UndoLastRemoteBatchInternalAsync();
    }

    // ============================================================
    //  ORGANIZZA — LOCALE
    // ============================================================

    private async Task PreviewOrganizeLocalInternalAsync()
    {
        if (string.IsNullOrWhiteSpace(OrganizeFolder)) { OrganizeStatusMessage = "Seleziona una cartella."; return; }
        if (!Directory.Exists(OrganizeFolder)) { OrganizeStatusMessage = "La cartella non esiste."; return; }

        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Analisi in corso...";
            var ops = await _fileOrganizer.PreviewAsync(OrganizeFolder, OrganizePattern, false);

            PreviewOperations.Clear();
            foreach (var op in ops) PreviewOperations.Add(op);

            var conflicts = ops.Count(o => o.HasConflict);
            OrganizeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni proposte."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    private async Task ApplyOrganizeLocalInternalAsync()
    {
        if (PreviewOperations.Count == 0) { OrganizeStatusMessage = "Nessuna operazione."; return; }
        var conflicts = PreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { OrganizeStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Applicazione...";
            var count = await _fileOrganizer.ApplyAsync(PreviewOperations.ToList());
            OrganizeStatusMessage = $"Applicate {count} operazioni.";
            PreviewOperations.Clear();
            await LoadHistoryAsync();
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    private async Task UndoLastLocalBatchInternalAsync()
    {
        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Undo...";
            var count = await _fileOrganizer.UndoLastBatchAsync();
            OrganizeStatusMessage = count == 0 ? "Niente da annullare." : $"Annullate {count} operazioni.";
            await LoadHistoryAsync();
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    // ============================================================
    //  ORGANIZZA — REMOTO
    // ============================================================

    private async Task PreviewOrganizeRemoteInternalAsync()
    {
        if (!IsSftpConnected) { OrganizeStatusMessage = "Non connesso."; return; }
        if (string.IsNullOrWhiteSpace(OrganizeFolder)) { OrganizeStatusMessage = "Seleziona una cartella remota."; return; }

        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Analisi remota in corso...";

            var ops = await _fileOrganizer.PreviewRemoteFolderAsync(
                OrganizeFolder, OrganizePattern, CreateSeriesFolder);

            PreviewOperations.Clear();
            foreach (var op in ops) PreviewOperations.Add(op);

            var conflicts = ops.Count(o => o.HasConflict);
            OrganizeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni (remoto)."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    private async Task ApplyOrganizeRemoteInternalAsync()
    {
        if (PreviewOperations.Count == 0) { OrganizeStatusMessage = "Nessuna operazione."; return; }
        var conflicts = PreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { OrganizeStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Applicazione remota...";

            var count = await _fileOrganizer.ApplyRemoteAsync(PreviewOperations.ToList());
            OrganizeStatusMessage = $"Applicate {count} operazioni.";

            PreviewOperations.Clear();
            await SftpRefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    private async Task UndoLastRemoteBatchInternalAsync()
    {
        if (!IsSftpConnected) { OrganizeStatusMessage = "Non connesso."; return; }

        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Undo remoto...";

            var count = await _fileOrganizer.UndoLastRemoteBatchAsync();

            OrganizeStatusMessage = count == 0
                ? "Niente da annullare (remoto)."
                : $"Annullate {count} operazioni.";

            await SftpRefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    [RelayCommand]
    private void ClearPreviewOperations() { PreviewOperations.Clear(); OrganizeStatusMessage = "Proposte svuotate."; }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try
        {
            await _fileOrganizer.ClearHistoryAsync();
            OperationHistory.Clear();
            OrganizeStatusMessage = "Cronologia svuotata.";
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
    }

    public async Task LoadHistoryAsync()
    {
        try
        {
            var history = await _fileOrganizer.GetHistoryAsync(200);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OperationHistory.Clear();
                foreach (var op in history) OperationHistory.Add(op);
            });
        }
        catch (Exception ex) { Console.WriteLine($">>> Errore cronologia: {ex.Message}"); }
    }

    public void SetOrganizeFolder(string folder) => OrganizeFolder = folder;

    public void SetBaseDownloadFolder(string folder)
    {
        BaseDownloadFolder = folder;
        _settings.Current.BaseDownloadFolder = folder;
        _settings.Save();
    }

    /// <summary>
    /// Ricontrolla silenziosamente l'evidenziazione 0-byte delle cartelle visibili,
    /// aggiornando anche la cache. Chiamato dopo un upload che potrebbe aver
    /// cambiato lo stato delle cartelle.
    /// </summary>
    private async Task RefreshZeroByteFlagsAsync()
    {
        if (!IsSftpConnected) return;

        var folders = SftpRemoteEntries.Where(e => e.IsDirectory).ToList();
        if (folders.Count == 0) return;

        foreach (var folder in folders)
        {
            if (!IsSftpConnected) return;
            try
            {
                var subEntries = await _sftpService.ListDirectoryAsync(folder.FullPath);
                bool hasZero = subEntries.Any(e => !e.IsDirectory && e.Size == 0);

                _zeroByteFolderCache[folder.FullPath] = hasZero;
                folder.HasZeroByteIssue = hasZero;
            }
            catch { }
        }
    }

    // ============================================================
    //  MERGE — ROUTER
    // ============================================================

    [RelayCommand]
    private async Task PreviewMergeAsync()
    {
        if (OrganizeModeIsLocal) await PreviewMergeLocalInternalAsync();
        else await PreviewMergeRemoteInternalAsync();
    }

    [RelayCommand]
    private async Task ApplyMergeAsync()
    {
        if (OrganizeModeIsLocal) await ApplyMergeLocalInternalAsync();
        else await ApplyMergeRemoteInternalAsync();
    }

    public void AddMergeFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        if (MergeSourceFolders.Any(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase))) return;

        var item = new MergeSourceFolder { Path = folder, Priority = MergeSourceFolders.Count };
        MergeSourceFolders.Add(item);
        RenumberPriorities();
    }

    public void RemoveMergeFolder(MergeSourceFolder folder) { MergeSourceFolders.Remove(folder); RenumberPriorities(); }

    public void MoveMergeFolderUp(MergeSourceFolder folder)
    {
        var idx = MergeSourceFolders.IndexOf(folder);
        if (idx <= 0) return;
        MergeSourceFolders.Move(idx, idx - 1);
        RenumberPriorities();
    }

    public void MoveMergeFolderDown(MergeSourceFolder folder)
    {
        var idx = MergeSourceFolders.IndexOf(folder);
        if (idx < 0 || idx >= MergeSourceFolders.Count - 1) return;
        MergeSourceFolders.Move(idx, idx + 1);
        RenumberPriorities();
    }

    public void ClearMergeFolders()
    {
        MergeSourceFolders.Clear();
        MergePreviewOperations.Clear();
        MergeStatusMessage = string.Empty;
    }

    private void RenumberPriorities()
    {
        for (int i = 0; i < MergeSourceFolders.Count; i++)
            MergeSourceFolders[i].Priority = i;
    }

    public void SetMergeDestinationFolder(string folder) => MergeDestinationFolder = folder;

    private async Task PreviewMergeLocalInternalAsync()
    {
        if (MergeSourceFolders.Count == 0) { MergeStatusMessage = "Aggiungi almeno una cartella."; return; }
        if (string.IsNullOrWhiteSpace(MergeDestinationFolder)) { MergeStatusMessage = "Seleziona una destinazione."; return; }

        try
        {
            IsMergeBusy = true;
            MergeStatusMessage = "Analisi...";
            var ops = await _fileOrganizer.PreviewMergeAsync(
                MergeSourceFolders.ToList(), MergeDestinationFolder, MergePattern, false);

            MergePreviewOperations.Clear();
            foreach (var op in ops) MergePreviewOperations.Add(op);

            var conflicts = ops.Count(o => o.HasConflict);
            MergeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { MergeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsMergeBusy = false; }
    }

    private async Task ApplyMergeLocalInternalAsync()
    {
        if (MergePreviewOperations.Count == 0) { MergeStatusMessage = "Nessuna operazione."; return; }
        var conflicts = MergePreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { MergeStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsMergeBusy = true;
            MergeStatusMessage = "Applicazione...";
            var count = await _fileOrganizer.ApplyAsync(MergePreviewOperations.ToList());
            MergeStatusMessage = $"Applicate {count} operazioni.";
            MergePreviewOperations.Clear();
            await LoadHistoryAsync();
        }
        catch (Exception ex) { MergeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsMergeBusy = false; }
    }

    private async Task PreviewMergeRemoteInternalAsync()
    {
        if (!IsSftpConnected) { MergeStatusMessage = "Non connesso."; return; }
        if (MergeSourceFolders.Count == 0) { MergeStatusMessage = "Aggiungi almeno una cartella."; return; }
        if (string.IsNullOrWhiteSpace(MergeDestinationFolder)) { MergeStatusMessage = "Seleziona una destinazione remota."; return; }

        try
        {
            IsMergeBusy = true;
            MergeStatusMessage = "Analisi remota...";
            var ops = await _fileOrganizer.PreviewRemoteMergeAsync(
                MergeSourceFolders.ToList(), MergeDestinationFolder, MergePattern, CreateSeriesFolder);

            MergePreviewOperations.Clear();
            foreach (var op in ops) MergePreviewOperations.Add(op);

            var conflicts = ops.Count(o => o.HasConflict);
            MergeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni (remoto)."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { MergeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsMergeBusy = false; }
    }

    private async Task ApplyMergeRemoteInternalAsync()
    {
        if (MergePreviewOperations.Count == 0) { MergeStatusMessage = "Nessuna operazione."; return; }
        var conflicts = MergePreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { MergeStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsMergeBusy = true;
            MergeStatusMessage = "Applicazione remota...";
            var count = await _fileOrganizer.ApplyRemoteAsync(MergePreviewOperations.ToList());
            MergeStatusMessage = $"Applicate {count} operazioni.";
            MergePreviewOperations.Clear();
            await SftpRefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { MergeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsMergeBusy = false; }
    }

    [RelayCommand]
    private void ClearMergePreviewOperations() { MergePreviewOperations.Clear(); MergeStatusMessage = "Proposte svuotate."; }

    // ============================================================
    //  SPLIT — ROUTER
    // ============================================================

    [RelayCommand]
    private async Task PreviewSplitAsync()
    {
        if (OrganizeModeIsLocal) await PreviewSplitLocalInternalAsync();
        else await PreviewSplitRemoteInternalAsync();
    }

    [RelayCommand]
    private async Task ApplySplitAsync()
    {
        if (OrganizeModeIsLocal) await ApplySplitLocalInternalAsync();
        else await ApplySplitRemoteInternalAsync();
    }

    public void SetSplitSourceFolder(string folder) => SplitSourceFolder = folder;
    public void SetSplitDestinationFolder(string folder) => SplitDestinationFolder = folder;

    [RelayCommand]
    private void AddSplitSegment()
    {
        var seg = new SplitSegment { EpisodeCount = 50 };
        seg.PropertyChanged += OnSplitSegmentPropertyChanged;
        SplitSegments.Add(seg);
        RenumberSegments();
        OnPropertyChanged(nameof(SplitSegmentsSummary));
    }

    [RelayCommand]
    private void RemoveSplitSegment(SplitSegment? segment)
    {
        if (segment == null) return;
        segment.PropertyChanged -= OnSplitSegmentPropertyChanged;
        SplitSegments.Remove(segment);
        RenumberSegments();
        OnPropertyChanged(nameof(SplitSegmentsSummary));
    }

    [RelayCommand]
    private void ClearSplitSegments()
    {
        foreach (var seg in SplitSegments) seg.PropertyChanged -= OnSplitSegmentPropertyChanged;
        SplitSegments.Clear();
        OnPropertyChanged(nameof(SplitSegmentsSummary));
    }

    private void OnSplitSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SplitSegment.EpisodeCount))
            OnPropertyChanged(nameof(SplitSegmentsSummary));
    }

    private void RenumberSegments()
    {
        for (int i = 0; i < SplitSegments.Count; i++) SplitSegments[i].Index = i + 1;
    }

    private List<int>? BuildSegmentSizesLocal()
    {
        if (SplitUniformMode)
        {
            if (SplitUniformEpisodes <= 0) { SplitStatusMessage = "Numero episodi non valido."; return null; }

            var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".m4v" };

            int fileCount = Directory.GetFiles(SplitSourceFolder, "*", SearchOption.TopDirectoryOnly)
                .Count(f => videoExts.Contains(Path.GetExtension(f)));

            if (fileCount == 0) { SplitStatusMessage = "Nessun file video."; return null; }

            int segmentCount = (int)Math.Ceiling((double)fileCount / SplitUniformEpisodes);
            var sizes = new List<int>();
            for (int i = 0; i < segmentCount; i++) sizes.Add(SplitUniformEpisodes);
            return sizes;
        }
        else
        {
            if (SplitSegments.Count == 0) { SplitStatusMessage = "Aggiungi un segmento."; return null; }
            var sizes = SplitSegments.Where(s => s.EpisodeCount > 0).Select(s => s.EpisodeCount).ToList();
            if (sizes.Count == 0) { SplitStatusMessage = "Tutti segmenti a 0."; return null; }
            return sizes;
        }
    }

    private async Task PreviewSplitLocalInternalAsync()
    {
        if (string.IsNullOrWhiteSpace(SplitSourceFolder)) { SplitStatusMessage = "Seleziona una cartella."; return; }
        if (!Directory.Exists(SplitSourceFolder)) { SplitStatusMessage = "Cartella non esiste."; return; }
        if (string.IsNullOrWhiteSpace(SplitDestinationFolder)) SplitDestinationFolder = SplitSourceFolder;

        var segmentSizes = BuildSegmentSizesLocal();
        if (segmentSizes == null) return;

        try
        {
            IsSplitBusy = true;
            SplitStatusMessage = "Analisi...";
            var mode = SplitNumberingContinue ? SplitNumberingMode.Continue : SplitNumberingMode.Reset;

            var ops = await _fileOrganizer.PreviewSplitAsync(
                SplitSourceFolder, SplitDestinationFolder, segmentSizes,
                SplitFilePattern, SplitFolderPattern, mode);

            SplitPreviewOperations.Clear();
            foreach (var op in ops) SplitPreviewOperations.Add(op);

            var conflicts = ops.Count(o => o.HasConflict);
            var folderCount = ops.Select(o => Path.GetDirectoryName(o.NewPath) ?? "").Distinct().Count();

            SplitStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni in {folderCount} sottocartelle."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { SplitStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsSplitBusy = false; }
    }

    private async Task ApplySplitLocalInternalAsync()
    {
        if (SplitPreviewOperations.Count == 0) { SplitStatusMessage = "Nessuna operazione."; return; }
        var conflicts = SplitPreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { SplitStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsSplitBusy = true;
            SplitStatusMessage = "Applicazione...";
            var count = await _fileOrganizer.ApplyAsync(SplitPreviewOperations.ToList());
            SplitStatusMessage = $"Applicate {count} operazioni.";
            SplitPreviewOperations.Clear();
            await LoadHistoryAsync();
        }
        catch (Exception ex) { SplitStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsSplitBusy = false; }
    }

    private async Task PreviewSplitRemoteInternalAsync()
    {
        if (!IsSftpConnected) { SplitStatusMessage = "Non connesso."; return; }
        if (string.IsNullOrWhiteSpace(SplitSourceFolder)) { SplitStatusMessage = "Seleziona una cartella remota."; return; }
        if (string.IsNullOrWhiteSpace(SplitDestinationFolder)) SplitDestinationFolder = SplitSourceFolder;

        List<int>? segmentSizes;

        if (SplitUniformMode)
        {
            if (SplitUniformEpisodes <= 0) { SplitStatusMessage = "Numero episodi non valido."; return; }

            var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".m4v" };

            List<SftpRemoteEntry> entries;
            try
            {
                entries = await _sftpService.ListDirectoryAsync(SplitSourceFolder.TrimEnd('/'), CancellationToken.None);
            }
            catch (Exception ex)
            {
                SplitStatusMessage = $"Errore lettura remota: {ex.Message}";
                return;
            }

            int fileCount = entries.Count(e => !e.IsDirectory && videoExts.Contains(Path.GetExtension(e.Name)));
            if (fileCount == 0) { SplitStatusMessage = "Nessun file video."; return; }

            int segmentCount = (int)Math.Ceiling((double)fileCount / SplitUniformEpisodes);
            segmentSizes = new List<int>();
            for (int i = 0; i < segmentCount; i++) segmentSizes.Add(SplitUniformEpisodes);
        }
        else
        {
            if (SplitSegments.Count == 0) { SplitStatusMessage = "Aggiungi un segmento."; return; }
            segmentSizes = SplitSegments.Where(s => s.EpisodeCount > 0).Select(s => s.EpisodeCount).ToList();
            if (segmentSizes.Count == 0) { SplitStatusMessage = "Tutti segmenti a 0."; return; }
        }

        try
        {
            IsSplitBusy = true;
            SplitStatusMessage = "Analisi remota...";
            var mode = SplitNumberingContinue ? SplitNumberingMode.Continue : SplitNumberingMode.Reset;

            var ops = await _fileOrganizer.PreviewRemoteSplitAsync(
                SplitSourceFolder, SplitDestinationFolder, segmentSizes,
                SplitFilePattern, SplitFolderPattern, mode);

            SplitPreviewOperations.Clear();
            foreach (var op in ops) SplitPreviewOperations.Add(op);

            var conflicts = ops.Count(o => o.HasConflict);
            var folderCount = ops
                .Select(o => { var i = o.NewPath.LastIndexOf('/'); return i > 0 ? o.NewPath.Substring(0, i) : ""; })
                .Distinct().Count();

            SplitStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni in {folderCount} sottocartelle (remoto)."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { SplitStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsSplitBusy = false; }
    }

    private async Task ApplySplitRemoteInternalAsync()
    {
        if (SplitPreviewOperations.Count == 0) { SplitStatusMessage = "Nessuna operazione."; return; }
        var conflicts = SplitPreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { SplitStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsSplitBusy = true;
            SplitStatusMessage = "Applicazione remota...";
            var count = await _fileOrganizer.ApplyRemoteAsync(SplitPreviewOperations.ToList());
            SplitStatusMessage = $"Applicate {count} operazioni.";
            SplitPreviewOperations.Clear();
            await SftpRefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { SplitStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsSplitBusy = false; }
    }

    [RelayCommand]
    private void ClearSplitPreviewOperations() { SplitPreviewOperations.Clear(); SplitStatusMessage = "Proposte svuotate."; }

    // ============================================================
    //  SFTP - CONFIG
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
            SftpStatusMessage = "Disconnesso.";
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    private async Task SftpAutoRefreshLocalAsync()
    {
        if (SelectedLocalEntries.Count > 0 || SftpSelectedLocalEntry != null) return;
        if (string.IsNullOrWhiteSpace(SftpCurrentLocalPath)) return;
        if (!Directory.Exists(SftpCurrentLocalPath)) return;

        await SftpRefreshLocalInternalAsync(silent: true);
    }

    private async Task SftpAutoRefreshRemoteAsync()
    {
        if (!IsSftpConnected) return;
        if (IsSftpBusy) return;
        // RIMOSSO il check sulla selezione: SyncCollection preserva la selezione
        // grazie a Move(), quindi il refresh non deve essere bloccato.

        await SftpRefreshInternalAsync(silent: true);
    }

    [RelayCommand]
    private async Task SftpRefreshAsync() => await SftpRefreshInternalAsync(silent: false);

    private async Task SftpRefreshInternalAsync(bool silent)
    {
        if (!IsSftpConnected) return;

        if (IsRefreshingRemote && !silent)
            return; // già in corso

        try
        {
            IsRefreshingRemote = true;

            if (!silent)
            {
                IsSftpBusy = true;
                SftpStatusMessage = $"Lettura {SftpCurrentRemotePath}...";
            }

            var entries = await _sftpService.ListDirectoryAsync(SftpCurrentRemotePath);

            foreach (var e in entries)
            {
                if (!e.IsDirectory)
                    e.HasZeroByteIssue = e.Size == 0;
                else
                    e.HasZeroByteIssue = _zeroByteFolderCache.TryGetValue(e.FullPath, out var flag) && flag;
            }

            var sorted = SortRemoteEntries(entries, RemoteSortOption.Mode, RemoteSortAscending);

            SyncCollection(SftpRemoteEntries, sorted, e => e.FullPath, UpdateRemoteEntryFromSource);

            if (!silent)
                SftpStatusMessage = $"{entries.Count} elementi in {SftpCurrentRemotePath} ({DateTime.Now:HH:mm:ss})";
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
        if (IsRefreshingRemote) return;

        try
        {
            IsRefreshingRemote = true;
            SftpStatusMessage = "Riconnessione per ricaricare dati freschi...";

            // Salva la config corrente, riconnetti, ripristina il path
            var config = BuildCurrentSftpConfig();
            var savedPath = SftpCurrentRemotePath;

            await _sftpService.DisconnectAsync();
            await _sftpService.ConnectAsync(config);

            SftpCurrentRemotePath = savedPath;
        }
        catch (Exception ex)
        {
            SftpStatusMessage = $"Errore riconnessione: {ex.Message}";
            IsRefreshingRemote = false;
            return;
        }

        await SftpRefreshInternalAsync(silent: false);
    }

    [RelayCommand]
    private void ToggleLocalSortDirection() => LocalSortAscending = !LocalSortAscending;

    [RelayCommand]
    private void ToggleRemoteSortDirection() => RemoteSortAscending = !RemoteSortAscending;

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
    //  SFTP - BROWSER LOCALE
    // ============================================================

    [RelayCommand]
    private async Task SftpRefreshLocalAsync() => await SftpRefreshLocalInternalAsync(silent: false);

    private async Task SftpRefreshLocalInternalAsync(bool silent)
    {
        if (string.IsNullOrWhiteSpace(SftpCurrentLocalPath) || !Directory.Exists(SftpCurrentLocalPath))
            SftpCurrentLocalPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            var entries = await Task.Run(() =>
            {
                var list = new List<LocalFileEntry>();

                foreach (var dir in Directory.GetDirectories(SftpCurrentLocalPath))
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        list.Add(new LocalFileEntry
                        {
                            Name = info.Name,
                            FullPath = info.FullName,
                            IsDirectory = true,
                            Size = 0,
                            Modified = info.LastWriteTime
                        });
                    }
                    catch { }
                }

                foreach (var file in Directory.GetFiles(SftpCurrentLocalPath))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        list.Add(new LocalFileEntry
                        {
                            Name = info.Name,
                            FullPath = info.FullName,
                            IsDirectory = false,
                            Size = info.Length,
                            Modified = info.LastWriteTime
                        });
                    }
                    catch { }
                }

                return list;
            });

            var sorted = SortLocalEntries(entries, LocalSortOption.Mode, LocalSortAscending);
            SyncCollection(SftpLocalEntries, sorted, e => e.FullPath, UpdateLocalEntryFromSource);
        }
        catch (Exception ex)
        {
            if (!silent) SftpStatusMessage = $"Errore lettura locale: {ex.Message}";
        }
    }

    /// <summary>
    /// Comando on-demand: scansiona le cartelle visibili nella directory corrente
    /// e marca quelle che contengono almeno un file di 0 byte (un livello di profondità).
    /// Non parte in automatico. Non ricorsivo.
    /// </summary>
    [RelayCommand]
    private async Task CheckZeroByteFoldersAsync()
    {
        if (!IsSftpConnected) { SftpStatusMessage = "Non connesso."; return; }
        if (IsCheckingZeroByte) return;

        var folders = SftpRemoteEntries.Where(e => e.IsDirectory).ToList();
        if (folders.Count == 0)
        {
            SftpStatusMessage = "Nessuna cartella da controllare.";
            return;
        }

        IsCheckingZeroByte = true;
        int checked_ = 0;
        int flagged = 0;

        try
        {
            foreach (var folder in folders)
            {
                if (!IsSftpConnected) break;

                checked_++;
                ZeroByteCheckStatus = $"Controllo {checked_}/{folders.Count}: {folder.Name}";

                try
                {
                    var subEntries = await _sftpService.ListDirectoryAsync(folder.FullPath);
                    bool hasZero = subEntries.Any(e => !e.IsDirectory && e.Size == 0);

                    _zeroByteFolderCache[folder.FullPath] = hasZero;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        folder.HasZeroByteIssue = hasZero;
                    });

                    if (hasZero) flagged++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ZERO-BYTE-CHECK] fallito su {folder.FullPath}: {ex.Message}");
                }
            }

            SftpStatusMessage = flagged == 0
                ? $"Controllate {checked_} cartelle: nessuna contiene file vuoti."
                : $"Controllate {checked_} cartelle: {flagged} contengono file vuoti (evidenziate in rosso).";
        }
        finally
        {
            IsCheckingZeroByte = false;
            ZeroByteCheckStatus = string.Empty;
        }
    }

    /// <summary>
    /// Invalida la cache del check 0-byte per una cartella specifica.
    /// La prossima refresh considererà la cartella "non flagged" finché
    /// l'utente non preme di nuovo 🔍.
    /// </summary>
    private void InvalidateZeroByteCacheForFolder(string? folderPath)
    {
        if (string.IsNullOrEmpty(folderPath)) return;
        var normalized = folderPath.TrimEnd('/');
        _zeroByteFolderCache.Remove(normalized);
    }

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
    //  SFTP - CRUD LOCALE
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
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.Message;
            }
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
    //  SFTP - CRUD REMOTO
    // ============================================================

    public async Task SftpRemoteDeleteManyAsync(IEnumerable<SftpRemoteEntry> entries)
    {
        if (!IsSftpConnected) { SftpStatusMessage = "Non connesso."; return; }

        var list = entries.Where(e => e != null).ToList();
        if (list.Count == 0) return;

        int success = 0, failed = 0;
        string? firstError = null;

        foreach (var entry in list)
        {
            try
            {
                if (entry.IsDirectory)
                    await _sftpService.DeleteDirectoryAsync(entry.FullPath);
                else
                    await _sftpService.DeleteFileAsync(entry.FullPath);
                success++;
            }
            catch (Exception ex)
            {
                failed++;
                firstError ??= ex.Message;
            }
        }

        await SftpRefreshInternalAsync(silent: true);

        // Invalida la cache della cartella corrente: potrebbe aver perso
        // il suo ultimo file da 0 byte (o averlo ancora, lo scopriremo al 🔍).
        InvalidateZeroByteCacheForFolder(SftpCurrentRemotePath);

        if (failed == 0)
            SftpStatusMessage = success == 1 ? "Elemento eliminato dal server." : $"{success} elementi eliminati dal server.";
        else if (success == 0)
            SftpStatusMessage = $"Eliminazione fallita: {firstError}";
        else
            SftpStatusMessage = $"Eliminati {success}, falliti {failed}: {firstError}";
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
            InvalidateZeroByteCacheForFolder(SftpCurrentRemotePath);
            SftpStatusMessage = $"Cartella remota creata: {name}";
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
            InvalidateZeroByteCacheForFolder(SftpCurrentRemotePath);
            SftpStatusMessage = $"Rinominato in: {newName}";
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    // ============================================================
    //  SFTP - UPLOAD / DOWNLOAD
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
            // L'upload potrebbe sostituire un file da 0 byte: invalida la cache.
            InvalidateZeroByteCacheForFolder(SftpCurrentRemotePath);
        }
        catch (Exception ex) { SftpStatusMessage = $"Errore: {ex.Message}"; }
    }

    public async Task EnqueueUploadsFromPathsAsync(IEnumerable<string> localPaths, string? remoteDir = null)
    {
        if (!IsSftpConnected) { SftpStatusMessage = "Non connesso."; return; }

        var targetDir = string.IsNullOrWhiteSpace(remoteDir) ? SftpCurrentRemotePath : remoteDir;
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            SftpStatusMessage = "Nessuna cartella remota di destinazione.";
            return;
        }

        try
        {
            _sftpUploadQueue.Configure(BuildCurrentSftpConfig());
            _sftpUploadQueue.MaxConcurrency = SftpMaxUploadConcurrency;

            int totalQueued = 0;

            foreach (var path in localPaths)
            {
                if (Directory.Exists(path))
                {
                    totalQueued += await _sftpUploadQueue.EnqueueFolderAsync(path, targetDir);
                }
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

            if (totalQueued == 0)
            {
                SftpStatusMessage = "Nessun file valido da caricare.";
                return;
            }

            SftpStatusMessage = $"{totalQueued} file accodati per upload (drag & drop).";
            _sftpUploadQueue.StartAll();
            // Invalida la cache della destinazione (potrebbe aver ricevuto file non-zero).
            InvalidateZeroByteCacheForFolder(targetDir);
        }
        catch (Exception ex)
        {
            SftpStatusMessage = $"Errore accodamento upload: {ex.Message}";
        }
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
            {
                await DownloadRemoteFolderRecursiveAsync(entry.FullPath, localFolder);
            }
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
    //  SFTP - UPLOAD QUEUE
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
            InvalidateZeroByteCacheForFolder(SftpUploadRemoteFolder);
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
    //  SFTP - DOWNLOAD QUEUE
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

    // ============================================================
    //  SORT / SYNC HELPER
    // ============================================================

    private static int NaturalCompare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i];
            char cb = b[j];

            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                // Estrai i due blocchi numerici
                int startA = i, startB = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;

                var numA = a.Substring(startA, i - startA);
                var numB = b.Substring(startB, j - startB);

                // Rimuovi zeri iniziali per confrontare per valore
                var trimmedA = numA.TrimStart('0');
                var trimmedB = numB.TrimStart('0');
                if (trimmedA.Length == 0) trimmedA = "0";
                if (trimmedB.Length == 0) trimmedB = "0";

                // Prima confronta per numero di cifre (più corto = più piccolo)
                if (trimmedA.Length != trimmedB.Length)
                    return trimmedA.Length.CompareTo(trimmedB.Length);

                // Poi confronta cifra per cifra
                int cmp = string.CompareOrdinal(trimmedA, trimmedB);
                if (cmp != 0) return cmp;
            }
            else
            {
                // Confronto case-insensitive carattere per carattere
                int cmp = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static List<LocalFileEntry> SortLocalEntries(
    IEnumerable<LocalFileEntry> source, SftpSortMode mode, bool ascending)
    {
        var dirs = source.Where(e => e.IsDirectory).ToList();
        var files = source.Where(e => !e.IsDirectory).ToList();

        Comparison<LocalFileEntry> comp = mode switch
        {
            SftpSortMode.Size => (a, b) =>
            {
                int c = a.Size.CompareTo(b.Size);
                return c != 0 ? c : NaturalCompare(a.Name, b.Name);
            },
            SftpSortMode.Modified => (a, b) =>
            {
                int c = a.Modified.CompareTo(b.Modified);
                return c != 0 ? c : NaturalCompare(a.Name, b.Name);
            },
            SftpSortMode.Type => (a, b) =>
            {
                int c = NaturalCompare(Path.GetExtension(a.Name), Path.GetExtension(b.Name));
                return c != 0 ? c : NaturalCompare(a.Name, b.Name);
            },
            _ => (a, b) => NaturalCompare(a.Name, b.Name)
        };

        dirs.Sort(comp);
        files.Sort(comp);

        if (!ascending)
        {
            dirs.Reverse();
            files.Reverse();
        }

        return dirs.Concat(files).ToList();
    }

    private static List<SftpRemoteEntry> SortRemoteEntries(
    IEnumerable<SftpRemoteEntry> source, SftpSortMode mode, bool ascending)
    {
        var dirs = source.Where(e => e.IsDirectory).ToList();
        var files = source.Where(e => !e.IsDirectory).ToList();

        Comparison<SftpRemoteEntry> comp = mode switch
        {
            SftpSortMode.Size => (a, b) =>
            {
                int c = a.Size.CompareTo(b.Size);
                return c != 0 ? c : NaturalCompare(a.Name, b.Name);
            },
            SftpSortMode.Modified => (a, b) =>
            {
                int c = a.Modified.CompareTo(b.Modified);
                return c != 0 ? c : NaturalCompare(a.Name, b.Name);
            },
            SftpSortMode.Type => (a, b) =>
            {
                int c = NaturalCompare(Path.GetExtension(a.Name), Path.GetExtension(b.Name));
                return c != 0 ? c : NaturalCompare(a.Name, b.Name);
            },
            _ => (a, b) => NaturalCompare(a.Name, b.Name)
        };

        dirs.Sort(comp);
        files.Sort(comp);

        if (!ascending)
        {
            dirs.Reverse();
            files.Reverse();
        }

        return dirs.Concat(files).ToList();
    }

    private static void SyncCollection<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> source,
        Func<T, string> keySelector,
        Action<T, T>? updateExisting = null)
    {
        var sourceKeys = new HashSet<string>(source.Select(keySelector));

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!sourceKeys.Contains(keySelector(target[i])))
                target.RemoveAt(i);
        }

        for (int i = 0; i < source.Count; i++)
        {
            var key = keySelector(source[i]);

            int existingIndex = -1;
            for (int j = i; j < target.Count; j++)
            {
                if (keySelector(target[j]) == key) { existingIndex = j; break; }
            }

            if (existingIndex < 0)
            {
                target.Insert(i, source[i]);
            }
            else
            {
                if (updateExisting != null)
                    updateExisting(target[existingIndex], source[i]);

                if (existingIndex != i)
                    target.Move(existingIndex, i);
            }
        }
    }

    /// <summary>
    /// Copia i campi modificabili di un SftpRemoteEntry dal listing fresco all'item esistente.
    /// Preserva l'identità dell'oggetto (e quindi il binding XAML e la posizione di scroll).
    /// </summary>
    private static void UpdateRemoteEntryFromSource(SftpRemoteEntry target, SftpRemoteEntry source)
    {
        target.Size = source.Size;
        target.Modified = source.Modified;
        target.IsDirectory = source.IsDirectory;
        target.HasZeroByteIssue = source.HasZeroByteIssue;
    }

    private static void UpdateLocalEntryFromSource(LocalFileEntry target, LocalFileEntry source)
    {
        target.Size = source.Size;
        target.Modified = source.Modified;
        target.IsDirectory = source.IsDirectory;
    }

    // --- Proxy ---

    [RelayCommand]
    private async Task ProxyLoadAllAsync()
    {
        try
        {
            await _proxyService.LoadAllAsync();
            Proxies.Clear();
            foreach (var p in _proxyService.All)
            {
                p.IsSelected = false;
                Proxies.Add(p);
            }

            ProxyStatusMessage = Proxies.Count == 0
                ? "Nessun proxy configurato."
                : $"{Proxies.Count} proxy disponibili.";
        }
        catch (Exception ex) { ProxyStatusMessage = $"Errore: {ex.Message}"; }
    }

    [RelayCommand]
    private void ProxyNew()
    {
        SelectedProxy = null;
        var p = new ProxyConfig { Name = "Nuovo proxy", IsSelected = false };
        Proxies.Add(p);
        SelectedProxy = p;
        ProxyStatusMessage = "Nuovo proxy aggiunto. Compila i campi e premi 💾.";
    }

    [RelayCommand]
    private async Task ProxySaveAsync()
    {
        if (SelectedProxy == null) return;

        try
        {
            IsProxyBusy = true;

            // Assicura che solo UNO sia abilitato
            if (SelectedProxy.IsEnabled)
            {
                foreach (var p in Proxies.Where(p => p != SelectedProxy))
                    p.IsEnabled = false;
            }

            await _proxyService.SaveAsync(SelectedProxy);

            ProxyStatusMessage = $"Proxy '{SelectedProxy.Name}' salvato.";
            ApplyActiveProxy();
            _ = Task.Run(async () =>
            {
                try { await _animeWorldService.NotifyProxyChangedAsync(); }
                catch { }
            });
        }
        catch (Exception ex) { ProxyStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsProxyBusy = false; }
    }

    [RelayCommand]
    private async Task ProxyDeleteFailedAsync()
    {
        var failed = Proxies.Where(p => p.IsFailed || p.TestResult.StartsWith("❌")).ToList();
        if (failed.Count == 0)
        {
            ProxyStatusMessage = "Nessun proxy fallito da eliminare.";
            return;
        }

        IsProxyBusy = true;
        try
        {
            foreach (var p in failed)
            {
                try { await _proxyService.DeleteAsync(p); } catch { }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var p in failed) Proxies.Remove(p);
                if (SelectedProxy != null && failed.Contains(SelectedProxy))
                    SelectedProxy = Proxies.FirstOrDefault();
            });

            ProxyStatusMessage = $"{failed.Count} proxy falliti eliminati.";
            ApplyActiveProxy();
        }
        finally { IsProxyBusy = false; }
    }

    [RelayCommand]
    private async Task ProxyDeleteAsync()
    {
        if (SelectedProxy == null) return;
        try
        {
            await _proxyService.DeleteAsync(SelectedProxy);
            Proxies.Remove(SelectedProxy);
            SelectedProxy = Proxies.FirstOrDefault();
            ProxyStatusMessage = "Proxy eliminato.";
            ApplyActiveProxy();
        }
        catch (Exception ex) { ProxyStatusMessage = $"Errore: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ProxyDeleteSelectedAsync()
    {
        var targets = Proxies.Where(p => p.IsSelected).ToList();
        if (targets.Count == 0) { ProxyStatusMessage = "Nessun proxy selezionato."; return; }

        IsProxyBusy = true;
        try
        {
            // Eliminazione in parallelo (max 8)
            var gate = new SemaphoreSlim(8);
            int ok = 0, fail = 0;

            var tasks = targets.Select(async p =>
            {
                await gate.WaitAsync();
                try
                {
                    await _proxyService.DeleteAsync(p);
                    Interlocked.Increment(ref ok);
                }
                catch { Interlocked.Increment(ref fail); }
                finally { gate.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);

            // Aggiorna la UI collection sul thread UI
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var p in targets) Proxies.Remove(p);
                if (SelectedProxy != null && targets.Contains(SelectedProxy))
                    SelectedProxy = Proxies.FirstOrDefault();
            });

            ProxyStatusMessage = fail == 0
                ? $"{ok} proxy eliminati."
                : $"{ok} eliminati, {fail} falliti.";
            ApplyActiveProxy();
        }
        finally { IsProxyBusy = false; }
    }

    [RelayCommand]
    private async Task ProxyDeleteAllAsync()
    {
        if (Proxies.Count == 0) { ProxyStatusMessage = "Lista già vuota."; return; }

        var snapshot = Proxies.ToList();
        IsProxyBusy = true;
        try
        {
            var gate = new SemaphoreSlim(8);
            await Task.WhenAll(snapshot.Select(async p =>
            {
                await gate.WaitAsync();
                try { await _proxyService.DeleteAsync(p); } catch { }
                finally { gate.Release(); }
            }));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Proxies.Clear();
                SelectedProxy = null;
            });

            ProxyStatusMessage = $"Eliminati {snapshot.Count} proxy.";
            ApplyActiveProxy();
        }
        finally { IsProxyBusy = false; }
    }

    [RelayCommand]
    private void ProxySelectAll()
    {
        foreach (var p in Proxies) p.IsSelected = true;
        ProxyStatusMessage = $"Selezionati {Proxies.Count} proxy.";
    }

    [RelayCommand]
    private void ProxyDeselectAll()
    {
        foreach (var p in Proxies) p.IsSelected = false;
        ProxyStatusMessage = "Selezione azzerata.";
    }

    [RelayCommand]
    private async Task ProxyTestAsync()
    {
        if (SelectedProxy == null) { ProxyStatusMessage = "Nessun proxy selezionato."; return; }
        await RunProxyTestAsync(SelectedProxy);
    }

    [RelayCommand]
    private async Task ProxyTestSelectedAsync()
    {
        var targets = Proxies.Where(p => p.IsSelected).ToList();
        if (targets.Count == 0) { ProxyStatusMessage = "Nessun proxy selezionato."; return; }

        await RunProxyTestBatchAsync(targets, "selezionati");
    }

    [RelayCommand]
    private async Task ProxyTestAllAsync()
    {
        if (Proxies.Count == 0) { ProxyStatusMessage = "Nessun proxy da testare."; return; }
        await RunProxyTestBatchAsync(Proxies.ToList(), "totali");
    }

    /// <summary>
    /// Esegue il test su un singolo proxy e aggiorna il suo stato.
    /// </summary>
    private async Task RunProxyTestAsync(ProxyConfig proxy)
    {
        try
        {
            proxy.IsTesting = true;
            proxy.TestResult = "Test in corso...";

            var (ok, msg, _) = await _proxyService.TestAsync(proxy);

            proxy.IsTesting = false;
            proxy.TestResult = ok ? $"✅ {msg}" : $"❌ {msg}";
            proxy.LastTested = DateTime.Now;

            if (ReferenceEquals(proxy, SelectedProxy))
                ProxyStatusMessage = proxy.TestResult;
        }
        catch (Exception ex)
        {
            proxy.IsTesting = false;
            proxy.TestResult = $"❌ {ex.Message}";
        }
    }

    /// <summary>
    /// Esegue test in parallelo su una lista di proxy (max 5 concorrenti).
    /// </summary>
    private async Task RunProxyTestBatchAsync(List<ProxyConfig> targets, string label)
    {
        if (_proxyTestCts != null)
        {
            ProxyStatusMessage = "Test già in corso. Fermalo prima di avviarne un altro.";
            return;
        }

        _proxyTestCts = new CancellationTokenSource();
        var token = _proxyTestCts.Token;

        IsProxyBusy = true;
        try
        {
            int total = targets.Count;
            int completed = 0;
            int okCount = 0;
            int failCount = 0;
            var gate = new SemaphoreSlim(Math.Max(1, ProxyTestConcurrency));

            var tasks = targets.Select(async proxy =>
            {
                await gate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();

                    proxy.IsTesting = true;
                    proxy.TestResult = "Test in corso...";

                    var (ok, msg, _) = await _proxyService.TestAsync(proxy, token);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        proxy.IsTesting = false;
                        proxy.TestResult = ok ? $"✅ {msg}" : $"❌ {msg}";
                        proxy.LastTested = DateTime.Now;

                        completed++;
                        if (ok) okCount++; else failCount++;

                        ProxyStatusMessage = $"Test {label}: {completed}/{total} — {okCount} ok, {failCount} ko";
                    });

                    // Persisti su DB
                    try { await _proxyService.SaveAsync(proxy); } catch { }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }

            if (token.IsCancellationRequested)
                ProxyStatusMessage = $"Test {label} interrotto: {completed}/{total} completati.";
            else
                ProxyStatusMessage = $"Test {label} completato: {okCount}/{total} funzionanti.";
        }
        finally
        {
            _proxyTestCts?.Dispose();
            _proxyTestCts = null;
            IsProxyBusy = false;
        }
    }

    [RelayCommand]
    private void ProxyStopTest()
    {
        if (_proxyTestCts == null)
        {
            ProxyStatusMessage = "Nessun test in corso.";
            return;
        }

        _proxyTestCts.Cancel();
        ProxyStatusMessage = "Interruzione test...";
    }

    [RelayCommand]
    private async Task ProxyToggleAsync()
    {
        if (SelectedProxy == null) return;

        SelectedProxy.IsEnabled = !SelectedProxy.IsEnabled;

        if (SelectedProxy.IsEnabled)
        {
            foreach (var p in Proxies.Where(p => p != SelectedProxy))
                p.IsEnabled = false;
        }

        foreach (var p in Proxies)
            await _proxyService.SaveAsync(p);

        ApplyActiveProxy();
        _ = Task.Run(async () =>
        {
            try { await _animeWorldService.NotifyProxyChangedAsync(); }
            catch { }
        });

        ProxyStatusMessage = SelectedProxy.IsEnabled
            ? $"Proxy '{SelectedProxy.Name}' ATTIVO."
            : "Proxy disattivato.";
    }

    public void ProxyImportFromString(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            ProxyStatusMessage = "File vuoto.";
            return;
        }

        var (parsed, errors) = DownloadManager.Services.Proxy.ProxyFileParser.Parse(content);

        if (parsed.Count == 0 && errors.Count == 0)
        {
            ProxyStatusMessage = "Nessun proxy trovato nel file.";
            return;
        }

        int added = 0;
        foreach (var p in parsed)
        {
            if (string.IsNullOrEmpty(p.Name)) p.Name = p.DisplayText;
            p.IsSelected = false;
            Proxies.Add(p);
            added++;
        }

        var msg = $"Importati {added} proxy.";
        if (errors.Count > 0)
            msg += $" {errors.Count} righe ignorate.";

        ProxyStatusMessage = msg;

        _ = Task.Run(async () =>
        {
            foreach (var p in parsed)
            {
                try { await _proxyService.SaveAsync(p); } catch { }
            }
        });
    }

    private void ApplyActiveProxy()
    {
        var active = Proxies.FirstOrDefault(p => p.IsEnabled);

        if (active != null)
            ProxyStatusMessage = $"Proxy attivo: {active.Name} ({active.DisplayText})";
        else
            ProxyStatusMessage = "Nessun proxy attivo. Connessione diretta.";

        OnPropertyChanged(nameof(ActiveProxyBadgeText));
        OnPropertyChanged(nameof(IsProxyActive));
    }
}