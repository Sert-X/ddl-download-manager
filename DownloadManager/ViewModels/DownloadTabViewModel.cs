using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Anime;
using DownloadManager.Services.Anime.AnimeWorld;
using DownloadManager.Services.Anime.AnimeSaturn;
using DownloadManager.Services.Download;
using Microsoft.Extensions.Logging;

namespace DownloadManager.ViewModels;

/// <summary>
/// Sub-ViewModel per il tab "Download". Contiene tutta la logica di:
/// ricerca anime, selezione episodi, accodamento, controllo della coda,
/// raggruppamento per serie e statistiche globali del tab.
/// </summary>
public partial class DownloadTabViewModel : ViewModelBase
{
    private readonly IAnimeWorldService _animeWorldService;
    private readonly IAnimeSaturnService _animeSaturnService;
    private readonly AnimeProviderRegistry _registry;
    private readonly DownloadQueueService _queueService;
    private readonly FileNameBuilder _fileNameBuilder;
    private readonly SettingsService _settings;
    private readonly SharedState _shared;
    private readonly ILogger<DownloadTabViewModel> _logger;    private Episode? _currentEpisode;
    private CancellationTokenSource? _batchCts;
    private readonly DispatcherTimer _statsTimer;
    private readonly HashSet<SeriesGroup> _activeGroupsLastTick = new();

    // ============================================================
    //  PROPERTY RICERCA
    // ============================================================

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _statusMessage = "Pronto. Premi 'Cerca' per iniziare.";
    [ObservableProperty] private ObservableCollection<AnimeSearchResult> _searchResults = new();
    [ObservableProperty] private AnimeSearchResult? _selectedAnime;
    [ObservableProperty] private ObservableCollection<Episode> _episodes = new();
    [ObservableProperty] private bool _isLoadingSearchResults;
    [ObservableProperty] private bool _isLoadingEpisodes;
    public string SearchResultsCountText =>
        SearchResults.Count == 0 ? "" : $"({SearchResults.Count} serie)";
    public string EpisodesCountText =>
        Episodes.Count == 0 ? "" : $"{Episodes.Count} episodi";

    public string EpisodesSelectedCountText =>
        Episodes.Count == 0 ? "" : $"{Episodes.Count(e => e.IsSelected)} selezionati";

    // ============================================================
    //  Selezione provider
    // ============================================================

    [ObservableProperty] private IAnimeProvider? _selectedProvider;

    public ObservableCollection<IAnimeProvider> AvailableProviders { get; } = new();

    // ============================================================
    //  PROPERTY DOWNLOAD
    // ============================================================

    [ObservableProperty] private int _maxConcurrency = 3;
    [ObservableProperty] private string _namingPattern = "{OriginalFileName}";
    [ObservableProperty] private bool _isBatchActive;

    // ============================================================
    //  COLLEZIONI
    // ============================================================

    public ObservableCollection<string> SearchHistory { get; } = new();
    public ObservableCollection<SeriesGroup> SeriesGroups { get; } = new();
    public ObservableCollection<DownloadItem> Downloads => _queueService.AllItems;

    public string[] NamingPresets { get; } = new[]
    {
        "{OriginalFileName}", "{Episode}", "{Episode:D2}", "{Episode:D3}",
        "{Series} - {Episode:D3}", "{Series} - {Episode:D3} - {Title}"
    };

    // ============================================================
    //  WRAPPER SharedState
    // ============================================================

    public string BaseDownloadFolder
    {
        get => _shared.BaseDownloadFolder;
        set => _shared.BaseDownloadFolder = value;
    }

    public bool CreateSeriesFolder
    {
        get => _shared.CreateSeriesFolder;
        set => _shared.CreateSeriesFolder = value;
    }

    // ============================================================
    //  COMPUTED
    // ============================================================

