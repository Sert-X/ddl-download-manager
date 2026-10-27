using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using System.IO;
using System.Linq;
using DownloadManager.Persistence;
using DownloadManager.Services.Anime;
using DownloadManager.Services.Anime.AnimeWorld;
using DownloadManager.Services.Anime.AnimeSaturn;
using DownloadManager.Services.Streaming;
using DownloadManager.Services.Streaming.Cb01;
using DownloadManager.Services.Streaming.StreamingCommunity;
using DownloadManager.Services.Download;
using DownloadManager.Services.FileOrganizer;
using DownloadManager.Services.Logging;
using DownloadManager.Services.Sftp;
using DownloadManager.Services.Videobox;
using DownloadManager.ViewModels;
using DownloadManager.Views;
using DownloadManager.Services.Domains;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DownloadManager.Services.Proxy;

namespace DownloadManager;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        DownloadManager.Helpers.IconCentering.Install();
        DownloadManager.Helpers.TrimmedTextToolTip.Install();
        DownloadManager.Helpers.MarqueeText.Install();
    }

    /// <summary>
    /// Rete di sicurezza: un'eccezione non gestita (tipicamente in un handler
    /// async void della UI) finisce nel log globale invece di chiudere l'app.
    /// Gli errori fatali del dominio vengono anche scritti in crash.log.
    /// </summary>
    private static void InstallGlobalExceptionHandlers(ILogger logger)
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            logger.LogError(e.Exception, "Errore non gestito nell'interfaccia");
            e.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception.GetBaseException(), "Errore non gestito in un'operazione in background");
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDLDownloadManager");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.ExceptionObject}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { /* ultima difesa: non si può fare altro */ }
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ============================================================
        //  1. SERVIZI
        // ============================================================

        var services = new ServiceCollection();

        var logService = new LogService();
        services.AddSingleton(logService);

        // Niente console logger: l'app è WinExe e non ha una console da scriverci.
        services.AddLogging(builder =>
        {
            builder.AddProvider(new UiLoggerProvider(logService));
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton<IAnimeWorldService, AnimeWorldService>();
        services.AddSingleton<IAnimeProvider>(sp => sp.GetRequiredService<IAnimeWorldService>());
        services.AddSingleton<IAnimeSaturnService, AnimeSaturnService>();
        services.AddSingleton<IAnimeProvider>(sp => sp.GetRequiredService<IAnimeSaturnService>());
        services.AddSingleton<AnimeProviderRegistry>();
        services.AddSingleton<IStreamingProvider>(sp =>
            sp.GetRequiredService<IStreamingCommunityService>());
        services.AddSingleton<DownloadManager.Persistence.AccountRepository>();
        services.AddSingleton<DownloadManager.Persistence.MaxstreamAccountRepository>();
        services.AddSingleton<DownloadManager.Services.Streaming.StreamingCommunity.StreamingCommunityAuthService>();
        services.AddSingleton<DownloadManager.ViewModels.AccountTabViewModel>();
        services.AddSingleton<IStreamingCommunityService, StreamingCommunityService>();
        services.AddSingleton<ICb01Service, Cb01Service>();
        services.AddSingleton<MaxstreamService>();
        services.AddSingleton<StreamingProviderRegistry>();
        services.AddSingleton<FfmpegLocator>();
        services.AddSingleton<DownloadManager.Services.Download.YtDlpLocator>();

        services.AddSingleton<SftpSessionLimiter>(_ => new SftpSessionLimiter(maxSessions: 8));
        services.AddSingleton<SftpConfigRepository>();
        services.AddSingleton<ISftpService, SftpWinScpService>();
        services.AddSingleton<SftpUploadQueueService>();
        services.AddSingleton<SftpDownloadQueueService>();
        services.AddSingleton<SftpRelayQueueService>();

        services.AddSingleton<ProxyConfigRepository>();
        services.AddSingleton<ProxyService>();
        services.AddSingleton<IProxyProvider, ProxyProvider>();

        services.AddSingleton<IDownloadService, DownloadService>();
        services.AddSingleton<DownloadRepository>();
        services.AddSingleton<FileOperationRepository>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<FileNameBuilder>();
        services.AddSingleton<IFileOrganizerService, FileOrganizerService>();
        services.AddSingleton<DownloadQueueService>();

        services.AddSingleton<DomainResolver>();
        services.AddSingleton<DomainTester>();

        // Registra VideoboxApiService e VideoboxTabViewModel
        services.AddSingleton<VideoboxApiService>();
        services.AddSingleton<VideoboxSftpPool>();
        services.AddTransient<VideoboxTabViewModel>();

        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        Services = services.BuildServiceProvider();

        InstallGlobalExceptionHandlers(Services.GetRequiredService<ILogger<App>>());

        // ============================================================
        //  2. BOOTSTRAP FFMPEG (copia in %LOCALAPPDATA%)
        // ============================================================

        try
        {
            var ffmpegLogger = Services.GetRequiredService<ILogger<App>>();
            FfmpegLocator.EnsureBundledFfmpeg(ffmpegLogger);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FFMPEG-BOOTSTRAP] {ex.Message}");
        }

        // ============================================================
        //  3. INIT DATABASE
        // ============================================================

        Task.Run(async () =>
        {
            var repo = Services!.GetRequiredService<DownloadRepository>();
            await repo.InitializeAsync();

            var fileOpsRepo = Services!.GetRequiredService<FileOperationRepository>();
            await fileOpsRepo.InitializeAsync();

            var sftpRepo = Services!.GetRequiredService<SftpConfigRepository>();
            await sftpRepo.InitializeAsync();

            var proxyRepo = Services!.GetRequiredService<ProxyConfigRepository>();
            await proxyRepo.InitializeAsync();
        }).GetAwaiter().GetResult();

        // ============================================================
        //  4. TEMA
        // ============================================================

        var settings = Services.GetRequiredService<SettingsService>();
        ApplyTheme(settings.Current.ThemeVariant);

        // ============================================================
        //  5. AVVIO UI
        // ============================================================

        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        // Cleanup temp drag
        var dragTemp = Path.Combine(Path.GetTempPath(), "DDLDownloadManager", "drag");
        if (Directory.Exists(dragTemp))
        {
            try { Directory.Delete(dragTemp, true); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TEMP-CLEANUP] {ex.Message}");
            }
        }

        // Verifica se serve il bootstrap Playwright
        bool needsBootstrap = !ArePlaywrightBrowsersInstalled();

        if (needsBootstrap)
        {
            // === PRIMO AVVIO: mostra BootstrapDialog, poi MainWindow ===
            var bootstrapDialog = new BootstrapDialog();
            desktop.MainWindow = bootstrapDialog;
            bootstrapDialog.Show();

            _ = Task.Run(async () =>
            {
                bool success = await bootstrapDialog.RunBootstrapAsync();

                // Aspetta un attimo per far leggere il messaggio finale all'utente
                await Task.Delay(1500);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        var mainWindow = Services.GetRequiredService<MainWindow>();
                        desktop.MainWindow = mainWindow;
                        mainWindow.Show();

                        try { bootstrapDialog.Close(); } catch { }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BOOTSTRAP] Errore apertura MainWindow: {ex}");
                    }
                });

                // Ripristina coda download
                try
                {
                    var queue = Services!.GetRequiredService<DownloadQueueService>();
                    await queue.RestoreFromDatabaseAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RESTORE] Errore: {ex}");
                }

                // Check aggiornamenti
                await CheckUpdatesAsync(desktop);
            });
        }
        else
        {
            // === AVVII SUCCESSIVI: MainWindow diretta ===
            desktop.MainWindow = Services.GetRequiredService<MainWindow>();

            _ = Task.Run(async () =>
            {
                try
                {
                    var queue = Services!.GetRequiredService<DownloadQueueService>();
                    await queue.RestoreFromDatabaseAsync();
                    System.Diagnostics.Debug.WriteLine("[RESTORE] Ripristino completato");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[RESTORE] Errore: {ex}");
                }

                await CheckUpdatesAsync(desktop);
            });
        }

        desktop.ShutdownRequested += OnShutdownRequested;

        base.OnFrameworkInitializationCompleted();
    }

    // ============================================================
    //  PLAYWRIGHT BOOTSTRAP
    // ============================================================

    private static bool ArePlaywrightBrowsersInstalled()
    {
        try
        {
            var browsersPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ms-playwright");

            return Directory.Exists(browsersPath) &&
                Directory.EnumerateDirectories(browsersPath, "chromium*").Any();
        }
        catch
        {
            return false;
        }
    }

    // ============================================================
    //  UPDATE CHECK
    // ============================================================

    private static async Task CheckUpdatesAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[UPDATE] Avvio check aggiornamenti...");

            // Attendi che la MainWindow sia visibile
            await Task.Delay(3000);

            await ShowWhatsNewIfUpdatedAsync(desktop);

            var mgr = new Velopack.UpdateManager(
                new Velopack.Sources.GithubSource(
                    "https://github.com/Sert-X/ddl-download-manager",
                    accessToken: null,
                    prerelease: false));

            var update = await mgr.CheckForUpdatesAsync();

            if (update == null)
            {
                System.Diagnostics.Debug.WriteLine("[UPDATE] Nessun aggiornamento.");
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[UPDATE] Trovata versione {update.TargetFullRelease.Version}");

            var newVersion = update.TargetFullRelease.Version.ToString();

            var changelog = await DownloadManager.Services.Update.GitHubReleaseFetcher.FetchChangelogAsync(
                "Sert-X", "ddl-download-manager", "v" + newVersion);

            var choice = await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var win = desktop.MainWindow;
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

            // silent: niente finestra di sistema di Velopack ("Aggiornamento alla versione..."):
            // l'avanzamento lo mostra il nostro dialog e, al riavvio, la schermata delle novità
            switch (choice)
            {
                case UpdateChoice.InstallNow:
                    mgr.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: true);
                    await Dispatcher.UIThread.InvokeAsync(() => desktop.Shutdown());
                    break;

                case UpdateChoice.InstallLater:
                    mgr.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UPDATE] ECCEZIONE: {ex}");
        }
    }

    /// <summary>App installata con l'installer (Velopack), non avviata dal progetto in sviluppo.</summary>
    private static bool IsInstalledBuild()
    {
        try
        {
            var mgr = new Velopack.UpdateManager(new Velopack.Sources.GithubSource(
                "https://github.com/Sert-X/ddl-download-manager", accessToken: null, prerelease: false));
            return mgr.IsInstalled;
        }
        catch { return false; }
    }

    /// <summary>Primo avvio dopo un aggiornamento: finestra "Aggiornamento completato" con le novità.</summary>
    private static async Task ShowWhatsNewIfUpdatedAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            if (v == null) return;
            var current = new Version(v.Major, v.Minor, Math.Max(0, v.Build));

            // Solo l'app installata: avviata da Visual Studio (bin\Debug) non deve né mostrare né
            // segnare niente, altrimenti le due versioni si "rubano" la finestra a vicenda.
            if (!IsInstalledBuild()) return;

            // L'ultima versione vista sta in un file suo, scritto solo qui e solo verso l'alto.
            // Prima stava in settings.json, che ogni copia dell'app in esecuzione riscrive per
            // intero con i propri valori: la versione vista tornava indietro e la finestra
            // "Aggiornamento completato" ricompariva ai riavvii successivi.
            var settings = Services!.GetRequiredService<SettingsService>();
            var seenPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DDLDownloadManager", "last_seen_version.txt");
            Version? previous = null;
            try { if (File.Exists(seenPath)) Version.TryParse(File.ReadAllText(seenPath).Trim(), out previous); } catch { }
            if (previous == null) Version.TryParse(settings.Current.LastRunVersion, out previous);   // installazioni precedenti

            if (previous != null && current <= previous) return;   // già vista (o versione più vecchia)
            try { File.WriteAllText(seenPath, current.ToString(3)); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[UPDATE] Versione vista non salvata: {ex.Message}"); return; }
            settings.Current.LastRunVersion = current.ToString(3);
            settings.Save();
            if (previous == null) return;   // prima installazione: niente novità da mostrare

            var changelog = await DownloadManager.Services.Update.GitHubReleaseFetcher.FetchChangelogAsync(
                "Sert-X", "ddl-download-manager", "v" + current.ToString(3));

            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (desktop.MainWindow is { } win)
                    await Views.UpdateDialog.ShowUpdatedAsync(win, current.ToString(3), changelog);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UPDATE] Novità non mostrate: {ex.Message}");
        }
    }

    // ============================================================
    //  SHUTDOWN
    // ============================================================

    private static EventWaitHandle? _closingSignal;

    private static void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // "Mi sto chiudendo": se l'utente riapre subito l'app, la nuova istanza aspetta
                // che questa finisca invece di dire "già aperto"
                try { _closingSignal ??= new EventWaitHandle(false, EventResetMode.ManualReset, Program.ClosingSignalName); } catch { }

                var queue = Services!.GetRequiredService<DownloadQueueService>();
                queue.CancelAll("anime");
                queue.CancelAll("streaming");

                // Connessioni e browser si chiudono tutti insieme, con 3 secondi in tutto
                // (prima uno dopo l'altro, fino a 3 secondi ciascuno: la chiusura durava fino a ~10 s)
                var vm = desktop.MainWindow?.DataContext as MainWindowViewModel;
                var jobs = new List<Task>
                {
                    Task.Run(() => { try { vm?.Sftp.Dispose(); } catch { } }),
                    Task.Run(() => { try { Services!.GetRequiredService<VideoboxSftpPool>().Dispose(); } catch { } }),
                    Task.Run(async () => { try { await Services!.GetRequiredService<ISftpService>().DisconnectAsync(); } catch { } }),
                    Task.Run(async () => { try { await Services!.GetRequiredService<IAnimeWorldService>().DisposeAsync(); } catch { } }),
                    Task.Run(async () => { try { await Services!.GetRequiredService<MaxstreamService>().DisposeAsync(); } catch { } }),
                };
                Task.WaitAll(jobs.ToArray(), TimeSpan.FromSeconds(3));
            }
        }
        catch { }
    }

    // ============================================================
    //  TEMA
    // ============================================================

    public static void ApplyTheme(string themeVariant)
    {
        var current = Application.Current;
        if (current == null) return;

        current.RequestedThemeVariant = themeVariant switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}