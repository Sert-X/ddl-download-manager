using DownloadManager.Persistence;

namespace DownloadManager.Services.Download;

/// <summary>
/// Controlli prima di accodare: episodi già scaricati in passato (dalla cronologia) e spazio su
/// disco stimato. Un'istanza per ogni accodamento: in "Scarica tutta la serie" la scelta fatta
/// alla prima stagione vale anche per le altre, senza ripetere la domanda.
/// </summary>
public sealed class EnqueueGuard
{
    /// <summary>
    /// Domanda all'utente: (titolo, testo, pulsante conferma, pulsante annulla) → true se conferma.
    /// La imposta la finestra principale; senza (test) si procede senza chiedere.
    /// </summary>
    public static Func<string, string, string, string, Task<bool>>? Ask { get; set; }

    private readonly DownloadRepository _repo;
    private readonly string _category;
    private bool? _redownload;
    private bool _diskConfirmed;

    /// <summary>Episodi saltati perché già scaricati in passato.</summary>
    public int SkippedAlreadyDownloaded { get; private set; }

    public EnqueueGuard(DownloadRepository repo, string category)
    {
        _repo = repo;
        _category = category;
    }

    /// <summary>
    /// Gli elementi da accodare dopo i controlli; null se l'utente annulla (spazio insufficiente).
    /// </summary>
    public async Task<List<T>?> FilterAsync<T>(List<T> items, Func<T, int> episodeOf, string seriesName,
                                               int season, string destinationFolder, string label = "episodi")
    {
        if (items.Count == 0) return items;

        // 1) Già scaricati in passato (la cronologia resta anche dopo aver pulito la coda)
        HashSet<int> done;
        try { done = await Task.Run(() => _repo.GetDownloadedEpisodesAsync(_category, seriesName, season)); }
        catch { done = new HashSet<int>(); }
        var already = items.Where(i => episodeOf(i) > 0 && done.Contains(episodeOf(i))).ToList();
        if (already.Count > 0)
        {
            if (_redownload == null)
            {
                var nums = string.Join(", ", already.Take(15).Select(episodeOf)) + (already.Count > 15 ? "…" : "");
                var what = label == "film" ? $"Il film «{seriesName}» risulta già scaricato in passato."
                    : $"{already.Count} {(already.Count == 1 ? "episodio" : "episodi")} di «{seriesName}» {(already.Count == 1 ? "risulta già scaricato" : "risultano già scaricati")} in passato (n. {nums}).";
                _redownload = Ask == null || await Ask("Già scaricati", what + " Vuoi scaricarli di nuovo o saltarli? (Elenco completo in «Cronologia».)",
                    "Scarica di nuovo", "Salta");
            }
            if (_redownload == false)
            {
                items = items.Except(already).ToList();
                SkippedAlreadyDownloaded += already.Count;
            }
        }

        // 2) Spazio su disco: dimensione media degli ultimi file scaricati × quanti se ne accodano
        if (!_diskConfirmed && items.Count > 0 && Ask != null)
        {
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(destinationFolder));
                if (!string.IsNullOrEmpty(root))
                {
                    long free = new DriveInfo(root).AvailableFreeSpace;
                    long avg = await Task.Run(() => _repo.GetAverageSizeAsync(_category));
                    if (avg <= 0) avg = _category == "anime" ? 350L << 20 : 1200L << 20;
                    long needed = avg * items.Count;
                    if (free - needed < DownloadQueueService.MinFreeBytes)
                    {
                        var ok = await Ask("Spazio su disco",
                            $"Per {items.Count} {(items.Count == 1 ? "file" : "file")} servono circa {DownloadQueueService.FormatBytes(needed)} " +
                            $"(media degli ultimi download), ma su {root} restano {DownloadQueueService.FormatBytes(free)}. " +
                            "Se il disco si riempie, i download si fermano in pausa. Accodare comunque?",
                            "Accoda comunque", "Annulla");
                        if (!ok) return null;
                    }
                }
            }
            catch { /* disco non leggibile: si procede */ }
            _diskConfirmed = true;
        }
        return items;
    }

    /// <summary>Testo da aggiungere al messaggio di stato (", 3 già scaricati saltati").</summary>
    public string Note => SkippedAlreadyDownloaded > 0
        ? $", {SkippedAlreadyDownloaded} già scaricati in passato saltati"
        : "";
}
