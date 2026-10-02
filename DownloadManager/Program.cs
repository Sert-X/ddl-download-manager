using Avalonia;
using System;

namespace DownloadManager;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
       // Fai puntare Playwright alla cartella ms-playwright accanto all'eseguibile.
        var browsersPath = Path.Combine(AppContext.BaseDirectory, "ms-playwright");
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);

       BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

// Usare quanto segue durante la compilazione publish.

// using System;
//using System.IO;
//using Avalonia;
//
//namespace DownloadManager;
//
//sealed class Program
//{
//    [STAThread]
//    public static void Main(string[] args)
//    {
//        // Fai puntare Playwright alla cartella ms-playwright accanto all'eseguibile.
//        var browsersPath = Path.Combine(AppContext.BaseDirectory, "ms-playwright");
//        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", browsersPath);
//
//       BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
//    }
//
//    public static AppBuilder BuildAvaloniaApp()
//        => AppBuilder.Configure<App>()
//            .UsePlatformDetect()
//#if DEBUG
//            .WithDeveloperTools()
//#endif
//            .WithInterFont()
//            .LogToTrace();
//}