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
using System.IO;
using System.Net.Http;
using Avalonia.Media.Imaging;

namespace DownloadManager.ViewModels;

/// <summary>
/// Sub-ViewModel per il tab "Download". Contiene tutta la logica di:
/// ricerca anime, selezione episodi, accodamento, controllo della coda,
/// raggruppamento per serie e statistiche globali del tab.
/// </summary>
public partial class DownloadTabViewModel : ViewModelBase
{
    private readonly DownloadQueueService _queueService;
    private readonly FileNameBuilder _fileNameBuilder;
    private readonly SettingsService _settings;
    private readonly SharedState _shared;
    private readonly ILogger<DownloadTabViewModel> _logger;
    private CancellationTokenSource? _batchCts;
    private readonly DispatcherTimer _statsTimer;
    private readonly HashSet<SeriesGroup> _activeGroupsLastTick = new();

    // ============================================================
    //  PROPERTY RICERCA
    // ============================================================

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _statusMessage = "Cerca un anime per iniziare.";
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

    /// <summary>
    /// Nome della serie/anime attualmente selezionato (usato nell'header
    /// del pannello Episodi). Stringa vuota se nessuna serie è selezionata.
    /// </summary>
    public string SelectedAnimeName => SelectedAnime?.Name ?? string.Empty;

    public bool HasSelectedAnime => SelectedAnime != null;

    // === Riquadro dettaglio dell'anime ===
    [ObservableProperty] private string _detailFolder = "";
    [ObservableProperty] private string _detailFileName = "";
    public string EpisodesCountPill => Episodes.Count == 0 ? "" : $"{Episodes.Count} episodi";
    public bool HasEpisodesCountPill => Episodes.Count > 0;
    public string SelectedCountPill => Episodes.Count(e => e.IsSelected) is var n and > 0 ? $"{n} selezionati" : "";
    public bool HasSelectedCountPill => SelectedCountPill.Length > 0;
    public string AnimeDetailHint => IsLoadingEpisodes
        ? "Caricamento degli episodi..."
        : Episodes.Count == 0
            ? "Fai doppio clic sull'anime a sinistra per caricare gli episodi."
            : "Spunta gli episodi (Ctrl/Maiusc per selezioni multiple) e premi \"Scarica selezionati\".";

