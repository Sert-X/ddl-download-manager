using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Streaming.StreamingCommunity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Download;

public class DownloadQueueService
{
    private readonly DownloadRepository _repository;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DownloadQueueService> _logger;

    private readonly List<DownloadItem> _pending = new();
    private readonly HashSet<int> _pendingIds = new();
    private readonly Dictionary<int, CancellationTokenSource> _active = new();
    /// <summary>Categoria (anime / streaming) di ogni download attivo: ogni categoria ha i suoi slot.</summary>
    private readonly Dictionary<int, string> _activeCategory = new();
    private readonly object _queueLock = new();

    private readonly SemaphoreSlim _dispatchGate = new(1, 1);

    private const int DefaultMaxConcurrency = 3;
    private readonly Dictionary<string, int> _maxConcurrency = new();

    /// <summary>
    /// Categorie la cui coda è in esecuzione. All'avvio è vuoto: i download ripristinati
    /// dalla sessione precedente restano fermi finché in quella categoria non si avvia
    /// qualcosa (Avvia tutti, nuovo accodamento, riprendi...). Accodare un anime non fa più
    /// partire i film rimasti in coda, e viceversa.
    /// </summary>
    private readonly HashSet<string> _runningCategories = new();

    /// <summary>Download in corso fermati per far posto a uno forzato: tornano subito in coda.</summary>
    private readonly HashSet<int> _preempted = new();

    /// <summary>Un download è finito: completato oppure fallito in modo definitivo (per le notifiche).</summary>
    public event Action<DownloadItem>? ItemFinished;

    /// <summary>Un download non parte perché sul disco resta troppo poco spazio.</summary>
    public event Action<string, long>? DiskSpaceLow;

