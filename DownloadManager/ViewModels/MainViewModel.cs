using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.IO;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Anime.AnimeWorld;
using DownloadManager.Services.Anime.AnimeSaturn;
using DownloadManager.Services.Streaming.StreamingCommunity;
using DownloadManager.Services.Anime;
using DownloadManager.Services.Download;
using DownloadManager.Services.FileOrganizer;
using DownloadManager.Services.Logging;
using DownloadManager.Services.Sftp;
using DownloadManager.Services.Domains;
using Microsoft.Extensions.Logging;
using Avalonia.Controls;

using DownloadManager.Services.Proxy;

namespace DownloadManager.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IAnimeWorldService _animeWorldService;
    private readonly IAnimeSaturnService _animeSaturnService;
    private readonly AnimeProviderRegistry _animeProviderRegistry;
    private readonly IStreamingCommunityService _streamingCommunityService;
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
    private readonly ILogger<SftpTabViewModel> _sftpLogger;

    public VideoboxTabViewModel Videobox { get; }

    /// <summary>Stato condiviso tra i tab.</summary>
    public SharedState Shared { get; }

    [ObservableProperty] private string _themeVariant = "Default";

    // --- Sub-ViewModel ---
    public DownloadTabViewModel AnimeDownload { get; }
    public StreamingTabViewModel Streaming { get; }
    public AccountTabViewModel Account { get; }
    public ProxyTabViewModel Proxy { get; }
    public OrganizerTabViewModel Organizer { get; }
    public SftpTabViewModel Sftp { get; }
    /// <summary>Barra di stato in basso e notifiche di Windows.</summary>
    public ActivityViewModel Activity { get; }
    public DomainsTabViewModel Domains { get; }

    // --- Log globale ---
    public ObservableCollection<LogEntry> Logs => _logService.Logs;

    /// <summary>Versione mostrata nel titolo, letta dall'assembly (non più scritta a mano).</summary>
    public string AppVersionText { get; } =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v
            ? $"v{v.Major}.{v.Minor}.{v.Build}"
            : "";

    // --- Badge proxy (top bar) ---
    public string ActiveProxyBadgeText
    {
        get
        {
            var s = _settings.Current;
            var providers = new List<string>();
            bool Active(string key) => Proxy.Proxies.Any(p => DownloadManager.Services.Proxy.ProxyService.IsEnabledFor(p, key));
            if (s.UseProxyForAnimeWorld && Active(ProviderKeys.AnimeWorld)) providers.Add("AW");
            if (s.UseProxyForAnimeSaturn && Active(ProviderKeys.AnimeSaturn)) providers.Add("AS");
            if (s.UseProxyForStreamingCommunity && Active(ProviderKeys.StreamingCommunity)) providers.Add("SC");
            if (s.UseProxyForDownloads && Active(ProviderKeys.Downloads)) providers.Add("DL");
            if (s.UseProxyForCb01 && Active(ProviderKeys.Cb01)) providers.Add("CB");

            if (providers.Count == 0) return "🌐 Off";
            return $"🌐 {string.Join("+", providers)}";
        }
    }

    public bool IsProxyActive
    {
        get
        {
            var s = _settings.Current;
            bool Active(string key) => Proxy.Proxies.Any(p => DownloadManager.Services.Proxy.ProxyService.IsEnabledFor(p, key));
            return (s.UseProxyForAnimeWorld && Active(ProviderKeys.AnimeWorld)) ||
                   (s.UseProxyForAnimeSaturn && Active(ProviderKeys.AnimeSaturn)) ||
                   (s.UseProxyForStreamingCommunity && Active(ProviderKeys.StreamingCommunity)) ||
                   (s.UseProxyForDownloads && Active(ProviderKeys.Downloads)) ||
                   (s.UseProxyForCb01 && Active(ProviderKeys.Cb01));
        }
    }

    // --- Badge domini (top bar) ---

    public bool HasDomainIssues => Shared.HasDomainIssues;

    public MainWindowViewModel(
        IAnimeWorldService animeWorldService,
        IAnimeSaturnService animeSaturnService,
        AnimeProviderRegistry animeProviderRegistry,
        DownloadQueueService queueService,
        FileNameBuilder fileNameBuilder,
        SettingsService settings,
        IFileOrganizerService fileOrganizer,
        SftpConfigRepository sftpRepository,
        ISftpService sftpService,
        SftpUploadQueueService sftpUploadQueue,
        SftpDownloadQueueService sftpDownloadQueue,
        DownloadManager.Services.Proxy.ProxyService proxyService,
        LogService logService,
        DownloadManager.Services.Domains.DomainResolver domainsResolver,
        DownloadManager.Services.Domains.DomainTester domainTester,
        ILogger<DomainsTabViewModel> domainsLogger,
        ILogger<SftpTabViewModel> sftpLogger,
        ILogger<DownloadTabViewModel> downloadLogger,
        ILogger<StreamingTabViewModel> streamingLogger,
        IStreamingCommunityService streamingCommunityService,
        DownloadManager.Services.Streaming.Cb01.ICb01Service cb01Service,
        AccountTabViewModel accountVm,
        VideoboxTabViewModel videobox,
        SftpRelayQueueService sftpRelayQueue,
        ILoggerFactory loggerFactory)
    {
        Shared = new SharedState();
        _animeWorldService = animeWorldService;
        _animeSaturnService = animeSaturnService;
        _animeProviderRegistry = animeProviderRegistry;
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
        _sftpLogger = sftpLogger;
        _streamingCommunityService = streamingCommunityService;

        // ============================================================
        //  SUB-VIEWMODEL
        // ============================================================

        AnimeDownload = new DownloadTabViewModel(
            _animeProviderRegistry,
            _queueService,
            _fileNameBuilder,
            _settings,
            downloadLogger,
            Shared);

        Streaming = new StreamingTabViewModel(
            _streamingCommunityService,
            cb01Service,
            _queueService,
            _fileNameBuilder,
            _settings,
            streamingLogger,
            Shared);

        Activity = new ActivityViewModel(_queueService, _settings, _sftpUploadQueue, _sftpDownloadQueue, sftpRelayQueue);

        Account = accountVm;
        Videobox = videobox;
        Videobox.Initialize(Shared); // Passa lo stato condiviso che hai creato tu

        // ============================================================
        //  TOP BAR — BADGE PROXY
        // ============================================================

        Shared.ActiveProxyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ActiveProxyBadgeText));
            OnPropertyChanged(nameof(IsProxyActive));
        };

        Proxy = new ProxyTabViewModel(
            _proxyService,
            _animeWorldService,
            _animeSaturnService,
            _streamingCommunityService,
            domainsResolver,
             _settings,
            Shared);

        Organizer = new OrganizerTabViewModel(
            _fileOrganizer,
            _sftpService,
            _settings,
            Shared);

        var zeroByteChecker = new ZeroByteCheckerService(
            _sftpService,
            () => Shared.IsSftpConnected);

        Sftp = new SftpTabViewModel(
            _sftpService,
            _sftpRepository,
            _sftpUploadQueue,
            _sftpDownloadQueue,
            zeroByteChecker,
            _sftpLogger,
            Shared,
            sftpRelayQueue,
            loggerFactory);

            Domains = new DomainsTabViewModel(
            domainsResolver,
            domainTester,
            _animeWorldService,
            _animeSaturnService,
            _streamingCommunityService,
            domainsLogger,
            Shared,
            _settings);

        Shared.RequestSftpRefresh = () => _ = Sftp.SftpRefreshCommand.ExecuteAsync(null);

        // ============================================================
        //  TOP BAR — BADGE DOMINI
        // ============================================================

        Shared.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SharedState.HasDomainIssues))
                OnPropertyChanged(nameof(HasDomainIssues));
        };
        

        // ============================================================
        //  IMPOSTAZIONI INIZIALI
        // ============================================================

        // ============================================================
        //  LOG — i messaggi di stato di ogni tab finiscono nel log globale
        // ============================================================

        TrackStatus(AnimeDownload, "Anime", nameof(DownloadTabViewModel.StatusMessage));
        TrackStatus(Streaming, "Streaming", nameof(StreamingTabViewModel.StatusMessage));
        TrackStatus(Account, "Account", nameof(AccountTabViewModel.StatusMessage));
        TrackStatus(Organizer, "Organizza",
            nameof(OrganizerTabViewModel.OrganizeStatusMessage),
            nameof(OrganizerTabViewModel.MergeStatusMessage),
            nameof(OrganizerTabViewModel.SplitStatusMessage));
        TrackStatus(Sftp, "SFTP", nameof(SftpTabViewModel.SftpStatusMessage));
        TrackStatus(Videobox, "Videobox", nameof(VideoboxTabViewModel.StatusMessage));
        TrackStatus(Proxy, "Proxy", nameof(ProxyTabViewModel.ProxyStatusMessage));
        TrackStatus(Domains, "Domini", nameof(DomainsTabViewModel.StatusMessage));

        foreach (var entry in Logs) AddToFilteredLogs(entry);
        Logs.CollectionChanged += OnLogsChanged;

        Shared.BaseDownloadFolder = _settings.Current.BaseDownloadFolder;
        Shared.CreateSeriesFolder = _settings.Current.CreateSeriesFolder;
        ThemeVariant = _settings.Current.ThemeVariant;
    }

    // ============================================================
    //  PROPERTY CHANGED HANDLERS
    // ============================================================

    partial void OnThemeVariantChanged(string value)
    {
        App.ApplyTheme(value);

        if (_settings != null)
        {
            _settings.Current.ThemeVariant = value;
            _settings.Save();
        }
    }

    // ============================================================
    //  LOG
    // ============================================================

    // ---- Filtri del log ----

    /// <summary>Log visibili nel pannello, secondo i filtri di livello e sezione.</summary>
    public ObservableCollection<LogEntry> FilteredLogs { get; } = new();

    private const string AllLogCategories = "Tutte le sezioni";
    public ObservableCollection<string> LogCategories { get; } = new() { AllLogCategories };

    public string[] LogLevelFilters { get; } = { "Tutti i livelli", "Avvisi ed errori", "Solo errori" };

    [ObservableProperty] private string _selectedLogCategory = AllLogCategories;
    [ObservableProperty] private int _selectedLogLevelFilter;

    partial void OnSelectedLogCategoryChanged(string value) => RebuildFilteredLogs();
    partial void OnSelectedLogLevelFilterChanged(int value) => RebuildFilteredLogs();

    private bool MatchesLogFilter(LogEntry e)
    {
        if (SelectedLogLevelFilter == 1 && e.Level == Models.LogLevel.Info) return false;
        if (SelectedLogLevelFilter == 2 && e.Level != Models.LogLevel.Error) return false;
        return SelectedLogCategory is null or AllLogCategories || e.Category == SelectedLogCategory;
    }

    private void AddToFilteredLogs(LogEntry e)
    {
        if (!LogCategories.Contains(e.Category)) LogCategories.Add(e.Category);
        if (MatchesLogFilter(e)) FilteredLogs.Add(e);
    }

    private void RebuildFilteredLogs()
    {
        FilteredLogs.Clear();
        foreach (var e in Logs)
            if (MatchesLogFilter(e)) FilteredLogs.Add(e);
    }

    private void OnLogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems != null:
                foreach (LogEntry entry in e.NewItems) AddToFilteredLogs(entry);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                // Il LogService toglie le voci più vecchie: sono in testa, la ricerca è immediata.
                foreach (LogEntry entry in e.OldItems) FilteredLogs.Remove(entry);
                break;
            default:
                RebuildFilteredLogs();
                break;
        }
    }

    // ---- Messaggi di stato delle tab ----

    private static readonly Regex DigitsRegex = new(@"\d+", RegexOptions.Compiled);
    private readonly Dictionary<string, (string Pattern, DateTime At)> _lastStatusLog = new();

    private void TrackStatus(INotifyPropertyChanged vm, string category, params string[] properties)
    {
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == null || Array.IndexOf(properties, e.PropertyName) < 0) return;
            LogStatus(category, vm.GetType().GetProperty(e.PropertyName)?.GetValue(vm) as string);
        };
    }

    private void LogStatus(string category, string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        // I contatori di avanzamento ("Test 3/50...") cambiano di continuo: se il messaggio
        // differisce dal precedente solo nei numeri ed è recente, non lo ripeto.
        var pattern = DigitsRegex.Replace(message, "#");
        var now = DateTime.Now;
        lock (_lastStatusLog)
        {
            if (_lastStatusLog.TryGetValue(category, out var last) &&
                last.Pattern == pattern && now - last.At < TimeSpan.FromSeconds(10))
                return;
            _lastStatusLog[category] = (pattern, now);
        }

        _logService.Add(new LogEntry { Category = category, Level = GuessLevel(message), Message = message });
    }

    private static Models.LogLevel GuessLevel(string message)
    {
        var m = message.ToLowerInvariant();
        if (m.Contains("errore") || m.Contains("\u274c") || m.Contains("fallit") || m.Contains("impossibile") || m.Contains("non riuscit"))
            return Models.LogLevel.Error;
        if (m.Contains("\u26a0") || m.Contains("attenzione") || m.Contains("annullat") || m.Contains("interrott") || m.Contains("scadut") || m.Contains("disconness"))
            return Models.LogLevel.Warning;
        return Models.LogLevel.Info;
    }

    private static string FormatLogLine(LogEntry l) => $"{l.TimestampText} {l.LevelText} [{l.Category}] {l.Message}";

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        try
        {
            _logService.Flush();   // anche le righe appena arrivate
            var text = string.Join(Environment.NewLine, FilteredLogs.Select(FormatLogLine));

            await DownloadManager.Helpers.ClipboardHelper.SetTextAsync(text);

            LogStatus("Generale", $"Copiate {FilteredLogs.Count} righe di log negli appunti.");
        }
        catch (Exception ex)
        {
            LogStatus("Generale", $"Errore copia log: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ExportLogsAsync()
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is not Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is null)
                return;

            var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
            if (topLevel is null) return;

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(
                new Avalonia.Platform.Storage.FilePickerSaveOptions
                {
                    Title = "Salva log",
                    SuggestedFileName = $"ddl-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                    DefaultExtension = "txt",
                    FileTypeChoices = new[]
                    {
                        new Avalonia.Platform.Storage.FilePickerFileType("File di testo")
                        {
                            Patterns = new[] { "*.txt" }
                        }
                    }
                });

            if (file is null) return;

            var path = file.Path.LocalPath;
            _logService.Flush();
            await File.WriteAllLinesAsync(path, Logs.Select(FormatLogLine).ToList());
            LogStatus("Generale", $"Log esportato in {path}");
        }
        catch (Exception ex)
        {
            LogStatus("Generale", $"Errore esportazione log: {ex.Message}");
        }
    }

    [RelayCommand]
    private void ClearLogs() => _logService.Clear();

    // ============================================================
    //  Aggiornamenti (Velopack)
    // ============================================================

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        try
        {
            var mgr = new Velopack.UpdateManager(
                new Velopack.Sources.GithubSource(
                    "https://github.com/Sert-X/ddl-download-manager",
                    accessToken: null,
                    prerelease: false));

            var update = await mgr.CheckForUpdatesAsync();

            // CASO 1: nessun aggiornamento → InfoDialog
            if (update == null)
            {
                System.Diagnostics.Debug.WriteLine("[UPDATE] Nessun aggiornamento.");

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    var win = GetOwnerWindow();
                    if (win == null) return;

                    await Views.InfoDialog.ShowAsync(
                        win,
                        "Stai già usando la versione più recente di DDL Download Manager.",
                        "Nessun aggiornamento");
                });
                return;
            }

            // CASO 2: aggiornamento disponibile → UpdateDialog
            var newVersion = update.TargetFullRelease.Version.ToString();

            System.Diagnostics.Debug.WriteLine($"[UPDATE] Trovata versione {newVersion}");

            var changelog = await DownloadManager.Services.Update.GitHubReleaseFetcher.FetchChangelogAsync(
                "Sert-X", "ddl-download-manager", "v" + newVersion);

            var choice = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var win = GetOwnerWindow();
                if (win == null) return UpdateChoice.Cancel;

                return await Views.UpdateDialog.ShowAsync(
                    win,
                    newVersion,
                    Views.UpdateDownloadInfo.From(mgr, update),
                    changelog,
                    async (progress, ct) =>
                    {
                        await mgr.DownloadUpdatesAsync(update, p => progress(p), ct);
                    });
            });

            // Come il controllo automatico all'avvio: silent = niente finestra di sistema di Velopack
            // ("Installazione dell'aggiornamento in corso"). L'avanzamento lo mostra il nostro dialog e,
            // al riavvio, la schermata delle novità. (Questo pulsante installava senza "silent".)
            switch (choice)
            {
                case UpdateChoice.InstallNow:
                    mgr.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: true);
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                            desktop.Shutdown();
                    });
                    break;

                case UpdateChoice.InstallLater:
                    mgr.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UPDATE] ECCEZIONE: {ex}");

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var win = GetOwnerWindow();
                if (win == null) return;

                await Views.InfoDialog.ShowAsync(
                    win,
                    $"Errore durante il controllo aggiornamenti:\n\n{ex.Message}",
                    "Errore aggiornamento");
            });
        }
    }

    /// <summary>
    /// Ritorna la finestra principale per far aprire i dialog modali al centro di essa.
    /// </summary>
    private Window? GetOwnerWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }
}