    private void UpdateAnimeDetail()
    {
        OnPropertyChanged(nameof(EpisodesCountPill));
        OnPropertyChanged(nameof(HasEpisodesCountPill));
        OnPropertyChanged(nameof(SelectedCountPill));
        OnPropertyChanged(nameof(HasSelectedCountPill));
        OnPropertyChanged(nameof(AnimeDetailHint));

        var a = SelectedAnime;
        if (a == null) { DetailFolder = ""; DetailFileName = ""; return; }
        try
        {
            var first = Episodes.FirstOrDefault();
            var probe = new DownloadItem { SeriesName = a.Name, EpisodeNumber = int.TryParse(first?.Number, out var n0) ? n0 : 1, EpisodeTitle = first?.Title ?? "Episodio 1" };
            var path = _fileNameBuilder.BuildDestinationPath(probe, "", probe.EpisodeTitle);
            DetailFolder = Path.GetDirectoryName(path) ?? "";
            DetailFileName = NamingPattern.Contains("{OriginalFileName}")
                ? "nome originale del file sul sito (noto al via)"
                : Path.GetFileName(path);
        }
        catch { DetailFolder = ""; DetailFileName = ""; }
    }

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
    private readonly ObservableCollection<DownloadItem> _downloads = new();
    public ObservableCollection<DownloadItem> Downloads => _downloads;

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
        set
        {
            if (_shared.CreateSeriesFolder == value) return;
            _shared.CreateSeriesFolder = value;
            _settings.Current.CreateSeriesFolder = value;
            _settings.Save();
            OnPropertyChanged();
        }
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
                    NamingPattern, "Frieren", 1, "Episodio 1", "Frieren_Ep_001_SUB_ITA");
                return preview + ".mp4";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string GlobalEtaText
    {
        get
        {
            double totalSpeed = _queueService.AllItems
                .Where(i => i.Category == "anime")
                .Sum(i => i.SpeedBytesPerSecond);
            if (totalSpeed <= 0) return "-";

            long totalBytes = _queueService.AllItems
                .Where(i => i.Category == "anime")
                .Sum(i => i.TotalBytes);
            long downloaded = _queueService.AllItems
                .Where(i => i.Category == "anime")
                .Sum(i => i.DownloadedBytes);
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
            double speed = _queueService.AllItems
                .Where(i => i.Category == "anime")
                .Sum(i => i.SpeedBytesPerSecond);
            return speed > 0 ? $"{speed / 1024 / 1024:F2} MB/s" : "-";
        }
    }

    public string GlobalSizeText
    {
        get
        {
            long total = _queueService.AllItems
                .Where(i => i.Category == "anime")
                .Sum(i => i.TotalBytes);
            long done = _queueService.AllItems
                .Where(i => i.Category == "anime")
                .Sum(i => i.DownloadedBytes);
            return total > 0
                ? $"{done / 1024.0 / 1024 / 1024:F2} / {total / 1024.0 / 1024 / 1024:F2} GB"
                : "-";
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

    /// <summary>
    /// True se l'utente può interagire con i pulsanti degli episodi
    /// (ci sono episodi caricati e non è in corso un caricamento).
    /// </summary>
    public bool CanInteractWithEpisodes => Episodes.Count > 0 && !IsLoadingEpisodes;

   /// <summary>True se ci sono download anime FALLITI.</summary>
    public bool HasFailedAnimeItems =>
        _queueService.AllItems.Any(i =>
            i.Category == "anime" &&
            i.Status == DownloadStatus.Failed);

    /// <summary>True se ci sono download anime ATTIVI (downloading/pending/paused).</summary>
    public bool HasActiveAnimeItems =>
        _queueService.AllItems.Any(i =>
            i.Category == "anime" &&
            (i.Status == DownloadStatus.Downloading ||
            i.Status == DownloadStatus.Pending ||
            i.Status == DownloadStatus.Paused));

    /// <summary>Pallino rosso.</summary>
    public bool ShowAnimeRed => HasFailedAnimeItems;

    /// <summary>Pallino giallo (attivo, ma nessun fallito).</summary>
    public bool ShowAnimeYellow => HasActiveAnimeItems && !HasFailedAnimeItems;

    /// <summary>Pallino verde (niente di attivo, niente di fallito).</summary>
    public bool ShowAnimeGreen => !HasActiveAnimeItems && !HasFailedAnimeItems;

    // ============================================================
    //  COSTRUTTORE
    // ============================================================

    public DownloadTabViewModel(
        AnimeProviderRegistry registry,
        DownloadQueueService queueService,
        FileNameBuilder fileNameBuilder,
        SettingsService settings,
        ILogger<DownloadTabViewModel> logger,
        SharedState shared)
    {
        _queueService = queueService;
        _fileNameBuilder = fileNameBuilder;
        _settings = settings;
        _logger = logger;
        _shared = shared;

        foreach (var p in registry.Names.Select(n => registry.Get(n)!))
            AvailableProviders.Add(p);

        SelectedProvider = AvailableProviders.FirstOrDefault()!;

        MaxConcurrency = _settings.Current.MaxConcurrency;
        _queueService.SetMaxConcurrency("anime", MaxConcurrency);

        NamingPattern = _settings.Current.NamingPattern;

        foreach (var term in _settings.Current.SearchHistory)
            SearchHistory.Add(term);

        _queueService.AllItems.CollectionChanged += OnAllItemsChanged;

        // Popola la lista filtrata per "anime"
        foreach (var item in _queueService.AllItems.Where(i => i.Category == "anime"))
            _downloads.Add(item);

        // Tieni sincronizzata la lista filtrata con la coda globale
        _queueService.AllItems.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (DownloadItem it in e.NewItems)
                    if (it.Category == "anime" && !_downloads.Contains(it))
                        _downloads.Add(it);

            if (e.OldItems != null)
                foreach (DownloadItem it in e.OldItems)
                    _downloads.Remove(it);

            if (e.Action == NotifyCollectionChangedAction.Reset)
                _downloads.Clear();
        };

        SearchResults.CollectionChanged += (_, _) =>
        {
            NotifyPlaceholders();
            OnPropertyChanged(nameof(SearchResultsCountText));
        };

        // Hook per contatore episodi: cambia quando la lista cambia
        Episodes.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(CanInteractWithEpisodes));
            OnPropertyChanged(nameof(EpisodesCountText));
            OnPropertyChanged(nameof(EpisodesSelectedCountText));
            if (Episodes.Count <= 1 || Episodes.Count % 25 == 0 || e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) UpdateAnimeDetail();

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
                    UpdateAnimeDetail();
                    break;
                case nameof(SharedState.CreateSeriesFolder):
                    OnPropertyChanged(nameof(CreateSeriesFolder));
                    UpdateAnimeDetail();
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
            OnPropertyChanged(nameof(HasFailedAnimeItems));
            OnPropertyChanged(nameof(HasActiveAnimeItems));
            OnPropertyChanged(nameof(ShowAnimeRed));
            OnPropertyChanged(nameof(ShowAnimeYellow));
            OnPropertyChanged(nameof(ShowAnimeGreen));
        };
        _statsTimer.Start();

        // Notifica all'avvio ritardato: ripresa coda
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var restored = _queueService.AllItems.Count(i =>
                    i.Category == "anime" &&
                    (i.Status == DownloadStatus.Pending || i.Status == DownloadStatus.Paused));

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

        OnPropertyChanged(nameof(SelectedProviderName));

        SearchResults.Clear();
        Episodes.Clear();
        SelectedAnime = null;
        StatusMessage = $"Provider {value.Name} selezionato. Cerca un anime per iniziare.";

        // Avvia il browser del provider in anticipo: la prima ricerca non aspetta l'avvio (0,5-3 s)
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000);
                if (ReferenceEquals(SelectedProvider, value)) await value.InitializeAsync(headless: true);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Preavvio del browser di {Provider} non riuscito", value.Name); }
        });
    }

    public string SelectedProviderName => SelectedProvider?.Name ?? "—";

    /// <summary>
    /// Notifica i binding del nome serie quando cambia la selezione.
    /// </summary>
    partial void OnSelectedAnimeChanged(AnimeSearchResult? value)
    {
        OnPropertyChanged(nameof(SelectedAnimeName));
        OnPropertyChanged(nameof(HasSelectedAnime));
        OnPropertyChanged(nameof(ShowDetailPlaceholder));
        UpdateAnimeDetail();
    }

    // ============================================================
    //  PROPERTY CHANGED
    // ============================================================

    partial void OnMaxConcurrencyChanged(int value)
    {
        _queueService?.SetMaxConcurrency("anime", value);
        if (_settings != null) { _settings.Current.MaxConcurrency = value; _settings.Save(); }
    }

    partial void OnNamingPatternChanged(string value)
    {
        OnPropertyChanged(nameof(PatternPreview));
        _settings.Current.NamingPattern = value;
        UpdateAnimeDetail();
        _settings.Save();
    }

    // ============================================================
    //  RICERCA
    // ============================================================

    private static readonly HttpClient _posterHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap> _posterCache = new();

    private static async Task LoadPosterAsync(AnimeSearchResult result)
    {
        if (string.IsNullOrEmpty(result.PosterUrl)) return;

        if (_posterCache.TryGetValue(result.PosterUrl, out var cached))
        {
            await Dispatcher.UIThread.InvokeAsync(() => result.Poster = cached);
            return;
        }

        try
        {
            var bytes = await _posterHttp.GetByteArrayAsync(result.PosterUrl);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);

            _posterCache[result.PosterUrl] = bmp;

            await Dispatcher.UIThread.InvokeAsync(() => result.Poster = bmp);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[POSTER] {result.PosterUrl}: {ex.Message}");
        }
    }
    partial void OnIsLoadingEpisodesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteractWithEpisodes));
        OnPropertyChanged(nameof(ShowDetailPlaceholder));
        OnPropertyChanged(nameof(AnimeDetailHint));
        if (!value) UpdateAnimeDetail();   // conteggi definitivi a caricamento finito
    }

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

        // Una ricerca nuova annulla quella in corso; i risultati di una ricerca superata
        // vengono ignorati (prima potevano arrivare dopo e sovrascrivere quelli giusti)
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        int seq = ++_searchSeq;
        var provider = SelectedProvider!;

        IsLoadingSearchResults = true;
        try
        {
            StatusMessage = $"Ricerca in corso per '{term}'...";
            var results = await RunWithRecoveryAsync(provider, () => provider.SearchAsync(term, cts.Token), cts.Token);
            if (seq != _searchSeq) return;   // nel frattempo è partita un'altra ricerca

            _emptySearchTerm = results.Count == 0 ? term.Trim() : null;
            SearchResults.Clear();
            foreach (var result in results)
            {
                SearchResults.Add(result);
                _ = LoadPosterAsync(result);
            }
            NotifyPlaceholders();

            StatusMessage = $"Trovati {results.Count} risultati per '{term}'.";
        }
        catch (OperationCanceledException) when (seq != _searchSeq) { /* superata da una ricerca nuova */ }
        catch (Exception ex)
        {
            if (seq != _searchSeq) return;
            StatusMessage = ex.Message.Contains("HTTP ")
                ? $"⚠ {ex.Message}"
                : $"Errore: {ex.Message}";
            _logger.LogWarning(ex, "[DOWNLOAD-TAB] Errore ricerca");
        }
        finally
        {
            if (seq == _searchSeq) IsLoadingSearchResults = false;
        }
    }


    // ============================================================
    //  RIQUADRI VUOTI E "PULISCI"
    // ============================================================

    /// <summary>Ultima ricerca senza risultati (per dire "nessun risultato per ..."). null = nessuna.</summary>
    private string? _emptySearchTerm;

    public bool ShowResultsPlaceholder => SearchResults.Count == 0 && !IsLoadingSearchResults;
    public string ResultsPlaceholderTitle => _emptySearchTerm != null ? "Nessun risultato" : "Cerca un anime";
    public string ResultsPlaceholderText => _emptySearchTerm != null
        ? $"Nessun anime trovato per «{_emptySearchTerm}». Prova con un titolo diverso (anche in inglese o giapponese) oppure con un altro sito dal menu a sinistra della ricerca."
        : "Scrivi il titolo qui sopra e premi Cerca (o Invio): i risultati compaiono qui con la locandina. Doppio clic su un risultato, o «Analizza», per vederne gli episodi.";

    private void NotifyPlaceholders()
    {
        OnPropertyChanged(nameof(ShowResultsPlaceholder));
        OnPropertyChanged(nameof(ResultsPlaceholderTitle));
        OnPropertyChanged(nameof(ResultsPlaceholderText));
        OnPropertyChanged(nameof(ShowDetailPlaceholder));
    }

    partial void OnIsLoadingSearchResultsChanged(bool value) => NotifyPlaceholders();

    /// <summary>
    /// Torna come all'apertura dell'app: risultati, dettaglio e campo di ricerca vuoti. Ricerche
    /// e caricamenti in corso vengono fermati (i loro risultati non ricompaiono); i download in
    /// coda e quelli in corso continuano.
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        // Le operazioni in corso diventano "superate": i loro risultati verranno ignorati
        _searchCts?.Cancel(); _searchSeq++;
        _episodesCts?.Cancel(); _episodesSeq++;
        IsLoadingSearchResults = false;
        IsLoadingEpisodes = false;
        SearchResults.Clear();
        Episodes.Clear();
        SelectedAnime = null;
        _emptySearchTerm = null;
        SearchQuery = string.Empty;
        NotifyPlaceholders();
        StatusMessage = "Cerca un anime per iniziare.";
    }

    /// <summary>Dettaglio vuoto: invito a scegliere un risultato (tabella e pulsanti nascosti).</summary>
    public bool ShowDetailPlaceholder => !HasSelectedAnime && !IsLoadingEpisodes;

    private CancellationTokenSource? _searchCts;
    private int _searchSeq;
    private CancellationTokenSource? _episodesCts;
    private int _episodesSeq;

    /// <summary>
    /// Esegue un'operazione sul sito; se il browser interno era stato chiuso (cambio di
    /// proxy, crash) o il sito ha dato un errore passeggero, lo riapre e riprova una volta.
    /// </summary>
    private async Task<T> RunWithRecoveryAsync<T>(IAnimeProvider provider, Func<Task<T>> operation, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await provider.InitializeAsync(headless: true);
                return await operation();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt == 1 && IsRecoverable(ex))
            {
                _logger.LogInformation("[DOWNLOAD-TAB] {Provider}: riapro il browser e riprovo ({Msg})", provider.Name, ex.Message);
                await Task.Delay(800, ct);
            }
        }
    }

    private static bool IsRecoverable(Exception ex)
    {
        var msg = ex.Message;
        return ex is ObjectDisposedException
            || ex.GetType().Name.Contains("Playwright")
            || msg.Contains("non inizializzato", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("closed", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Target", StringComparison.Ordinal)
            || msg.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("403") || msg.Contains("429");
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
        {
            OnPropertyChanged(nameof(EpisodesSelectedCountText));
            OnPropertyChanged(nameof(SelectedCountPill));
            OnPropertyChanged(nameof(HasSelectedCountPill));
        }
    }

    [RelayCommand]
    private async Task LoadEpisodesAsync(AnimeSearchResult? anime)
    {
        if (anime == null) return;

        _episodesCts?.Cancel();
        var cts = _episodesCts = new CancellationTokenSource();
        int seq = ++_episodesSeq;
        var provider = SelectedProvider!;

        IsLoadingEpisodes = true;
        try
        {
            SelectedAnime = anime;
            StatusMessage = $"Caricamento episodi di '{anime.Name}'...";

            var episodes = await RunWithRecoveryAsync(provider, () => provider.GetEpisodesAsync(anime.Link, cts.Token), cts.Token);
            if (seq != _episodesSeq) return;   // nel frattempo è stato scelto un altro anime

            Episodes.Clear();
            foreach (var ep in episodes) Episodes.Add(ep);

            StatusMessage = $"Trovati {episodes.Count} episodi per '{anime.Name}'.";
        }
        catch (OperationCanceledException) when (seq != _episodesSeq) { }
        catch (Exception ex) { if (seq == _episodesSeq) StatusMessage = $"Errore: {ex.Message}"; }
        finally
        {
            if (seq == _episodesSeq) IsLoadingEpisodes = false;
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
        ct.ThrowIfCancellationRequested();
        var item = BuildLazyDownloadItem(episode, SelectedAnime?.Name ?? "", SelectedProvider!);
        await _queueService.EnqueueAsync(item);
        StatusMessage = $"Episodio {episode.Number} accodato.";
    }

    /// <summary>
    /// Episodio in coda senza link: lo chiede al sito il download stesso quando parte
    /// (AnimeUrlResolver: riapre il browser se serve, rallenta e riprova se il sito rifiuta).
    /// Il nome definitivo del file si calcola quando il link è noto.
    /// </summary>
    private DownloadItem BuildLazyDownloadItem(Episode episode, string seriesName, IAnimeProvider provider)
    {
        var episodeNumber = int.TryParse(episode.Number, out var n) ? n : 0;

        _settings.Current.NamingPattern = NamingPattern;
        _settings.Current.BaseDownloadFolder = _shared.BaseDownloadFolder;
        _settings.Current.CreateSeriesFolder = _shared.CreateSeriesFolder;

        var item = new DownloadItem
        {
            Url = "",
            SeriesName = seriesName,
            EpisodeNumber = episodeNumber,
            EpisodeTitle = episode.Title,
            Status = DownloadStatus.Pending,
            Category = "anime",
            ProviderKey = provider.Name,
            ContentLink = episode.Link,
            UrlExtractor = AnimeUrlResolver.CreateExtractor(provider, episode.Link, episode.Number, _logger)
        };

        // Nome provvisorio (definitivo se il pattern non usa il nome originale del file)
        item.DestinationPath = _fileNameBuilder.BuildDestinationPath(item, "", episode.Title);
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

        int skipped = 0;

        try
        {
            var seriesName = SelectedAnime?.Name ?? "";

            // UNA sola scansione della coda
            var existingKeys = _queueService.SnapshotExistingEpisodeKeys("anime", seriesName);

            var toDownload = new List<Episode>();
            foreach (var ep in selected)
            {
                var epNum = int.TryParse(ep.Number, out var n) ? n : 0;
                if (existingKeys.Contains(epNum)) { skipped++; continue; }
                toDownload.Add(ep);
            }

            if (toDownload.Count == 0)
            {
                StatusMessage = $"Tutti i {skipped} episodi selezionati sono già in coda.";
                return;
            }

            // Accodamento istantaneo: il link di ogni episodio si chiede al sito all'avvio
            // del suo download (con ritmo e nuovi tentativi), non tutti insieme adesso:
            // con centinaia di episodi il sito rispondeva 403 e l'accodamento si fermava.
            var provider = SelectedProvider!;
            var items = toDownload.Select(ep => BuildLazyDownloadItem(ep, seriesName, provider)).ToList();
            await _queueService.EnqueueManyAsync(items);

            var msg = $"{items.Count} episodi accodati";
            if (skipped > 0) msg += $", {skipped} già in coda";
            StatusMessage = msg + ": il link di ognuno viene preso dal sito quando parte il suo download.";
        }
        catch (OperationCanceledException) { StatusMessage = "Accodamento annullato."; }
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
            i.Category == "anime" &&
            (i.Status == DownloadStatus.Pending ||
            i.Status == DownloadStatus.Paused ||
            i.Status == DownloadStatus.Failed));

        if (count == 0) { StatusMessage = "Nessun download da avviare."; return; }
        _queueService.StartAll("anime");
        StatusMessage = $"Avvio di {count} download.";
    }

    [RelayCommand]
    private void PauseAll()
    {
        _batchCts?.Cancel();
        _queueService.PauseAll("anime");
        StatusMessage = "In pausa.";
    }

    [RelayCommand]
    private void CancelAllDownloads()
    {
        _queueService.CancelAll("anime");
        StatusMessage = "Annullati.";
    }

    [RelayCommand]
    private async Task DeleteEverythingAsync()
    {
        await _queueService.DeleteEverythingAsync("anime");
        StatusMessage = "Eliminati.";
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        _queueService.ClearCompleted("anime");
        StatusMessage = "Rimossi completati.";
    }

    [RelayCommand]
    private void ClearCancelledDownloads()
    {
        _queueService.ClearCancelled("anime");
        StatusMessage = "Rimossi annullati.";
    }

    [RelayCommand]
    private void ClearFailedDownloads()
    {
        _queueService.ClearFailed("anime");
        StatusMessage = "Rimossi failed.";
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

    public Task RetryItemPublicAsync(DownloadItem item) => _queueService.RetryItemAsync(item);
    public Task RetryFailedGroupPublicAsync(SeriesGroup group) => _queueService.RetryFailedInGroupAsync(group);
    public Task RetryCancelledGroupPublicAsync(SeriesGroup group) => _queueService.RetryCancelledInGroupAsync(group);
    public void CancelGroupPublic(SeriesGroup group)
    {
        _queueService.CancelGroup(group);
        StatusMessage = $"Annullata la serie: {group.SeriesName}";
    }

    public async Task RemoveGroupPublicAsync(SeriesGroup group)
    {
        // Prendi tutti gli item della serie
        var items = group.Items.ToList();
        if (items.Count == 0) return;

        // Usa il nuovo metodo ottimizzato
        await _queueService.RemoveManyAsync(items);
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

    private void OnDownloadItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not DownloadItem item) return;
        if (e.PropertyName != nameof(DownloadItem.Status)) return;

        var seriesName = string.IsNullOrWhiteSpace(item.SeriesName) ? "Senza serie" : item.SeriesName;
        var group = SeriesGroups.FirstOrDefault(g => g.SeriesName == seriesName);
        group?.ScheduleRefresh();
    }
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

        OnPropertyChanged(nameof(SeriesSummaryText));
    }

    private void AddToGroup(DownloadItem item)
    {
        if (item.Category != "anime") return;

        var seriesName = string.IsNullOrWhiteSpace(item.SeriesName) ? "Senza serie" : item.SeriesName;

        var group = SeriesGroups.FirstOrDefault(g => g.SeriesName == seriesName);
        if (group == null)
        {
            group = new SeriesGroup { SeriesName = seriesName, IsExpanded = false };
            SeriesGroups.Add(group);
        }

        if (!group.Items.Contains(item))
        {
            group.Items.Add(item);
            item.PropertyChanged += OnDownloadItemPropertyChanged;
        }
    }

    private void RemoveFromGroup(DownloadItem item)
    {
        var seriesName = string.IsNullOrWhiteSpace(item.SeriesName) ? "Senza serie" : item.SeriesName;

        var group = SeriesGroups.FirstOrDefault(g => g.SeriesName == seriesName);
        if (group != null)
        {
            item.PropertyChanged -= OnDownloadItemPropertyChanged;
            group.Items.Remove(item);
            if (group.Items.Count == 0)
                SeriesGroups.Remove(group);
        }
    }
}