    public string PatternPreview
    {
        get
        {
            try
            {
                var preview = FileNameBuilder.ApplyPattern(
                    NamingPattern, "Naruto Shippuden", 1, "Episodio 1", "NarutoShippuden_Ep_001_SUB_ITA");
                return preview + ".mp4";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string GlobalEtaText
    {
        get
        {
            double totalSpeed = _queueService.AllItems.Sum(i => i.SpeedBytesPerSecond);
            if (totalSpeed <= 0) return "-";

            long totalBytes = _queueService.AllItems.Sum(i => i.TotalBytes);
            long downloaded = _queueService.AllItems.Sum(i => i.DownloadedBytes);
            long remaining = totalBytes - downloaded;

            if (remaining <= 0) return "0s";

            var seconds = remaining / totalSpeed;
            if (seconds < 1) return "<1s";
            if (seconds < 60) return $"{(int)seconds}s";
            if (seconds < 3600) return $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s";
            return $"{(int)(seconds / 3600)}h {(int)((seconds % 3600) / 60)}m";
        }
    }

    public string GlobalSpeedText
    {
        get
        {
            double speed = _queueService.AllItems.Sum(i => i.SpeedBytesPerSecond);
            return speed > 0 ? $"{speed / 1024 / 1024:F2} MB/s" : "-";
        }
    }

    public string GlobalSizeText
    {
        get
        {
            long total = _queueService.AllItems.Sum(i => i.TotalBytes);
            long done = _queueService.AllItems.Sum(i => i.DownloadedBytes);
            return total > 0 ? $"{done / 1024.0 / 1024 / 1024:F2} / {total / 1024.0 / 1024 / 1024:F2} GB" : "-";
        }
    }

    public string SeriesSummaryText
    {
        get
        {
            var groups = SeriesGroups;
            if (groups.Count == 0) return "Nessuna serie in coda.";

            int totalEpisodes = groups.Sum(g => g.Items.Count);
            int completed = groups.Sum(g => g.Items.Count(i => i.Status == DownloadStatus.Completed));
            int downloading = groups.Sum(g => g.Items.Count(i => i.Status == DownloadStatus.Downloading));
            int pending = groups.Sum(g => g.Items.Count(i => i.Status == DownloadStatus.Pending || i.Status == DownloadStatus.Paused));
            int failed = groups.Sum(g => g.Items.Count(i => i.Status == DownloadStatus.Failed));
            int cancelled = groups.Sum(g => g.Items.Count(i => i.Status == DownloadStatus.Cancelled));

            var parts = new List<string> { $"{totalEpisodes} episodi" };
            if (completed > 0) parts.Add($"{completed} completati");
            if (downloading > 0) parts.Add($"{downloading} in corso");
            if (pending > 0) parts.Add($"{pending} in attesa");
            if (failed > 0) parts.Add($"{failed} falliti");
            if (cancelled > 0) parts.Add($"{cancelled} annullati");

            return $"{groups.Count} serie · {string.Join(" · ", parts)}";
        }
    }

    // ============================================================
    //  COSTRUTTORE
    // ============================================================

    public DownloadTabViewModel(
        IAnimeWorldService animeWorldService,
        IAnimeSaturnService animeSaturnService,
        AnimeProviderRegistry registry,
        DownloadQueueService queueService,
        FileNameBuilder fileNameBuilder,
        SettingsService settings,
        ILogger<DownloadTabViewModel> logger,
        SharedState shared)
    {
        _animeWorldService = animeWorldService;
        _animeSaturnService = animeSaturnService;
        _registry = registry;
        _queueService = queueService;
        _fileNameBuilder = fileNameBuilder;
        _settings = settings;
        _logger = logger;
        _shared = shared;

        foreach (var p in registry.Names.Select(n => registry.Get(n)!))
        AvailableProviders.Add(p);

        SelectedProvider = AvailableProviders.FirstOrDefault()!;

        MaxConcurrency = _settings.Current.MaxConcurrency;
        _queueService.MaxConcurrency = MaxConcurrency;

        NamingPattern = _settings.Current.NamingPattern;

        foreach (var term in _settings.Current.SearchHistory)
            SearchHistory.Add(term);

        _queueService.AllItems.CollectionChanged += OnAllItemsChanged;

        SearchResults.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SearchResultsCountText));
        };

        // Hook per contatore episodi: cambia quando la lista cambia
        Episodes.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(EpisodesCountText));
            OnPropertyChanged(nameof(EpisodesSelectedCountText));

            // Ascolta il cambio IsSelected di ogni episodio
            if (e.NewItems != null)
                foreach (Episode ep in e.NewItems)
                    ep.PropertyChanged += OnEpisodePropertyChanged;

