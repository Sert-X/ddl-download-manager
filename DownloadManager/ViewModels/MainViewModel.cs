using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Anime.AnimeWorld;
using DownloadManager.Services.Anime.AnimeSaturn;
using DownloadManager.Services.Anime;
using DownloadManager.Services.Download;
using DownloadManager.Services.FileOrganizer;
using DownloadManager.Services.Logging;
using DownloadManager.Services.Sftp;
using DownloadManager.Services.Domains;
using Microsoft.Extensions.Logging;
using Avalonia.Controls;

namespace DownloadManager.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IAnimeWorldService _animeWorldService;
    private readonly IAnimeSaturnService _animeSaturnService;
    private readonly AnimeProviderRegistry _animeProviderRegistry;
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

    /// <summary>Stato condiviso tra i tab.</summary>
    public SharedState Shared { get; }

    [ObservableProperty] private string _themeVariant = "Default";

    // --- Sub-ViewModel ---
    public DownloadTabViewModel Download { get; }
    public ProxyTabViewModel Proxy { get; }
    public OrganizerTabViewModel Organizer { get; }
    public SftpTabViewModel Sftp { get; }
    public DomainsTabViewModel Domains { get; }

    // --- Log globale ---
    public ObservableCollection<LogEntry> Logs => _logService.Logs;

    // --- Badge proxy (top bar) ---
    public string ActiveProxyBadgeText => Proxy.Proxies.Any(p => p.IsEnabled)
        ? $"🌐 {Proxy.Proxies.First(p => p.IsEnabled).Name}"
        : "🌐 off";

    public bool IsProxyActive => Proxy.Proxies.Any(p => p.IsEnabled);

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
        ILogger<DownloadTabViewModel> downloadLogger)
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

        // ============================================================
        //  SUB-VIEWMODEL
        // ============================================================

        Download = new DownloadTabViewModel(
            _animeWorldService,
            _animeSaturnService,
            _animeProviderRegistry,
            _queueService,
            _fileNameBuilder,
            _settings,
            downloadLogger,
            Shared);

        Proxy = new ProxyTabViewModel(
            _proxyService,
            _animeWorldService,
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
            Shared);

            Domains = new DomainsTabViewModel(
            domainsResolver,
            domainTester,
            _animeWorldService,
            _animeSaturnService,
            domainsLogger,
            Shared);

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
        //  TOP BAR — BADGE PROXY
        // ============================================================

        Shared.ActiveProxyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ActiveProxyBadgeText));
            OnPropertyChanged(nameof(IsProxyActive));
        };

        // ============================================================
        //  IMPOSTAZIONI INIZIALI
        // ============================================================

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
            var packageSize = update.TargetFullRelease.Size;

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
                    packageSize,
                    changelog,
                    async progress =>
                    {
                        await mgr.DownloadUpdatesAsync(update, p => progress(p));
                    });
            });

            switch (choice)
            {
                case UpdateChoice.InstallNow:
                    mgr.ApplyUpdatesAndRestart(update);
                    break;

                case UpdateChoice.InstallLater:
                    mgr.WaitExitThenApplyUpdates(update);
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