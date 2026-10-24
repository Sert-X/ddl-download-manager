using DownloadManager.Models;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Anime;

/// <summary>
/// Link video di un episodio chiesto al sito al momento del download (non all'accodamento):
/// accodare 300 episodi è istantaneo e le richieste al sito si distribuiscono nel tempo,
/// invece di arrivare tutte insieme (il sito rispondeva 403 "troppe richieste").
/// Se il sito non risponde, o il browser interno è stato chiuso (es. cambio di proxy),
/// si riapre e si riprova con attese crescenti.
/// </summary>
public static class AnimeUrlResolver
{
    private const int MaxAttempts = 4;

    public static Func<CancellationToken, Task<string?>> CreateExtractor(
        IAnimeProvider provider, string episodeLink, string episodeNumber, ILogger? logger)
        => ct => ResolveAsync(provider, episodeLink, episodeNumber, logger, ct);

    public static async Task<string?> ResolveAsync(
        IAnimeProvider provider, string episodeLink, string episodeNumber, ILogger? logger, CancellationToken ct)
    {
        var episode = new Episode { Link = episodeLink, Number = episodeNumber, Title = $"Episodio {episodeNumber}" };
        Exception? lastError = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                // Riapre il browser interno se è stato chiuso (cambio di proxy, errore)
                await provider.InitializeAsync(headless: true);

                var urls = await provider.GetVideoUrlsBatchAsync(new List<Episode> { episode }, ct);
                if (urls.TryGetValue(episodeLink, out var url) && !string.IsNullOrEmpty(url))
                    return url;

                // AnimeWorld: se l'API non risponde si prova dalla pagina dell'episodio
                // (AnimeSaturn lo fa già da sé nel suo metodo a lotti)
                if (!string.Equals(provider.Name, "AnimeSaturn", StringComparison.OrdinalIgnoreCase))
                {
                    var single = await provider.GetVideoUrlAsync(episodeLink, ct);
                    if (!string.IsNullOrEmpty(single)) return single;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                lastError = ex;
                logger?.LogWarning("[{Provider}] Episodio {Ep}: tentativo {N}/{Max} non riuscito ({Msg})",
                    provider.Name, episodeNumber, attempt, MaxAttempts, ex.Message);
            }

            if (attempt < MaxAttempts)
            {
                // 5s, 20s, 45s: il sito di solito sblocca dopo qualche decina di secondi
                var wait = TimeSpan.FromSeconds(5 * attempt * attempt);
                logger?.LogInformation("[{Provider}] Episodio {Ep}: nuovo tentativo tra {Sec}s",
                    provider.Name, episodeNumber, (int)wait.TotalSeconds);
                await Task.Delay(wait, ct);
            }
        }

        throw new InvalidOperationException(lastError != null
            ? $"link non trovato dopo {MaxAttempts} tentativi ({lastError.Message})"
            : $"link non trovato dopo {MaxAttempts} tentativi: il sito non lo fornisce o sta bloccando le richieste");
    }
}
