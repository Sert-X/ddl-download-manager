using Avalonia;
using System;
using System.IO;
using Velopack;

namespace DownloadManager;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // 1) Velopack: DEVE essere la primissima cosa.
        //    Gestisce install/uninstall/update in startup e, se serve, riavvia il processo.
        VelopackApp.Build().Run();

        // 2) Playwright: se i browser sono accanto all'exe, puntaci.
        //    Altrimenti lascia il default (%LOCALAPPDATA%\ms-playwright).
        ConfigurePlaywrightBrowsersPath();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void ConfigurePlaywrightBrowsersPath()
    {
        try
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "ms-playwright");
            if (Directory.Exists(bundled))
            {
                Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", bundled);
            }
            // else: NON settare nulla → Playwright usa %LOCALAPPDATA%\ms-playwright
        }
        catch
        {
            // ignora: Playwright userà il default
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}