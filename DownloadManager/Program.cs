using Avalonia;
using System;
using System.IO;
using System.Threading;
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

        // 3) Una sola istanza: due finestre aperte condividono database e cartelle
        //    temporanee e scaricano gli stessi episodi in parallelo, rovinando i file.
        using var singleInstance = new Mutex(true, @"Local\DDLDownloadManager.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBoxW(IntPtr.Zero,
                "DDL Download Manager è già aperto.\n\nUsa la finestra già aperta (controlla anche la barra delle applicazioni).",
                "DDL Download Manager", 0x40 /* MB_ICONINFORMATION */);
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

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