using System.Collections.ObjectModel;
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
    public DomainsTabViewModel Domains { get; }

    // --- Log globale ---
    public ObservableCollection<LogEntry> Logs => _logService.Logs;

    // --- Badge proxy (top bar) ---
    public string ActiveProxyBadgeText
    {
        get
        {
            var s = _settings.Current;
            var providers = new List<string>();
            if (s.UseProxyForAnimeWorld && Proxy.Proxies.Any(p => p.IsEnabledForAnimeWorld)) providers.Add("AW");
            if (s.UseProxyForAnimeSaturn && Proxy.Proxies.Any(p => p.IsEnabledForAnimeSaturn)) providers.Add("AS");
            if (s.UseProxyForStreamingCommunity && Proxy.Proxies.Any(p => p.IsEnabledForStreamingCommunity)) providers.Add("SC");
            if (s.UseProxyForDownloads && Proxy.Proxies.Any(p => p.IsEnabledForDownloads)) providers.Add("DL");

            if (providers.Count == 0) return "🌐 Off";
            return $"🌐 {string.Join("+", providers)}";
        }
    }

    public bool IsProxyActive
    {
        get
        {
            var s = _settings.Current;
            return Proxy.Proxies.Any(p =>
                (s.UseProxyForAnimeWorld && p.IsEnabledForAnimeWorld) ||
                (s.UseProxyForAnimeSaturn && p.IsEnabledForAnimeSaturn) ||
                (s.UseProxyForStreamingCommunity && p.IsEnabledForStreamingCommunity) ||
                (s.UseProxyForDownloads && p.IsEnabledForDownloads));
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
        AccountTabViewModel accountVm)
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
            _animeWorldService,
            _animeSaturnService,
            _animeProviderRegistry,
            _queueService,
            _fileNameBuilder,
            _settings,
            downloadLogger,
            Shared);

        Streaming = new StreamingTabViewModel(
            _streamingCommunityService,
            _queueService,
            _fileNameBuilder,
            _settings,
            streamingLogger,
            Shared);

        Account = accountVm;

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
            Shared);

            Domains = new DomainsTabViewModel(
            domainsResolver,
            domainTester,
            _animeWorldService,
            _animeSaturnService,
            _streamingCommunityService,
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
    private async Task CopyLogsAsync()
    {
        try
        {
            var text = string.Join(Environment.NewLine,
                Logs.Select(l => $"{l.TimestampText} {l.LevelIcon} [{l.Category}] {l.Message}"));

            // Usa clip.exe di Windows: sempre presente, indipendente dall'API Avalonia.
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "clip.exe",
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return;

            await proc.StandardInput.WriteAsync(text);
            proc.StandardInput.Close();
            await proc.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[COPY-LOG] {ex}");
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
            var lines = Logs.Select(l =>
                $"{l.TimestampText} {l.LevelIcon} [{l.Category}] {l.Message}");

            await File.WriteAllLinesAsync(path, lines);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EXPORT-LOG] {ex}");
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