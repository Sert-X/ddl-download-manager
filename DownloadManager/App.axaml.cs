using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using System.IO;
using DownloadManager.Persistence;
using DownloadManager.Services.AnimeWorld;
using DownloadManager.Services.Download;
using DownloadManager.Services.FileOrganizer;
using DownloadManager.Services.Logging;
using DownloadManager.Services.Sftp;
using DownloadManager.ViewModels;
using DownloadManager.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DownloadManager.Services.Proxy;
using System.Linq;

namespace DownloadManager;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // ==== DUMP TEMPORANEO ====
        try
        {
            var asm = System.Reflection.Assembly.Load("ManagedCode.Playwright.Stealth");
            Console.WriteLine("========= STEALTH DUMP =========");
            foreach (var t in asm.GetExportedTypes())
            {
                Console.WriteLine($"=== {t.FullName} ===");
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
                {
                    var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"));
                    Console.WriteLine($"  METHOD: {m.ReturnType.FullName} {m.Name}({ps})");
                }
                foreach (var p in t.GetProperties())
                {
                    Console.WriteLine($"  PROP:   {p.PropertyType.FullName} {p.Name}");
                }
                foreach (var c in t.GetConstructors())
                {
                    var ps = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.FullName} {p.Name}"));
                    Console.WriteLine($"  CTOR:   ({ps})");
                }
            }

            var pwAsm = System.Reflection.Assembly.Load("Microsoft.Playwright");
            Console.WriteLine("========= PLAYWRIGHT PROXY TYPES =========");
            foreach (var t in pwAsm.GetExportedTypes())
            {
                if (t.Name.Contains("Proxy", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"  TYPE: {t.FullName}");
            }
            Console.WriteLine("========= END DUMP =========");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DUMP FAIL] {ex}");
        }
        // ==== FINE DUMP ====
        var services = new ServiceCollection();

        // Crea LogService prima del logging provider, così il provider lo vede subito.
        var logService = new LogService();
        services.AddSingleton(logService);

        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddProvider(new UiLoggerProvider(logService));
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSingleton<IAnimeWorldService, AnimeWorldService>();
        services.AddSingleton<SftpConfigRepository>();
        services.AddSingleton<ISftpService, SftpWinScpService>();
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

        services.AddSingleton<SftpConfigRepository>();
        services.AddSingleton<ISftpService, SftpWinScpService>();
        services.AddSingleton<SftpUploadQueueService>();
        services.AddSingleton<SftpDownloadQueueService>();
        services.AddSingleton<ProxyConfigRepository>();
        services.AddSingleton<ProxyService>();

        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();

        Services = services.BuildServiceProvider();

        // Init DB su thread pool (evita deadlock sul thread UI di Avalonia).
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

        // Applica il tema salvato.
        var settings = Services.GetRequiredService<SettingsService>();
        ApplyTheme(settings.Current.ThemeVariant);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dragTemp = Path.Combine(Path.GetTempPath(), "DDLDownloadManager", "drag");
            if (Directory.Exists(dragTemp))
            {
                try
                {
                    Directory.Delete(dragTemp, true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TEMP-CLEANUP] {ex.Message}");
                }
            }

            desktop.MainWindow = Services.GetRequiredService<MainWindow>();

           // Check aggiornamenti in background (non blocca l'avvio)
            _ = Task.Run(async () =>
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("[UPDATE] Avvio check aggiornamenti...");

                    // Attendi che la MainWindow sia effettivamente visibile
                    await Task.Delay(3000);

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
                    var packageSize = update.TargetFullRelease.Size;

                    var choice = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                    {
                        var win = desktop.MainWindow;
                        if (win == null) return UpdateChoice.Cancel;

                        return await Views.UpdateDialog.ShowAsync(
                            win,
                            newVersion,
                            packageSize,
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
                }
            });

            desktop.ShutdownRequested += (s, e) =>
            {
                try
                {
                    var queue = Services!.GetRequiredService<DownloadQueueService>();
                    queue.CancelAll();

                    var sftpService = Services!.GetRequiredService<ISftpService>();
                    var t1 = sftpService.DisconnectAsync();
                    Task.WaitAny(t1, Task.Delay(3000));

                    var anime = Services!.GetRequiredService<IAnimeWorldService>();
                    var t2 = anime.DisposeAsync();
                    Task.WaitAny(t2, Task.Delay(3000));
                }
                catch { }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

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