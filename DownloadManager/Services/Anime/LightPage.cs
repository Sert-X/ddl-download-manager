using Microsoft.Playwright;

namespace DownloadManager.Services.Anime;

/// <summary>
/// Pagina "leggera" per ricerca ed elenco episodi: servono solo l'HTML e gli script del sito,
/// quindi immagini, video, font e fogli di stile non vengono scaricati (le locandine si
/// caricano a parte, dall'app). Non va usata per estrarre il video: lì servono tutte le risorse.
/// </summary>
internal static class LightPage
{
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "image", "media", "font", "stylesheet"
    };

    public static async Task<IPage> OpenAsync(IBrowserContext context)
    {
        var page = await context.NewPageAsync();
        await page.RouteAsync("**/*", async route =>
        {
            try
            {
                if (Blocked.Contains(route.Request.ResourceType)) await route.AbortAsync();
                else await route.ContinueAsync();
            }
            catch { /* pagina già chiusa */ }
        });
        return page;
    }
}