            if (e.OldItems != null)
                foreach (Episode ep in e.OldItems)
                    ep.PropertyChanged -= OnEpisodePropertyChanged;
        };

        foreach (var item in _queueService.AllItems)
            AddToGroup(item);

        // Hook Shared → notifica binding dei wrapper
        _shared.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SharedState.BaseDownloadFolder):
                    OnPropertyChanged(nameof(BaseDownloadFolder));
                    break;
                case nameof(SharedState.CreateSeriesFolder):
                    OnPropertyChanged(nameof(CreateSeriesFolder));
                    break;
            }
        };

        _statsTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1000)
        };
        _statsTimer.Tick += (s, e) =>
        {
            var activeNow = new HashSet<SeriesGroup>();

            foreach (var g in SeriesGroups)
            {
                bool isActive = g.Items.Any(i =>
                    i.Status == DownloadStatus.Downloading ||
                    i.Status == DownloadStatus.Pending ||
                    i.Status == DownloadStatus.Paused);

                if (isActive || _activeGroupsLastTick.Contains(g))
                    g.RefreshStats();

                if (isActive) activeNow.Add(g);
            }

            _activeGroupsLastTick.Clear();
            foreach (var g in activeNow) _activeGroupsLastTick.Add(g);

            OnPropertyChanged(nameof(GlobalEtaText));
            OnPropertyChanged(nameof(GlobalSpeedText));
            OnPropertyChanged(nameof(GlobalSizeText));
            OnPropertyChanged(nameof(SeriesSummaryText));
        };
        _statsTimer.Start();

        // Notifica all'avvio ritardato: ripresa coda
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var restored = _queueService.AllItems.Count(i =>
                    i.Status == DownloadStatus.Pending || i.Status == DownloadStatus.Paused);

                if (restored > 0)
                {
                    StatusMessage = $"Ripristinati {restored} download in coda dalla sessione precedente. Premi ▶ Avvia tutti per riprenderli.";
                }
            });
        });
    }

    // ============================================================
    //  SELEZIONE PROVIDER
    // ============================================================

    partial void OnSelectedProviderChanged(IAnimeProvider? value)
    {
        if (value == null) return;

        // Notifica UI (il ComboBox si aggiorna automaticamente)
        OnPropertyChanged(nameof(SelectedProviderName));

        // Pulisce i risultati correnti quando cambi provider
        SearchResults.Clear();
        Episodes.Clear();
        SelectedAnime = null;
        StatusMessage = $"Provider: {value.Name}. Premi 'Cerca' per iniziare.";
    }

    public string SelectedProviderName => SelectedProvider?.Name ?? "—";

    // ============================================================
    //  PROPERTY CHANGED
    // ============================================================

    partial void OnMaxConcurrencyChanged(int value)
    {
        if (_queueService != null) _queueService.MaxConcurrency = value;
        if (_settings != null) { _settings.Current.MaxConcurrency = value; _settings.Save(); }
    }

    partial void OnNamingPatternChanged(string value) => OnPropertyChanged(nameof(PatternPreview));

    // ============================================================
    //  RICERCA
    // ============================================================

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            StatusMessage = "Inserisci un termine di ricerca.";
            return;
        }
        await SearchWithTermAsync(SearchQuery);
    }

    public async Task SearchWithTermAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(term)) { StatusMessage = "Inserisci un termine."; return; }

        SearchQuery = term;
        SaveSearchToHistory(term.Trim());

        IsLoadingSearchResults = true;
        try
        {
            StatusMessage = "Inizializzazione browser...";
            await SelectedProvider!.InitializeAsync(headless: true);

            StatusMessage = $"Ricerca in corso per '{term}'...";
            var results = await SelectedProvider!.SearchAsync(term);

            SearchResults.Clear();
            foreach (var result in results) SearchResults.Add(result);

            StatusMessage = $"Trovati {results.Count} risultati per '{term}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message.Contains("HTTP ")
                ? $"⚠ {ex.Message}"
                : $"Errore: {ex.Message}";
            _logger.LogWarning(ex, "[DOWNLOAD-TAB] Errore ricerca");
        }
        finally
        {
            IsLoadingSearchResults = false;
        }
    }

    [RelayCommand]
    private void ClearSearchHistory()
    {
        SearchHistory.Clear();
        _settings.Current.SearchHistory = new List<string>();
        _settings.Save();
        StatusMessage = "Cronologia ricerche svuotata.";
    }

    public void SetSearchQueryAndSearch(string query) => _ = SearchWithTermAsync(query);

    private void SaveSearchToHistory(string term)
    {
        if (string.IsNullOrEmpty(term)) return;

        var existing = SearchHistory.FirstOrDefault(h =>
            string.Equals(h, term, StringComparison.OrdinalIgnoreCase));
        if (existing != null) SearchHistory.Remove(existing);

        SearchHistory.Insert(0, term);

        while (SearchHistory.Count > 20)
            SearchHistory.RemoveAt(SearchHistory.Count - 1);

        _settings.Current.SearchHistory = SearchHistory.ToList();
        _settings.Save();
    }
    private void OnEpisodePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Episode.IsSelected))
            OnPropertyChanged(nameof(EpisodesSelectedCountText));
    }

    [RelayCommand]
    private async Task LoadEpisodesAsync(AnimeSearchResult? anime)
    {
        if (anime == null) return;

        IsLoadingEpisodes = true;
        try
        {
            SelectedAnime = anime;
            StatusMessage = $"Caricamento episodi di '{anime.Name}'...";

            var episodes = await SelectedProvider!.GetEpisodesAsync(anime.Link);

            Episodes.Clear();
            foreach (var ep in episodes) Episodes.Add(ep);

            StatusMessage = $"Trovati {episodes.Count} episodi per '{anime.Name}'.";
        }
        catch (Exception ex) { StatusMessage = $"Errore: {ex.Message}"; }
        finally
        {
            IsLoadingEpisodes = false;
        }
    }

    // ============================================================
    //  DOWNLOAD ANIME
    // ============================================================

    [RelayCommand]
    private async Task DownloadEpisodeAsync(Episode? episode)
    {
        if (episode == null) return;
        await DownloadEpisodeInternalAsync(episode);
    }

    private async Task DownloadEpisodeInternalAsync(Episode episode, CancellationToken ct = default)
    {
        _currentEpisode = episode;

        ct.ThrowIfCancellationRequested();
        var videoUrl = await SelectedProvider!.GetVideoUrlAsync(episode.Link);

        if (string.IsNullOrEmpty(videoUrl)) return;
        ct.ThrowIfCancellationRequested();

        var item = BuildDownloadItem(episode, videoUrl, SelectedAnime?.Name ?? "");
        await _queueService.EnqueueAsync(item);
    }

    private DownloadItem BuildDownloadItem(Episode episode, string videoUrl, string seriesName)
    {
        var episodeNumber = int.TryParse(episode.Number, out var n) ? n : 0;

        _settings.Current.NamingPattern = NamingPattern;
        _settings.Current.BaseDownloadFolder = _shared.BaseDownloadFolder;
        _settings.Current.CreateSeriesFolder = _shared.CreateSeriesFolder;

        var item = new DownloadItem
        {
            Url = videoUrl,
            SeriesName = seriesName,
            EpisodeNumber = episodeNumber,
            EpisodeTitle = episode.Title,
            Status = DownloadStatus.Pending
        };

        item.DestinationPath = _fileNameBuilder.BuildDestinationPath(item, videoUrl, episode.Title);
        return item;
    }

    [RelayCommand]
    private async Task DownloadSelectedEpisodesAsync()
    {
        var selected = Episodes.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0) { StatusMessage = "Nessun episodio selezionato."; return; }
        if (IsBatchActive) { StatusMessage = "Un accodamento è già in corso."; return; }

        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;
        IsBatchActive = true;

        int queued = 0, skipped = 0, errors = 0;

        try
        {
            var seriesName = SelectedAnime?.Name ?? "";
            var toDownload = new List<Episode>();

            foreach (var ep in selected)
            {
                var epNum = int.TryParse(ep.Number, out var n) ? n : 0;
                if (_queueService.ContainsEpisode(seriesName, epNum)) { skipped++; continue; }
                toDownload.Add(ep);
            }

            if (toDownload.Count == 0)
            {
                StatusMessage = $"Tutti i {skipped} episodi selezionati sono già in coda.";
                return;
            }

            StatusMessage = $"Recupero URL di {toDownload.Count} episodi...";
            var urls = await SelectedProvider!.GetVideoUrlsBatchAsync(toDownload, token);

            foreach (var episode in toDownload)
            {
                token.ThrowIfCancellationRequested();

                if (!urls.TryGetValue(episode.Link, out var videoUrl) || string.IsNullOrEmpty(videoUrl))
                { errors++; continue; }

                var item = BuildDownloadItem(episode, videoUrl, seriesName);
                await _queueService.EnqueueAsync(item);
                queued++;
                StatusMessage = $"Accodati: {queued}/{toDownload.Count}";
            }

            var msg = $"{queued} episodi accodati";
            if (skipped > 0) msg += $", {skipped} già in coda";
            if (errors > 0) msg += $", {errors} falliti";
            StatusMessage = msg + ".";
        }
        catch (OperationCanceledException) { StatusMessage = $"Accodamento annullato ({queued})."; }
        catch (Exception ex) { StatusMessage = $"Errore: {ex.Message}"; }
        finally
        {
            _batchCts?.Dispose();
            _batchCts = null;
            IsBatchActive = false;
        }
    }

    [RelayCommand]
    private void CancelBatch() { _batchCts?.Cancel(); StatusMessage = "Annullamento accodamento..."; }

    [RelayCommand]
    private void SelectAllEpisodes() { foreach (var ep in Episodes) ep.IsSelected = true; StatusMessage = $"Selezionati {Episodes.Count} episodi."; }

    [RelayCommand]
    private void DeselectAllEpisodes() { foreach (var ep in Episodes) ep.IsSelected = false; StatusMessage = "Deselezionati."; }

    [RelayCommand]
    private void InvertEpisodeSelection() { foreach (var ep in Episodes) ep.IsSelected = !ep.IsSelected; StatusMessage = "Selezione invertita."; }

    // ============================================================
    //  CONTROLLI GLOBALI CODA
    // ============================================================

    [RelayCommand]
    private void StartAll()
    {
        var count = _queueService.AllItems.Count(i =>
            i.Status == DownloadStatus.Pending || i.Status == DownloadStatus.Paused || i.Status == DownloadStatus.Failed);

        if (count == 0) { StatusMessage = "Nessun download da avviare."; return; }
        _queueService.StartAll();
        StatusMessage = $"Avvio di {count} download.";
    }

    [RelayCommand]
    private void PauseAll()
    {
        _batchCts?.Cancel();
        var active = _queueService.ActiveCount;
        var pending = _queueService.PendingCount;
        if (active == 0 && pending == 0) { StatusMessage = "Nessun download in corso o in attesa."; return; }
        _queueService.PauseAll();
        StatusMessage = $"In pausa {active} download attivi.";
    }

    [RelayCommand]
    private void CancelAllDownloads()
    {
        var cancellable = _queueService.AllItems.Count(i =>
            i.Status == DownloadStatus.Downloading ||
            i.Status == DownloadStatus.Pending ||
            i.Status == DownloadStatus.Paused ||
            i.Status == DownloadStatus.Failed);

        if (cancellable == 0) { StatusMessage = "Nessun download da annullare."; return; }
        _queueService.CancelAll();
        StatusMessage = $"Annullati {cancellable} download.";
    }

    [RelayCommand]
    private async Task DeleteEverythingAsync()
    {
        var count = _queueService.AllItems.Count;
        if (count == 0) { StatusMessage = "La lista è già vuota."; return; }
        await _queueService.DeleteEverythingAsync();
        StatusMessage = $"Eliminati {count} download.";
    }

    [RelayCommand]
    private async Task DeleteAllAsync()
    {
        _batchCts?.Cancel();
        await Task.Delay(300);
        var count = _queueService.AllItems.Count;
        if (count == 0) { StatusMessage = "La lista è già vuota."; return; }
        await _queueService.DeleteAllAsync();
        StatusMessage = $"Rimossi {count} download.";
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        var completed = _queueService.AllItems.Count(d => d.Status == DownloadStatus.Completed);
        if (completed == 0) { StatusMessage = "Nessun completato."; return; }
        _queueService.ClearCompleted();
        StatusMessage = $"Rimossi {completed} completati.";
    }

    [RelayCommand]
    private void ClearCancelledDownloads()
    {
        var cancelled = _queueService.AllItems.Count(d => d.Status == DownloadStatus.Cancelled);
        if (cancelled == 0) { StatusMessage = "Nessun annullato."; return; }
        _queueService.ClearCancelled();
        StatusMessage = $"Rimossi {cancelled} annullati.";
    }

    [RelayCommand]
    private void ClearFailedDownloads()
    {
        var failed = _queueService.AllItems.Count(d => d.Status == DownloadStatus.Failed);
        if (failed == 0) { StatusMessage = "Nessun failed."; return; }
        _queueService.ClearFailed();
        StatusMessage = $"Rimossi {failed} failed.";
    }

    // ============================================================
    //  CONTROLLI ITEM / GRUPPO (chiamati dal code-behind)
    // ============================================================

    public void PauseItemPublic(DownloadItem item) => _queueService.PauseItem(item);
    public void ResumeItemPublic(DownloadItem item) => _queueService.ResumeItem(item);
    public void ForceItemPublic(DownloadItem item) => _queueService.ForceQueueItem(item);
    public void UnforceItemPublic(DownloadItem item) => _queueService.UnforceQueueItem(item);
    public void CancelItemPublic(DownloadItem item) => _queueService.CancelItem(item);
    public Task RemoveItemPublicAsync(DownloadItem item) => _queueService.RemoveItemAsync(item);

    public void PauseGroupPublic(SeriesGroup group) { foreach (var item in group.Items.ToList()) _queueService.PauseItem(item); }
    public void ResumeGroupPublic(SeriesGroup group) { foreach (var item in group.Items.ToList()) _queueService.ResumeItem(item); }
    public void ForceGroupPublic(SeriesGroup group) => _queueService.ForceQueueGroup(group);
    public void UnforceGroupPublic(SeriesGroup group) => _queueService.UnforceQueueGroup(group);

    public void CancelGroupPublic(SeriesGroup group)
    {
        _queueService.CancelGroup(group);
        StatusMessage = $"Annullata la serie: {group.SeriesName}";
    }

    public async Task RemoveGroupPublicAsync(SeriesGroup group)
    {
        foreach (var item in group.Items.ToList())
            await _queueService.RemoveItemAsync(item);
    }

    // ============================================================
    //  IMPOSTAZIONI (dal tab Download)
    // ============================================================

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.Current.NamingPattern = NamingPattern;
        _settings.Current.CreateSeriesFolder = _shared.CreateSeriesFolder;
        _settings.Save();
        StatusMessage = "Impostazioni salvate.";
    }

    public void SetBaseDownloadFolder(string folder)
    {
        _shared.BaseDownloadFolder = folder;
        _settings.Current.BaseDownloadFolder = folder;
        _settings.Save();
    }

    // ============================================================
    //  RAGGRUPPAMENTO SERIE
    // ============================================================

    private void OnAllItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            SeriesGroups.Clear();
            return;
        }

        if (e.NewItems != null)
            foreach (DownloadItem item in e.NewItems)
                AddToGroup(item);

        if (e.OldItems != null)
            foreach (DownloadItem item in e.OldItems)
                RemoveFromGroup(item);
    }

    private void AddToGroup(DownloadItem item)
    {
        var seriesName = string.IsNullOrWhiteSpace(item.SeriesName) ? "Senza serie" : item.SeriesName;

        var group = SeriesGroups.FirstOrDefault(g => g.SeriesName == seriesName);
        if (group == null)
        {
            group = new SeriesGroup { SeriesName = seriesName, IsExpanded = false };
            SeriesGroups.Add(group);
        }

        if (!group.Items.Contains(item))
            group.Items.Add(item);
        OnPropertyChanged(nameof(SeriesSummaryText));
    }

    private void RemoveFromGroup(DownloadItem item)
    {
        var seriesName = string.IsNullOrWhiteSpace(item.SeriesName) ? "Senza serie" : item.SeriesName;

        var group = SeriesGroups.FirstOrDefault(g => g.SeriesName == seriesName);
        if (group != null)
        {
            group.Items.Remove(item);
            if (group.Items.Count == 0)
                SeriesGroups.Remove(group);
        }
        OnPropertyChanged(nameof(SeriesSummaryText));
    }
}