    // Nuovi tentativi automatici dopo un errore temporaneo (rete, proxy, server occupato)
    public const int MaxAutoRetries = 3;
    public static TimeSpan[] RetryDelays { get; set; } = { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5) };

    /// <summary>Spazio minimo da lasciare libero: sotto questa soglia i download non partono.</summary>
    public const long MinFreeBytes = 1L << 30;   // 1 GB
    private readonly Dictionary<string, (DateTime At, long Free)> _freeCache = new(StringComparer.OrdinalIgnoreCase);

    public DownloadRepository Repository => _repository;
    private bool _isRunning = true;

    public DownloadQueueService(
        DownloadRepository repository,
        IServiceProvider serviceProvider,
        ILogger<DownloadQueueService> logger)
    {
        _repository = repository;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public ObservableCollection<DownloadItem> AllItems { get; } = new();

    /// <summary>
    /// Salvataggi sul DB sempre fuori dal thread dell'interfaccia: SQLite lavora in modo
    /// sincrono anche nei metodi "async", quindi accodando una serie (500 episodi = ~75 ms, molto
    /// di più col disco occupato dai download) l'interfaccia restava ferma per tutto il tempo.
    /// </summary>
    private static Task Db(Func<Task> work) => Task.Run(work);
    private static Task<T> Db<T>(Func<Task<T>> work) => Task.Run(work);

    /// <summary>Download simultanei per una categoria (ognuna ha i propri slot).</summary>
    public int GetMaxConcurrency(string category)
    {
        lock (_queueLock)
            return _maxConcurrency.TryGetValue(category, out var v) ? v : DefaultMaxConcurrency;
    }

    public void SetMaxConcurrency(string category, int value)
    {
        value = Math.Clamp(value, 1, 20);
        lock (_queueLock)
        {
            if (_maxConcurrency.TryGetValue(category, out var old) && old == value) return;
            _maxConcurrency[category] = value;
        }
        _logger.LogInformation("[QUEUE] Download simultanei per '{Category}': {Value}", category, value);
        TryDispatch();
    }

    public int ActiveCount { get { lock (_queueLock) return _active.Count; } }
    public int PendingCount { get { lock (_queueLock) return _pending.Count; } }

    private void MarkRunning(string category)
    {
        lock (_queueLock) _runningCategories.Add(category);
    }

    public event Action? QueueChanged;

    // ============================================================
    //  RESTORE / ENQUEUE
    // ============================================================

    /// <summary>
    /// Elimina le cartelle temporanee di yt-dlp (una per download, chiamata con l'Id)
    /// che non appartengono a nessun download ancora da finire: residui di download
    /// falliti, annullati o rimossi che altrimenti restano su disco per sempre.
    /// </summary>
    private void CleanupStaleTempFolders(HashSet<int> keepIds)
    {
        var root = Path.Combine(Path.GetTempPath(), "DDLDownloadManager", "ytdlp");
        if (!Directory.Exists(root)) return;

        int removed = 0;
        foreach (var dir in Directory.GetDirectories(root))
        {
            if (int.TryParse(Path.GetFileName(dir), out var id) && keepIds.Contains(id)) continue;
            try { Directory.Delete(dir, recursive: true); removed++; }
            catch (Exception ex) { _logger.LogDebug(ex, "[QUEUE] Cartella temporanea non eliminabile: {Dir}", dir); }
        }
        if (removed > 0)
            _logger.LogInformation("[QUEUE] Eliminate {N} cartelle temporanee di download non più in coda", removed);
    }

    public async Task RestoreFromDatabaseAsync()
    {
        // Prima di riprendere qualsiasi download: via i yt-dlp/ffmpeg orfani di una
        // sessione chiusa male, che altrimenti scriverebbero negli stessi file.
        try { DownloadManager.Helpers.OrphanProcessCleaner.KillOrphans(_logger); }
        catch (Exception ex) { _logger.LogWarning(ex, "[QUEUE] Pulizia processi orfani fallita"); }

        var unfinished = await Db(() => _repository.GetUnfinishedAsync());
        _logger.LogInformation("Ripristino {Count} download non completati (in pausa)", unfinished.Count);

        CleanupStaleTempFolders(unfinished.Select(i => i.Id).ToHashSet());

        foreach (var item in unfinished)
        {
            RewireStreamingExtractors(item);
            RewireCb01Extractors(item);
            RewireAnimeExtractors(item);
            item.Status = DownloadStatus.Pending;
            await AddToListAsync(item);
            lock (_queueLock)
            {
                if (_pendingIds.Add(item.Id))
                    _pending.Add(item);
            }
        }
    }

    /// <summary>Anime accodati senza link (non ancora partiti): il link si chiederà al sito all'avvio.</summary>
    private void RewireAnimeExtractors(DownloadItem item)
    {
        if (item.Category != "anime" || item.UrlExtractor != null) return;
        if (!string.IsNullOrEmpty(item.Url) || string.IsNullOrEmpty(item.ContentLink)) return;

        var registry = _serviceProvider.GetService<DownloadManager.Services.Anime.AnimeProviderRegistry>();
        var provider = registry?.Get(item.ProviderKey);
        if (provider == null)
        {
            _logger.LogWarning("[REWIRE] Provider anime '{Key}' non trovato per {Series} ep. {Ep}", item.ProviderKey, item.SeriesName, item.EpisodeNumber);
            return;
        }
        item.UrlExtractor = DownloadManager.Services.Anime.AnimeUrlResolver.CreateExtractor(
            provider, item.ContentLink, item.EpisodeNumber.ToString(), _logger);
    }

    /// <summary>CB01: il link Maxstream vale solo per l'IP e scade, quindi si sceglie di nuovo al via.</summary>
    private void RewireCb01Extractors(DownloadItem item)
    {
        if (item.ProviderKey != "CB01" || item.UrlExtractor != null || string.IsNullOrEmpty(item.ContentLink)) return;

        var cb01 = _serviceProvider.GetService<DownloadManager.Services.Streaming.Cb01.ICb01Service>();
        if (cb01 == null)
        {
            _logger.LogWarning("[REWIRE] ICb01Service non disponibile");
            return;
        }
        cb01.AttachExtractor(item);
    }

    private void RewireStreamingExtractors(DownloadItem item)
    {
        if (item.ProviderKey != "StreamingCommunity") return;
        if (string.IsNullOrEmpty(item.ContentLink)) return;
        if (item.UrlExtractor != null) return;

        var sc = _serviceProvider.GetService<IStreamingCommunityService>();
        if (sc == null)
        {
            _logger.LogWarning("[REWIRE] IStreamingCommunityService non disponibile");
            return;
        }

        var capturedLink = item.ContentLink;
        var capturedIsSeries = item.IsSeries;
        var capturedSeason = item.SeasonNumber;
        var capturedEp = item.EpisodeNumber;
        var capturedEpId = item.EpisodeId > 0 ? (int?)item.EpisodeId : null;
        // Restituisce la playlist master: qualità, lingue audio e sottotitoli vengono
        // scelti dal download stesso (DownloadService.Tracks) in base ai campi salvati.
        item.UrlExtractor = _ => sc.GetVideoUrlAsync(
            capturedLink, isSeries: capturedIsSeries,
            seasonNumber: capturedSeason,
            episodeNumber: capturedEp,
            episodeId: capturedEpId);

        item.CookieExtractor = async _ => await sc.GetSessionCookiesNetscapeAsync();
    }

    // ============================================================
    //  ENQUEUE singolo
    // ============================================================

    public async Task EnqueueAsync(DownloadItem item)
    {
        if (item.Id == 0)
            await Db(() => _repository.InsertAsync(item));

        await AddToListAsync(item);

        item.Status = DownloadStatus.Pending;

        lock (_queueLock)
        {
            if (_pendingIds.Add(item.Id))
                _pending.Add(item);
        }

        MarkRunning(item.Category);

        QueueChanged?.Invoke();
        TryDispatch();
    }

    // ============================================================
    //  ENQUEUE multiplo — 1 transazione SQLite, 1 notifica UI
    //  (usare questo per accodare decine di episodi)
    // ============================================================

    public async Task EnqueueManyAsync(IReadOnlyList<DownloadItem> items)
    {
        if (items == null || items.Count == 0) return;

        // 1. INSERT in blocco (una sola transazione SQLite)
        var toInsert = new List<DownloadItem>(items.Count);
        foreach (var item in items)
        {
            if (item.Id == 0) toInsert.Add(item);
        }

        if (toInsert.Count > 0)
            await Db(() => _repository.InsertManyAsync(toInsert));

        // 2. Prepara gli item (già creati da chiamante, quindi modifiche in-memory)
        foreach (var item in items)
            item.Status = DownloadStatus.Pending;

        // 3. Aggiungi TUTTI ad AllItems con UNA SOLA InvokeAsync
        //    (HashSet: evita una scansione di AllItems per ogni item)
        void AddAll()
        {
            var present = AllItems.ToHashSet();
            foreach (var item in items)
                if (present.Add(item)) AllItems.Add(item);
        }

        if (Dispatcher.UIThread.CheckAccess()) AddAll();
        else await Dispatcher.UIThread.InvokeAsync(AddAll);

        // 4. Aggiungi TUTTI alla pending con UN SOLO lock
        lock (_queueLock)
        {
            foreach (var item in items)
            {
                if (_pendingIds.Add(item.Id))
                    _pending.Add(item);
            }
        }

        foreach (var category in items.Select(i => i.Category).Distinct())
            MarkRunning(category);

        // 5. UNA SOLA notifica + UN SOLO dispatch
        QueueChanged?.Invoke();
        TryDispatch();

        _logger.LogInformation("[QUEUE] EnqueueMany: {Count} item accodati in blocco", items.Count);
    }

    private async Task AddToListAsync(DownloadItem item)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (!AllItems.Contains(item)) AllItems.Add(item);
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!AllItems.Contains(item)) AllItems.Add(item);
            });
        }
    }

    public bool ContainsEpisode(string seriesName, int episodeNumber)
    {
        if (string.IsNullOrEmpty(seriesName) && episodeNumber == 0)
            return false;

        return AllItems.Any(i =>
            string.Equals(i.SeriesName, seriesName, StringComparison.OrdinalIgnoreCase) &&
            i.EpisodeNumber == episodeNumber);
    }

    public bool ContainsEpisode(string category, string seriesName, int episodeNumber)
    {
        if (string.IsNullOrEmpty(seriesName) && episodeNumber == 0) return false;

        return AllItems.Any(i =>
            i.Category == category &&
            string.Equals(i.SeriesName, seriesName, StringComparison.OrdinalIgnoreCase) &&
            i.EpisodeNumber == episodeNumber);
    }

    /// <summary>
    /// Snapshot veloce dei numeri di episodio già presenti in coda per una serie
    /// e una category. Sostituisce N chiamate a ContainsEpisode (che scansiona
    /// la collection ogni volta) con UNA sola scansione.
    /// </summary>
    public HashSet<int> SnapshotExistingEpisodeKeys(string category, string seriesName)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrEmpty(seriesName)) return result;

        foreach (var i in AllItems)
        {
            if (i.Category == category &&
                string.Equals(i.SeriesName, seriesName, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(i.EpisodeNumber);
            }
        }
        return result;
    }

    // ============================================================
    //  FORZA PRIORITÀ
    // ============================================================

    public void ForceQueueItem(DownloadItem item)
    {
        if (!EnqueueForced(item)) return;
        MarkRunning(item.Category);
        MakeRoomForPriority(item.Category);
        QueueChanged?.Invoke();
        TryDispatch();
    }

    /// <summary>Mette l'item in cima alla coda (dietro gli altri forzati). false = non serve.</summary>
    private bool EnqueueForced(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) return false;

        lock (_queueLock)
        {
            if (_active.ContainsKey(item.Id))
                return false;

            _pending.RemoveAll(i => i.Id == item.Id);
            _pendingIds.Remove(item.Id);

            item.IsPriority = true;
            item.Status = DownloadStatus.Pending;
            item.PauseRequested = false;
            item.ErrorMessage = string.Empty;
            item.AutoRetryCount = 0;

            int insertAt = 0;
            while (insertAt < _pending.Count && _pending[insertAt].IsPriority)
                insertAt++;

            _pending.Insert(insertAt, item);
            _pendingIds.Add(item.Id);
        }
        return true;
    }

    public void ForceQueueGroup(SeriesGroup group)
    {
        var categories = new HashSet<string>();
        foreach (var item in group.Items.ToList())
        {
            if (item.Status == DownloadStatus.Completed) continue;
            lock (_queueLock) if (_active.ContainsKey(item.Id)) continue;
            if (EnqueueForced(item)) categories.Add(item.Category);
        }
        foreach (var c in categories)
        {
            MarkRunning(c);
            MakeRoomForPriority(c);
        }
        QueueChanged?.Invoke();
        TryDispatch();
    }

    /// <summary>
    /// Se gli slot della categoria sono tutti occupati, ferma i download non forzati meno avanzati
    /// (uno per ogni forzato in attesa) così i forzati partono subito. I download fermati tornano
    /// in coda per primi dopo i forzati e riprendono da dove erano. Quelli già in conversione
    /// (download finito) non vengono toccati.
    /// </summary>
    private void MakeRoomForPriority(string category)
    {
        var toCancel = new List<CancellationTokenSource>();
        lock (_queueLock)
        {
            int max = _maxConcurrency.TryGetValue(category, out var m) ? m : DefaultMaxConcurrency;
            int busy = 0;
            foreach (var kv in _activeCategory)
                if (kv.Value == category && !_preempted.Contains(kv.Key)) busy++;
            int waiting = 0;
            foreach (var p in _pending)
                if (p.IsPriority && p.Category == category) waiting++;
            int need = waiting - Math.Max(0, max - busy);
            if (need <= 0) return;

            var byId = AllItems.ToDictionary(i => i.Id);
            var victims = _activeCategory
                .Where(kv => kv.Value == category && !_preempted.Contains(kv.Key))
                .Select(kv => byId.GetValueOrDefault(kv.Key))
                .Where(i => i != null && !i.IsPriority && !IsFinishing(i))
                .OrderBy(i => i!.TotalBytes > 0 ? (double)i.DownloadedBytes / i.TotalBytes : 0)
                .Take(need)
                .ToList();
            foreach (var v in victims)
            {
                _preempted.Add(v!.Id);
                if (_active.TryGetValue(v.Id, out var cts)) toCancel.Add(cts);
                _logger.LogInformation("[QUEUE] {File} messo in pausa per far posto a un download forzato", v.FileName);
            }
        }
        foreach (var cts in toCancel)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Download finito e in conversione/unione: fermarlo farebbe perdere il lavoro.</summary>
    private static bool IsFinishing(DownloadItem i) =>
        (i.TotalBytes > 0 && i.DownloadedBytes >= i.TotalBytes) || !string.IsNullOrEmpty(i.ErrorMessage);

    public void UnforceQueueItem(DownloadItem item)
    {
        if (!item.IsPriority) return;

        item.IsPriority = false;

        lock (_queueLock)
        {
            if (_active.ContainsKey(item.Id))
            {
                QueueChanged?.Invoke();
                return;
            }

            int index = _pending.FindIndex(i => i.Id == item.Id);
            if (index >= 0)
            {
                _pending.RemoveAt(index);
                _pending.Add(item);
            }
        }

        QueueChanged?.Invoke();
    }

    public void UnforceQueueGroup(SeriesGroup group)
    {
        foreach (var item in group.Items.ToList())
        {
            if (item.IsPriority)
                UnforceQueueItem(item);
        }
    }

    public void CancelGroup(SeriesGroup group)
    {
        foreach (var item in group.Items.ToList())
            CancelItem(item);
    }

    // ============================================================
    //  PER SINGOLO ITEM
    // ============================================================

    public void PauseItem(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) return;

        item.PauseRequested = true;

        if (_active.TryGetValue(item.Id, out var cts))
        {
            cts.Cancel();
        }
        else
        {
            lock (_queueLock)
            {
                _pending.RemoveAll(i => i.Id == item.Id);
                _pendingIds.Remove(item.Id);
            }
            item.ErrorMessage = string.Empty;   // es. "Nuovo tentativo alle..." in attesa
            item.Status = DownloadStatus.Paused;
        }

        QueueChanged?.Invoke();
    }

    public void ResumeItem(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Completed) return;

        lock (_queueLock)
        {
            if (_active.ContainsKey(item.Id))
                return;
        }

        item.Status = DownloadStatus.Pending;
        item.PauseRequested = false;
        item.ErrorMessage = string.Empty;
        item.AutoRetryCount = 0;

        lock (_queueLock)
        {
            if (_pendingIds.Add(item.Id))
                _pending.Add(item);
        }

        MarkRunning(item.Category);

        QueueChanged?.Invoke();
        TryDispatch();
    }

    public void CancelItem(DownloadItem item)
    {
        item.PauseRequested = false;

        if (_active.TryGetValue(item.Id, out var cts))
        {
            cts.Cancel();
        }
        else
        {
            lock (_queueLock)
            {
                _pending.RemoveAll(i => i.Id == item.Id);
                _pendingIds.Remove(item.Id);
            }
            item.Status = DownloadStatus.Cancelled;
        }

        QueueChanged?.Invoke();
    }

    public async Task RemoveItemAsync(DownloadItem item)
    {
        if (_active.TryGetValue(item.Id, out var cts))
        {
            cts.Cancel();
            await Task.Delay(200);
        }

        lock (_queueLock)
        {
            _pending.RemoveAll(i => i.Id == item.Id);
            _pendingIds.Remove(item.Id);
        }

        if (item.Id > 0)
        {
            try { await Db(() => _repository.DeleteAsync(item.Id)); }
            catch (Exception ex) { _logger.LogWarning(ex, "[QUEUE] Eliminazione dal database fallita (Id {Id})", item.Id); }
        }

        await Dispatcher.UIThread.InvokeAsync(() => AllItems.Remove(item));

        QueueChanged?.Invoke();
    }

    public async Task RemoveManyAsync(IReadOnlyList<DownloadItem> items)
    {
        if (items == null || items.Count == 0) return;

        var activeCtsToCancel = new List<CancellationTokenSource>();
        var idsToRemove = new HashSet<int>();

        lock (_queueLock)
        {
            foreach (var item in items)
            {
                idsToRemove.Add(item.Id);
                if (_active.TryGetValue(item.Id, out var cts))
                {
                    activeCtsToCancel.Add(cts);
                }
            }

            // Rimuoviamo subito dalla coda interna
            _pending.RemoveAll(i => idsToRemove.Contains(i.Id));
            foreach (var id in idsToRemove)
                _pendingIds.Remove(id);
        }

        // 1. Cancella i download attivi (in parallelo)
        foreach (var cts in activeCtsToCancel)
        {
            try { cts.Cancel(); } catch { }
        }

        // Se c'erano file attivi, aspetta UNA sola volta per tutti
        if (activeCtsToCancel.Count > 0)
        {
            await Task.Delay(200); 
        }

        // 2. Elimina dal Database con una sola query
        await DeleteFromDatabaseAsync(items);

        // 3. Rimuovi dalla UI in un solo colpo (UNA SOLA InvokeAsync)
        await RemoveFromAllItemsAsync(items);

        // 4. Notifica una sola volta
        QueueChanged?.Invoke();
    }

    // ============================================================
    //  RETRY
    // ============================================================

    public async Task RetryItemAsync(DownloadItem item)
    {
        if (item.Status != DownloadStatus.Failed && item.Status != DownloadStatus.Cancelled)
            return;

        try
        {
            var partPath = item.DestinationPath + ".part";
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
                _logger.LogInformation("Rimosso .part per retry: {Path}", partPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossibile eliminare .part per retry: {Path}", item.DestinationPath);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            item.DownloadedBytes = 0;
            item.SpeedBytesPerSecond = 0;
            item.ErrorMessage = string.Empty;
            item.Status = DownloadStatus.Pending;
            item.PauseRequested = false;
            item.IsPriority = false;
            item.AutoRetryCount = 0;
        });

        try { await Db(() => _repository.ClearChunksAsync(item.Id)); }
        catch (Exception ex) { _logger.LogWarning(ex, "[QUEUE] Pulizia chunk fallita (Id {Id})", item.Id); }

        // Aspetta che il tentativo precedente sia davvero chiuso (yt-dlp può metterci
        // qualche secondo): ripartire prima voleva dire due processi sugli stessi file.
        for (int i = 0; i < 300; i++)
        {
            lock (_queueLock)
            {
                if (!_active.ContainsKey(item.Id))
                    break;
            }
            await Task.Delay(50);
        }
        lock (_queueLock)
        {
            if (_active.ContainsKey(item.Id))
            {
                _logger.LogWarning("[QUEUE] Riprova annullata: il download {Id} non si è ancora fermato", item.Id);
                Dispatcher.UIThread.Post(() =>
                {
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = "Il tentativo precedente si sta ancora chiudendo: riprova tra qualche secondo.";
                });
                return;
            }
        }

        lock (_queueLock)
        {
            _pending.RemoveAll(i => i.Id == item.Id);
            _pendingIds.Remove(item.Id);

            if (_pendingIds.Add(item.Id))
                _pending.Add(item);
        }

        MarkRunning(item.Category);

        QueueChanged?.Invoke();
        TryDispatch();
    }

    public async Task RetryFailedInGroupAsync(SeriesGroup group)
    {
        var failedItems = group.Items
            .Where(i => i.Status == DownloadStatus.Failed)
            .ToList();

        if (failedItems.Count == 0)
            return;

        _logger.LogInformation(
            "Retry di {Count} failed nella serie {Series}",
            failedItems.Count, group.SeriesName);

        foreach (var item in failedItems)
            await RetryItemAsync(item);
    }

    public async Task RetryCancelledInGroupAsync(SeriesGroup group)
    {
        var cancelledItems = group.Items
            .Where(i => i.Status == DownloadStatus.Cancelled)
            .ToList();

        if (cancelledItems.Count == 0)
            return;

        _logger.LogInformation(
            "Retry di {Count} cancelled nella serie {Series}",
            cancelledItems.Count, group.SeriesName);

        foreach (var item in cancelledItems)
            await RetryItemAsync(item);
    }

    // ============================================================
    //  VARIANTI PER CATEGORY
    // ============================================================

    public void StartAll(string category)
    {
        MarkRunning(category);

        lock (_queueLock)
        {
            foreach (var item in AllItems.Where(i => i.Category == category))
            {
                if (item.Status == DownloadStatus.Paused ||
                    item.Status == DownloadStatus.Pending ||
                    item.Status == DownloadStatus.Failed)
                {
                    item.Status = DownloadStatus.Pending;
                    item.ErrorMessage = string.Empty;

                    if (!_active.ContainsKey(item.Id) && _pendingIds.Add(item.Id))
                        _pending.Add(item);
                }
            }
        }

        QueueChanged?.Invoke();
        TryDispatch();
    }

    /// <summary>
    /// Toglie dalla coda d'attesa gli item di una categoria, anche dall'indice degli ID:
    /// senza questo, "Avvia tutti" dopo una pausa non li riaccodava più (restavano in pausa).
    /// Da chiamare con _queueLock acquisito.
    /// </summary>
    private void RemovePendingOfCategory(string category)
    {
        foreach (var item in _pending)
            if (item.Category == category) _pendingIds.Remove(item.Id);
        _pending.RemoveAll(i => i.Category == category);
    }

    public void PauseAll(string category)
    {
        List<DownloadItem> toPause;
        lock (_queueLock)
        {
            toPause = _active.Keys
                .Select(id => AllItems.FirstOrDefault(i => i.Id == id))
                .Where(i => i != null && i.Category == category)
                .Cast<DownloadItem>()
                .ToList();

            RemovePendingOfCategory(category);

            foreach (var item in AllItems.Where(i =>
                i.Category == category && i.Status == DownloadStatus.Pending))
            {
                item.Status = DownloadStatus.Paused;
            }
        }

        foreach (var item in toPause)
        {
            item.PauseRequested = true;
            if (_active.TryGetValue(item.Id, out var cts))
                cts.Cancel();
        }

        QueueChanged?.Invoke();
    }

    public void CancelAll(string category)
    {
        List<int> activeIds;
        List<DownloadItem> toCancel;

        lock (_queueLock)
        {
            activeIds = _active.Keys
                .Select(id => AllItems.FirstOrDefault(i => i.Id == id))
                .Where(i => i != null && i.Category == category)
                .Select(i => i!.Id)
                .ToList();

            toCancel = AllItems
                .Where(i => i.Category == category &&
                        (i.Status == DownloadStatus.Pending ||
                            i.Status == DownloadStatus.Paused ||
                            i.Status == DownloadStatus.Failed))
                .ToList();

            RemovePendingOfCategory(category);
        }

        foreach (var item in toCancel)
        {
            item.PauseRequested = false;
            item.Status = DownloadStatus.Cancelled;
        }

        foreach (var id in activeIds)
        {
            if (_active.TryGetValue(id, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }

        QueueChanged?.Invoke();
    }

    public void ClearCompleted(string category)
    {
        var toRemove = AllItems
            .Where(i => i.Category == category && i.Status == DownloadStatus.Completed)
            .ToList();
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in toRemove) AllItems.Remove(item);
        });
        QueueChanged?.Invoke();
    }

    public void ClearCancelled(string category)
    {
        List<DownloadItem> toRemove;
        lock (_queueLock)
        {
            toRemove = AllItems
                .Where(i => i.Category == category && i.Status == DownloadStatus.Cancelled)
                .ToList();
            foreach (var item in toRemove)
            {
                _pending.RemoveAll(p => p.Id == item.Id);
                _pendingIds.Remove(item.Id);
            }
        }
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in toRemove) AllItems.Remove(item);
        });
        QueueChanged?.Invoke();
    }

    public void ClearFailed(string category)
    {
        var toRemove = AllItems
            .Where(i => i.Category == category && i.Status == DownloadStatus.Failed)
            .ToList();
        Dispatcher.UIThread.Post(() =>
        {
            foreach (var item in toRemove) AllItems.Remove(item);
        });
        QueueChanged?.Invoke();
    }

    private async Task DeleteFromDatabaseAsync(IEnumerable<DownloadItem> items)
    {
        var ids = items.Where(i => i.Id > 0).Select(i => i.Id).ToList();
        try { await Db(() => _repository.DeleteManyAsync(ids)); }
        catch (Exception ex) { _logger.LogWarning(ex, "[QUEUE] Eliminazione di {Count} download dal database fallita", ids.Count); }
    }

    /// <summary>Toglie più item da AllItems scorrendo la lista una volta sola (Remove singoli sono O(n) ciascuno).</summary>
    private async Task RemoveFromAllItemsAsync(IEnumerable<DownloadItem> items)
    {
        var set = items.ToHashSet();
        if (set.Count == 0) return;

        void Remove()
        {
            for (int i = AllItems.Count - 1; i >= 0; i--)
                if (set.Contains(AllItems[i])) AllItems.RemoveAt(i);
        }

        if (Dispatcher.UIThread.CheckAccess()) Remove();
        else await Dispatcher.UIThread.InvokeAsync(Remove);
    }

    public async Task DeleteEverythingAsync(string category)
    {
        CancelAll(category);
        await Task.Delay(300);

        List<DownloadItem> snapshot;
        lock (_queueLock)
        {
            snapshot = AllItems.Where(i => i.Category == category).ToList();
            var ids = snapshot.Select(i => i.Id).ToHashSet();
            _pending.RemoveAll(i => i.Category == category);
            _pendingIds.RemoveWhere(ids.Contains);
        }

        await DeleteFromDatabaseAsync(snapshot);
        await RemoveFromAllItemsAsync(snapshot);

        QueueChanged?.Invoke();
    }

    // ============================================================
    //  DISPATCH
    // ============================================================

    private void TryDispatch()
    {
        if (!_dispatchGate.Wait(0)) return;

        Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    DownloadItem? item = null;
                    CancellationTokenSource? cts = null;

                    lock (_queueLock)
                    {
                        if (!_isRunning) return;

                        // Slot liberi per categoria: anime e film/serie non si rubano il posto.
                        var activeByCategory = new Dictionary<string, int>();
                        foreach (var c in _activeCategory.Values)
                            activeByCategory[c] = activeByCategory.GetValueOrDefault(c) + 1;

                        for (int i = 0; i < _pending.Count; i++)
                        {
                            var candidate = _pending[i];

                            if (candidate.Status == DownloadStatus.Cancelled ||
                                candidate.Status == DownloadStatus.Completed ||
                                candidate.Status == DownloadStatus.Paused)
                            {
                                _pending.RemoveAt(i--);
                                _pendingIds.Remove(candidate.Id);
                                continue;
                            }

                            var category = candidate.Category;
                            if (!_runningCategories.Contains(category)) continue;

                            var max = _maxConcurrency.TryGetValue(category, out var m) ? m : DefaultMaxConcurrency;
                            if (activeByCategory.GetValueOrDefault(category) >= max) continue;

                            // Disco quasi pieno: il download non parte e va in pausa con il motivo
                            if (FreeBytesFor(candidate.DestinationPath) is { } free && free < MinFreeBytes)
                            {
                                _pending.RemoveAt(i--);
                                _pendingIds.Remove(candidate.Id);
                                var root = SafeRoot(candidate.DestinationPath);
                                var low = candidate;
                                Dispatcher.UIThread.Post(() =>
                                {
                                    low.ErrorMessage = $"spazio su disco esaurito ({FormatBytes(free)} liberi su {root}). Libera spazio e premi Riprendi";
                                    low.Status = DownloadStatus.Paused;
                                });
                                DiskSpaceLow?.Invoke(root, free);
                                continue;
                            }

                            item = candidate;
                            _pending.RemoveAt(i);
                            _pendingIds.Remove(candidate.Id);
                            break;
                        }

                        if (item == null) return;

                        cts = new CancellationTokenSource();
                        _active[item.Id] = cts;
                        _activeCategory[item.Id] = item.Category;
                    }

                    _ = RunItemAsync(item!, cts!);
                }
            }
            finally
            {
                _dispatchGate.Release();
            }
        });
    }

    private static string SafeRoot(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? ""; } catch { return ""; }
    }

    /// <summary>Spazio libero sul disco del percorso (letto al massimo ogni 5 secondi). null = sconosciuto.</summary>
    private long? FreeBytesFor(string path)
    {
        var root = SafeRoot(path);
        if (root.Length == 0) return null;
        if (_freeCache.TryGetValue(root, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(5)) return c.Free;
        try
        {
            var free = new DriveInfo(root).AvailableFreeSpace;
            _freeCache[root] = (DateTime.UtcNow, free);
            return free;
        }
        catch { return null; }
    }

    public static string FormatBytes(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):F1} GB"
        : $"{bytes / (double)(1L << 20):F0} MB";

    /// <summary>
    /// Errore per cui riprovare ha senso (rete, proxy, server occupato). Non si riprova per file
    /// che non esistono, captcha, disco pieno o permessi.
    /// </summary>
    private static bool IsTransient(Exception ex)
    {
        var m = ex.ToString();
        string[] permanent = { "404", "Not Found", "captcha", "Captcha", "not enough space", "spazio", "disk full", "Access to the path", "UnauthorizedAccess", "Accesso negato" };
        return !permanent.Any(p => m.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Rimette in coda un download fallito dopo l'attesa, se nel frattempo nessuno l'ha toccato.</summary>
    private async Task AutoRetryLaterAsync(DownloadItem item, TimeSpan delay, int attempt)
    {
        await Task.Delay(delay);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            // Messo in pausa, annullato, rimosso o già ripartito: non si fa niente
            if (item.Status != DownloadStatus.Pending || item.AutoRetryCount != attempt || !AllItems.Contains(item)) return;
            item.ErrorMessage = string.Empty;
            lock (_queueLock)
            {
                if (_active.ContainsKey(item.Id) || !_pendingIds.Add(item.Id)) return;
                _pending.Add(item);
            }
            _logger.LogInformation("[QUEUE] Nuovo tentativo {N}/{Max}: {File}", attempt, MaxAutoRetries, item.FileName);
        });
        QueueChanged?.Invoke();
        TryDispatch();
    }

    private async Task RunItemAsync(DownloadItem item, CancellationTokenSource cts)
    {
        bool completed = false;
        bool finalFailure = false;
        try
        {
            QueueChanged?.Invoke();

            var downloadService = _serviceProvider.GetRequiredService<IDownloadService>();
            await downloadService.DownloadItemAsync(item, cts.Token);
            completed = true;
        }
        catch (OperationCanceledException)
        {
            bool preempted;
            lock (_queueLock) preempted = _preempted.Contains(item.Id);
            if (!preempted)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (item.PauseRequested)
                    {
                        item.ErrorMessage = string.Empty;   // es. "Conversione 37%"
                        item.Status = DownloadStatus.Paused;
                    }
                    else
                        item.Status = DownloadStatus.Cancelled;
                });
            }
        }
        catch (Exception ex)
        {
            if (item.AutoRetryCount < MaxAutoRetries && IsTransient(ex))
            {
                // Errore temporaneo: si riprova da solo dopo un'attesa crescente (30 s, 2 min, 5 min)
                int attempt = ++item.AutoRetryCount;
                var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length) - 1];
                var at = DateTime.Now + delay;
                Dispatcher.UIThread.Post(() =>
                {
                    item.SpeedBytesPerSecond = 0;
                    item.ErrorMessage = $"Nuovo tentativo alle {at:HH:mm:ss} ({attempt}/{MaxAutoRetries}) · {ex.Message}";
                    item.Status = DownloadStatus.Pending;
                });
                _logger.LogWarning("Download fallito, nuovo tentativo {N}/{Max} tra {Sec} s: {File} — {Err}",
                    attempt, MaxAutoRetries, (int)delay.TotalSeconds, item.FileName, ex.Message);
                _ = AutoRetryLaterAsync(item, delay, attempt);
            }
            else
            {
                finalFailure = true;
                Dispatcher.UIThread.Post(() =>
                {
                    item.Status = DownloadStatus.Failed;
                    item.ErrorMessage = item.AutoRetryCount > 0
                        ? $"{ex.Message} (dopo {item.AutoRetryCount} {(item.AutoRetryCount == 1 ? "nuovo tentativo" : "nuovi tentativi")})"
                        : ex.Message;
                });
                _logger.LogError(ex, "Download fallito: {Url}", item.Url);
            }
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                item.PauseRequested = false;
                item.IsPriority = false;
            });

            bool requeue;
            lock (_queueLock)
            {
                if (_active.TryGetValue(item.Id, out var currentCts) && currentCts == cts)
                {
                    _active.Remove(item.Id);
                    _activeCategory.Remove(item.Id);
                }
                requeue = _preempted.Remove(item.Id);
            }

            if (completed)
            {
                item.AutoRetryCount = 0;
                try { await Db(() => _repository.AddHistoryAsync(item)); }
                catch (Exception ex) { _logger.LogWarning(ex, "[QUEUE] Cronologia non aggiornata (Id {Id})", item.Id); }
            }
            if (completed || finalFailure)
                Dispatcher.UIThread.Post(() => ItemFinished?.Invoke(item));

            if (requeue)
            {
                // Fermato per un download forzato: torna in coda subito dietro i forzati e
                // riprende appena si libera uno slot.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (item.Status is DownloadStatus.Cancelled or DownloadStatus.Completed or DownloadStatus.Paused) return;
                    item.Status = DownloadStatus.Pending;
                    item.SpeedBytesPerSecond = 0;
                    lock (_queueLock)
                    {
                        if (_active.ContainsKey(item.Id) || !_pendingIds.Add(item.Id)) return;
                        int at = 0;
                        while (at < _pending.Count && _pending[at].IsPriority) at++;
                        _pending.Insert(at, item);
                    }
                });
            }

            try { await Db(() => _repository.UpdateAsync(item)); }
            catch (Exception ex) { _logger.LogWarning(ex, "[QUEUE] Salvataggio stato fallito (Id {Id})", item.Id); }

            QueueChanged?.Invoke();
            TryDispatch();
        }
    }
}