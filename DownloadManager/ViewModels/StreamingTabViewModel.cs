using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Download;
using DownloadManager.Services.Streaming.StreamingCommunity;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using Avalonia.Media.Imaging;

namespace DownloadManager.ViewModels;

public partial class StreamingTabViewModel : ViewModelBase
{
    private readonly IStreamingCommunityService _service;
    private readonly DownloadQueueService _queueService;
    private readonly FileNameBuilder _fileNameBuilder;
    private readonly SettingsService _settings;
    private readonly SharedState _shared;
    private readonly ILogger<StreamingTabViewModel> _logger;
    private readonly ObservableCollection<DownloadItem> _downloads = new();
    private CancellationTokenSource? _batchCts;
    private bool _loadingQualities;

    private const string CategoryName = "streaming";

    // ============================================================
    //  PROPERTY RICERCA
    // ============================================================

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _statusMessage = "Cerca un film o una serie TV per iniziare.";
    [ObservableProperty] private ObservableCollection<MovieSearchResult> _searchResults = new();
    [ObservableProperty] private MovieSearchResult? _selectedResult;
    [ObservableProperty] private ObservableCollection<Season> _seasons = new();
    [ObservableProperty] private Season? _selectedSeason;
    [ObservableProperty] private ObservableCollection<StreamingEpisode> _episodes = new();
    [ObservableProperty] private bool _isQualitiesLoading;
    [ObservableProperty] private bool _isLoadingSearchResults;
    [ObservableProperty] private bool _isLoadingEpisodes;
    [ObservableProperty] private bool _isBatchActive;

    // === Due pattern separati per Film e Serie ===
    [ObservableProperty] private string _movieNamingPattern = "{Series} ({Anno})";
    [ObservableProperty] private string _seriesNamingPattern = "{Episode}";
    [ObservableProperty] private string _seasonFolderPattern = "Stagione {number}";

    [ObservableProperty] private ObservableCollection<VideoQuality> _availableQualities = new();

    // === Tracce audio / sottotitoli trovate dall'analisi ===
    public ObservableCollection<MediaTrack> AudioTracks { get; } = new();
    public ObservableCollection<MediaTrack> SubtitleTracks { get; } = new();
    [ObservableProperty] private bool _burnSubtitles;
    [ObservableProperty] private int _concurrentFragments = 24;

    public bool HasAudioTracks => AudioTracks.Count > 0;
    public bool HasSubtitleTracks => SubtitleTracks.Count > 0;

    public string AudioSelectionText
    {
        get
        {
            var sel = AudioTracks.Where(t => t.IsSelected).Select(t => t.DisplayName).ToList();
            return sel.Count == 0 ? "Automatico (italiano)" : string.Join(", ", sel);
        }
    }

    public string SubtitleSelectionText
    {
        get
        {
            var sel = SubtitleTracks.Where(t => t.IsSelected).Select(t => t.DisplayName).ToList();
            return sel.Count == 0 ? "Nessuno" : string.Join(", ", sel);
        }
    }
    [ObservableProperty] private VideoQuality? _selectedQuality;

    [ObservableProperty] private int _maxConcurrency = 1;

    // ============================================================
    //  COLLEZIONI
    // ============================================================

    public ObservableCollection<string> SearchHistory { get; } = new();
    public ObservableCollection<SeriesGroup> SeriesGroups { get; } = new();
    public ObservableCollection<DownloadItem> RecentDownloads => _downloads;

    public string[] MovieNamingPresets { get; } = new[]
    {
        "{Series} ({Anno})",
        "{Series}",
        "{OriginalFileName}"
    };

    public string[] SeriesNamingPresets { get; } = new[]
    {
        "{Episode}",
        "{Episode:D2}",
        "{Episode:D3}",
        "{Series} - {Episode:D3}",
        "{Series} S{Season:D2}E{Episode:D2}",
        "{OriginalFileName}"
    };

    // ============================================================
    //  COMPUTED
    // ============================================================

    public string SearchResultsCountText => SearchResults.Count == 0 ? "" : $"({SearchResults.Count} risultati)";
    public string EpisodesCountText => Episodes.Count == 0 ? "" : $"{Episodes.Count} episodi";
    public string EpisodesSelectedCountText => Episodes.Count == 0 ? "" : $"{Episodes.Count(e => e.IsSelected)} selezionati";
    public string SelectedResultName => SelectedResult?.Title ?? string.Empty;
    public bool HasSelectedResult => SelectedResult != null;
    public bool IsSelectedResultSeries => SelectedResult?.IsSeries == true;
    public bool IsSelectedResultMovie => SelectedResult != null && !SelectedResult.IsSeries;
    public bool HasSeasons => Seasons.Count > 0;
    public bool HasFailedStreamingItems =>
        _queueService.AllItems.Any(i =>
            i.Category == CategoryName &&
            i.Status == DownloadStatus.Failed);

    public bool HasActiveStreamingItems =>
        _queueService.AllItems.Any(i =>
            i.Category == CategoryName &&
            (i.Status == DownloadStatus.Downloading ||
            i.Status == DownloadStatus.Pending ||
            i.Status == DownloadStatus.Paused));

    public bool ShowStreamingRed => HasFailedStreamingItems;

    public bool ShowStreamingYellow => HasActiveStreamingItems && !HasFailedStreamingItems;

    public bool ShowStreamingGreen => !HasActiveStreamingItems && !HasFailedStreamingItems;
    /// <summary>
    /// True solo quando l'analisi qualità è finita CON SUCCESSO
    /// (almeno un elemento in AvailableQualities). Blocca il pulsante
    /// "Scarica film" sia durante il caricamento sia in caso di errore.
    /// </summary>
    public bool CanDownloadMovie => !IsQualitiesLoading && AvailableQualities.Count > 0;

    // === Cartella base: usa settings streaming separati ===
    public string BaseDownloadFolder
    {
        get => _settings.Current.StreamingBaseDownloadFolder;
        set
        {
            _settings.Current.StreamingBaseDownloadFolder = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool CreateSeriesFolder
    {
        get => _settings.Current.StreamingCreateSeriesFolder;
        set
        {
            _settings.Current.StreamingCreateSeriesFolder = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    // === Anteprime separate ===
    public string MoviePatternPreview
    {
        get
        {
            try
            {
                var preview = FileNameBuilder.ApplyPattern(
                    MovieNamingPattern, "Nome Film", 1, "Titolo", "file", year: "2024");
                return preview + ".mp4";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string SeriesPatternPreview
    {
        get
        {
            try
            {
                var preview = FileNameBuilder.ApplyPattern(
                    SeriesNamingPattern, "Nome Serie", 5, "Titolo Ep", "file", season: 1);
                return preview + ".mp4";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string SeasonFolderPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SeasonFolderPattern))
                return "(nessuna sottocartella)";
            try
            {
                return SeasonFolderPattern
                    .Replace("{number:D2}", "01")
                    .Replace("{number:D3}", "001")
                    .Replace("{number}", "1");
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string GlobalSpeedText
    {
        get
        {
            double speed = _queueService.AllItems
                .Where(i => i.Category == CategoryName)
                .Sum(i => i.SpeedBytesPerSecond);
            return speed > 0 ? $"{speed / 1024 / 1024:F2} MB/s" : "-";
        }
    }

    public string GlobalSizeText
    {
        get
        {
            long total = _queueService.AllItems.Where(i => i.Category == CategoryName).Sum(i => i.TotalBytes);
            long done = _queueService.AllItems.Where(i => i.Category == CategoryName).Sum(i => i.DownloadedBytes);
            return total > 0 ? $"{done / 1024.0 / 1024 / 1024:F2} / {total / 1024.0 / 1024 / 1024:F2} GB" : "-";
        }
    }

    public string GlobalEtaText
    {
        get
        {
            double totalSpeed = _queueService.AllItems.Where(i => i.Category == CategoryName).Sum(i => i.SpeedBytesPerSecond);
            if (totalSpeed <= 0) return "-";
            long totalBytes = _queueService.AllItems.Where(i => i.Category == CategoryName).Sum(i => i.TotalBytes);
            long downloaded = _queueService.AllItems.Where(i => i.Category == CategoryName).Sum(i => i.DownloadedBytes);
            long remaining = totalBytes - downloaded;
            if (remaining <= 0) return "0s";
            var s = remaining / totalSpeed;
            if (s < 60) return $"{(int)s}s";
            if (s < 3600) return $"{(int)(s / 60)}m {(int)(s % 60)}s";
            return $"{(int)(s / 3600)}h {(int)((s % 3600) / 60)}m";
        }
    }

    public string SeriesSummaryText
    {
        get
        {
            var groups = SeriesGroups;
            if (groups.Count == 0) return "Nessuna serie in coda.";

            int total = groups.Sum(g => g.Items.Count);
            int completed = groups.Sum(g => g.Items.Count(i => i.Status == DownloadStatus.Completed));
            int active = groups.Sum(g => g.Items.Count(i => i.Status == DownloadStatus.Downloading));
            int pending = groups.Sum(g => g.Items.Count(i =>
                i.Status == DownloadStatus.Pending || i.Status == DownloadStatus.Paused));
            int failed = groups.Sum(g => g.Items.Count(i => i.Status == DownloadStatus.Failed));

            var parts = new List<string> { $"{total} episodi" };
            if (completed > 0) parts.Add($"{completed} completati");
            if (active > 0) parts.Add($"{active} in corso");
            if (pending > 0) parts.Add($"{pending} in attesa");
            if (failed > 0) parts.Add($"{failed} falliti");

            return $"{groups.Count} serie · {string.Join(" · ", parts)}";
        }
    }

    /// <summary>
    /// True se l'utente può interagire con i pulsanti di download
    /// (ci sono episodi, non sta caricando episodi né qualità).
    /// </summary>
    public bool CanInteractWithEpisodes =>
        Episodes.Count > 0 && !IsLoadingEpisodes && !IsQualitiesLoading;

    // ============================================================
    //  COSTRUTTORE
    // ============================================================

    public StreamingTabViewModel(
        IStreamingCommunityService service,
        DownloadQueueService queueService,
        FileNameBuilder fileNameBuilder,
        SettingsService settings,
        ILogger<StreamingTabViewModel> logger,
        SharedState shared)
    {
        _service = service;
        _queueService = queueService;
        _fileNameBuilder = fileNameBuilder;
        _settings = settings;
        _logger = logger;
        _shared = shared;

        MovieNamingPattern = _settings.Current.StreamingMovieNamingPattern;
        SeriesNamingPattern = _settings.Current.StreamingSeriesNamingPattern;
        SeasonFolderPattern = _settings.Current.SeasonFolderPattern;
        // Slot propri: prima questa riga forzava a 1 il limite di TUTTA la coda,
        // rallentando anche gli anime.
        MaxConcurrency = _settings.Current.MaxConcurrencyStreaming;
        BurnSubtitles = _settings.Current.StreamingBurnSubtitles;
        ConcurrentFragments = Math.Clamp(_settings.Current.StreamingConcurrentFragments, 4, 64);
        _queueService.SetMaxConcurrency(CategoryName, MaxConcurrency);

        SearchResults.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(SearchResultsCountText));

        Episodes.CollectionChanged += (_, e) =>
        {
            OnPropertyChanged(nameof(CanInteractWithEpisodes));
            OnPropertyChanged(nameof(EpisodesCountText));
            OnPropertyChanged(nameof(EpisodesSelectedCountText));

            if (e.NewItems != null)
                foreach (StreamingEpisode ep in e.NewItems)
                    ep.PropertyChanged += OnEpisodePropertyChanged;

            if (e.OldItems != null)
                foreach (StreamingEpisode ep in e.OldItems)
                    ep.PropertyChanged -= OnEpisodePropertyChanged;
        };
        AvailableQualities.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanDownloadMovie));
        };

        // Sync con SharedState SOLO per il badge globale — non per la cartella
        _shared.PropertyChanged += (_, e) =>
        {
            // Non sincronizziamo più BaseDownloadFolder/CreateSeriesFolder:
            // ora sono separate da Anime e vivono in _settings.
        };

        foreach (var item in _queueService.AllItems.Where(i => i.Category == CategoryName))
        {
            _downloads.Add(item);
            AddToGroup(item);
        }

        _queueService.AllItems.CollectionChanged += OnQueueCollectionChanged;

        var statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        statsTimer.Tick += (_, _) =>
        {
            foreach (var g in SeriesGroups) g.RefreshStats();
            OnPropertyChanged(nameof(GlobalSpeedText));
            OnPropertyChanged(nameof(GlobalSizeText));
            OnPropertyChanged(nameof(GlobalEtaText));
            OnPropertyChanged(nameof(SeriesSummaryText));
            OnPropertyChanged(nameof(HasFailedStreamingItems));
            OnPropertyChanged(nameof(HasActiveStreamingItems));
            OnPropertyChanged(nameof(ShowStreamingRed));
            OnPropertyChanged(nameof(ShowStreamingYellow));
            OnPropertyChanged(nameof(ShowStreamingGreen));
        };
        statsTimer.Start();

        // Notifica ripresa coda streaming
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var restored = _queueService.AllItems.Count(i =>
                    i.Category == CategoryName &&
                    (i.Status == DownloadStatus.Pending || i.Status == DownloadStatus.Paused));

                if (restored > 0)
                {
                    StatusMessage = $"Ripristinati {restored} download in coda dalla sessione precedente. Premi ▶ Avvia tutti per riprenderli.";
                }
            });
        });
    }

    private void OnQueueCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _downloads.Clear();
            SeriesGroups.Clear();
            return;
        }

        if (e.NewItems != null)
            foreach (DownloadItem it in e.NewItems)
                if (it.Category == CategoryName)
                {
                    if (!_downloads.Contains(it)) _downloads.Add(it);
                    AddToGroup(it);
                }

        if (e.OldItems != null)
            foreach (DownloadItem it in e.OldItems)
            {
                _downloads.Remove(it);
                RemoveFromGroup(it);
            }

        OnPropertyChanged(nameof(SeriesSummaryText));
    }

    private void AddToGroup(DownloadItem item)
    {
        if (item.Category != CategoryName) return;

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
            if (group.Items.Count == 0) SeriesGroups.Remove(group);
        }
    }

    private void OnDownloadItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DownloadItem item) return;
        if (e.PropertyName != nameof(DownloadItem.Status)) return;

        var seriesName = string.IsNullOrWhiteSpace(item.SeriesName) ? "Senza serie" : item.SeriesName;
        var group = SeriesGroups.FirstOrDefault(g => g.SeriesName == seriesName);
        group?.RefreshStats();
    }

    private void OnEpisodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StreamingEpisode.IsSelected))
            OnPropertyChanged(nameof(EpisodesSelectedCountText));
    }

    // ============================================================
    //  PROPERTY CHANGED
    // ============================================================

    partial void OnSelectedResultChanged(MovieSearchResult? value)
    {
        OnPropertyChanged(nameof(SelectedResultName));
        OnPropertyChanged(nameof(HasSelectedResult));
        OnPropertyChanged(nameof(IsSelectedResultSeries));
        OnPropertyChanged(nameof(IsSelectedResultMovie));

        Episodes.Clear();
        Seasons.Clear();
        SelectedSeason = null;
        AvailableQualities.Clear();
        SelectedQuality = null;
        ClearTracks();
        IsQualitiesLoading = false;   // ← NUOVO (reset nel caso rimanesse appeso)
        OnPropertyChanged(nameof(HasSeasons));
    }

    partial void OnSelectedSeasonChanged(Season? value)
    {
        if (value == null) return;
        if (SelectedResult == null || !SelectedResult.IsSeries) return;

        _ = LoadEpisodesForSeasonAsync(SelectedResult.Link, value.Number);
        _ = LoadQualitiesForCurrentSelectionAsync();
    }

    partial void OnMovieNamingPatternChanged(string value)
    {
        _settings.Current.StreamingMovieNamingPattern = value;
        _settings.Save();
        OnPropertyChanged(nameof(MoviePatternPreview));
    }

    partial void OnSeriesNamingPatternChanged(string value)
    {
        _settings.Current.StreamingSeriesNamingPattern = value;
        _settings.Save();
        OnPropertyChanged(nameof(SeriesPatternPreview));
    }

    partial void OnSeasonFolderPatternChanged(string value)
    {
        _settings.Current.SeasonFolderPattern = value;
        _settings.Save();
        OnPropertyChanged(nameof(SeasonFolderPreview));
    }

    partial void OnMaxConcurrencyChanged(int value)
    {
        _queueService?.SetMaxConcurrency(CategoryName, value);
        if (_settings != null) { _settings.Current.MaxConcurrencyStreaming = value; _settings.Save(); }
    }

    // ============================================================
    //  RICERCA
    // ============================================================

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) { StatusMessage = "Inserisci un termine."; return; }
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
            StatusMessage = $"Ricerca in corso per '{term}'...";
            var results = await _service.SearchAsync(term);

            SearchResults.Clear();
            foreach (var r in results)
            {
                SearchResults.Add(r);
                _ = LoadPosterAsync(r);   // scarica in background, non blocca la UI
            }

            StatusMessage = $"Trovati {results.Count} risultati per '{term}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore: {ex.Message}";
            _logger.LogWarning(ex, "[STREAMING-TAB] Errore ricerca");
        }
        finally { IsLoadingSearchResults = false; }
    }

    [RelayCommand]
    private void ClearSearchHistory()
    {
        SearchHistory.Clear();
        StatusMessage = "Cronologia ricerche svuotata.";
    }

    private void SaveSearchToHistory(string term)
    {
        if (string.IsNullOrEmpty(term)) return;
        var existing = SearchHistory.FirstOrDefault(h =>
            string.Equals(h, term, StringComparison.OrdinalIgnoreCase));
        if (existing != null) SearchHistory.Remove(existing);
        SearchHistory.Insert(0, term);
        while (SearchHistory.Count > 20) SearchHistory.RemoveAt(SearchHistory.Count - 1);
    }

    private static readonly HttpClient _posterHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap> _posterCache = new();

    private static async Task LoadPosterAsync(MovieSearchResult result)
    {
        if (string.IsNullOrEmpty(result.PosterUrl)) return;

        // Già in cache?
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
            System.Diagnostics.Debug.WriteLine($"[POSTER] Errore {result.PosterUrl}: {ex.Message}");
        }
    }

    // ============================================================
    //  STAGIONI / EPISODI
    // ============================================================

    partial void OnIsLoadingEpisodesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteractWithEpisodes));
    }

    partial void OnIsQualitiesLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteractWithEpisodes));
        OnPropertyChanged(nameof(CanDownloadMovie));
    }

    [RelayCommand]
    private async Task LoadSeasonsAsync(MovieSearchResult? result)
    {
        if (result == null) return;

        SelectedResult = result;
        IsLoadingEpisodes = true;

        try
        {
            StatusMessage = $"Analisi di '{result.Title}'...";
            var seasons = await _service.GetSeasonsAsync(result.Link);

            if (seasons.Count == 0)
            {
                result.IsSeries = false;
                OnPropertyChanged(nameof(IsSelectedResultSeries));
                OnPropertyChanged(nameof(IsSelectedResultMovie));

                StatusMessage = $"Film selezionato: '{result.Title}'. Clicca 'Scarica film' per procedere.";
                _ = LoadQualitiesForCurrentSelectionAsync();
                return;
            }

            result.IsSeries = true;
            OnPropertyChanged(nameof(IsSelectedResultSeries));
            OnPropertyChanged(nameof(IsSelectedResultMovie));

            Seasons.Clear();
            foreach (var s in seasons) Seasons.Add(s);
            OnPropertyChanged(nameof(HasSeasons));

            SelectedSeason = Seasons[0];
            StatusMessage = $"'{result.Title}': {seasons.Count} stagioni caricate.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore: {ex.Message}";
            _logger.LogWarning(ex, "[STREAMING-TAB] Errore caricamento stagioni");
        }
        finally { IsLoadingEpisodes = false; }
    }

    private async Task LoadEpisodesForSeasonAsync(string seriesLink, int seasonNumber)
    {
        IsLoadingEpisodes = true;
        try
        {
            StatusMessage = $"Caricamento episodi stagione {seasonNumber}...";
            var episodes = await _service.GetEpisodesAsync(seriesLink, seasonNumber);

            Episodes.Clear();
            foreach (var ep in episodes) Episodes.Add(ep);

            StatusMessage = $"Trovati {episodes.Count} episodi (stagione {seasonNumber}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore: {ex.Message}";
            _logger.LogWarning(ex, "[STREAMING-TAB] Errore caricamento episodi");
        }
        finally { IsLoadingEpisodes = false; }
    }

    private void ClearTracks()
    {
        foreach (var t in AudioTracks.Concat(SubtitleTracks)) t.PropertyChanged -= OnTrackSelectionChanged;
        AudioTracks.Clear();
        SubtitleTracks.Clear();
        NotifyTracksChanged();
    }

    /// <summary>
    /// Mostra le tracce trovate e preseleziona le ultime lingue scelte; se nessuna è
    /// disponibile, l'audio italiano (o quello predefinito della playlist).
    /// </summary>
    private void LoadTracks(MasterTracks tracks)
    {
        ClearTracks();

        var savedAudio = _settings.Current.StreamingAudioLanguages.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var savedSubs = _settings.Current.StreamingSubtitleLanguages.Split('|', StringSplitOptions.RemoveEmptyEntries);

        foreach (var a in tracks.Audios)
        {
            a.IsSelected = savedAudio.Any(a.Matches);
            AudioTracks.Add(a);
        }
        if (AudioTracks.Count > 0 && !AudioTracks.Any(t => t.IsSelected))
            (AudioTracks.FirstOrDefault(t => t.IsItalian) ?? AudioTracks.FirstOrDefault(t => t.IsDefault) ?? AudioTracks[0]).IsSelected = true;

        foreach (var sub in tracks.Subtitles)
        {
            sub.IsSelected = savedSubs.Any(sub.Matches);
            SubtitleTracks.Add(sub);
        }

        foreach (var t in AudioTracks.Concat(SubtitleTracks)) t.PropertyChanged += OnTrackSelectionChanged;
        NotifyTracksChanged();
    }

    private void OnTrackSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MediaTrack.IsSelected)) return;
        _settings.Current.StreamingAudioLanguages = string.Join("|", AudioTracks.Where(t => t.IsSelected).Select(t => t.Key));
        _settings.Current.StreamingSubtitleLanguages = string.Join("|", SubtitleTracks.Where(t => t.IsSelected).Select(t => t.Key));
        _settings.Save();
        NotifyTracksChanged();
    }

    public string AudioCountText => $"{AudioTracks.Count(t => t.IsSelected)} di {AudioTracks.Count}";
    public string SubtitleCountText => $"{SubtitleTracks.Count(t => t.IsSelected)} di {SubtitleTracks.Count}";

    [RelayCommand] private void SelectAllAudio() => SetAll(AudioTracks, true);
    [RelayCommand] private void SelectNoAudio() => SetAll(AudioTracks, false);
    [RelayCommand] private void SelectAllSubtitles() => SetAll(SubtitleTracks, true);
    [RelayCommand] private void SelectNoSubtitles() => SetAll(SubtitleTracks, false);

    private static void SetAll(IEnumerable<MediaTrack> tracks, bool value)
    {
        foreach (var t in tracks) t.IsSelected = value;
    }

    private void NotifyTracksChanged()
    {
        OnPropertyChanged(nameof(AudioCountText));
        OnPropertyChanged(nameof(SubtitleCountText));
        OnPropertyChanged(nameof(HasAudioTracks));
        OnPropertyChanged(nameof(HasSubtitleTracks));
        OnPropertyChanged(nameof(AudioSelectionText));
        OnPropertyChanged(nameof(SubtitleSelectionText));
    }

    partial void OnBurnSubtitlesChanged(bool value)
    {
        _settings.Current.StreamingBurnSubtitles = value;
        _settings.Save();
    }

    partial void OnConcurrentFragmentsChanged(int value)
    {
        _settings.Current.StreamingConcurrentFragments = value;
        _settings.Save();
    }

    private async Task LoadQualitiesForCurrentSelectionAsync()
    {
        if (_loadingQualities) return;
        _loadingQualities = true;
        IsQualitiesLoading = true;   // ← NUOVO

        try
        {
            AvailableQualities.Clear();
            SelectedQuality = null;
            ClearTracks();

            if (SelectedResult == null) return;
            if (SelectedResult.IsSeries && SelectedSeason == null) return;

            int seasonNum = SelectedSeason?.Number ?? 1;
            bool isSeries = SelectedResult.IsSeries;

            StatusMessage = "Analisi di qualità, audio e sottotitoli...";
            var tracks = await _service.GetAvailableTracksAsync(
                SelectedResult.Link, isSeries, seasonNum, 1);
            var qualities = tracks.Videos;
            LoadTracks(tracks);

            AvailableQualities.Add(new VideoQuality
            {
                Name = "Auto (max)",
                Url = "",
                Bandwidth = 0    // ← MODIFICATO
            });

            foreach (var q in qualities) AvailableQualities.Add(q);

            SelectedQuality = AvailableQualities.FirstOrDefault();

            StatusMessage = qualities.Count > 0
                ? $"Qualità: {string.Join(", ", qualities.Select(q => q.Name))} · Audio: {AudioTracks.Count} · Sottotitoli: {SubtitleTracks.Count}"
                : "Nessuna qualità rilevata (uso auto).";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[STREAMING-TAB] Errore caricamento qualità");
            StatusMessage = $"⚠ Errore analisi qualità: {ex.Message}";
        }
        finally
        {
            _loadingQualities = false;
            IsQualitiesLoading = false;   // ← NUOVO
        }
    }

    // ============================================================
    //  DOWNLOAD FILM
    // ============================================================

    [RelayCommand]
    private async Task DownloadMovieAsync(MovieSearchResult? movie)
    {
        movie ??= SelectedResult;
        if (movie == null || movie.IsSeries) return;

        try
        {
            // Come per gli episodi: il film entra subito in coda e il link (che scade in
            // pochi minuti) viene estratto all'avvio del download.
            var link = movie.Link;
            Func<CancellationToken, Task<string?>> extractor = _ => _service.GetMovieStreamUrlAsync(link);

            var item = BuildDownloadItem(
                "", movie.Title, 1, movie.Title,
                urlExtractor: extractor,
                contentLink: movie.Link,
                episodeId: 0,
                isSeries: false,
                year: movie.Year ?? "");

            await _queueService.EnqueueAsync(item);

            StatusMessage = $"Film accodato: {movie.Title}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Errore: {ex.Message}";
            _logger.LogWarning(ex, "[STREAMING-TAB] Errore download film");
        }
    }

    // ============================================================
    //  DOWNLOAD EPISODI SELEZIONATI
    // ============================================================

    [RelayCommand]
    private async Task DownloadSelectedEpisodesAsync()
    {
        var selected = Episodes.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0) { StatusMessage = "Nessun episodio selezionato."; return; }
        if (IsBatchActive) { StatusMessage = "Accodamento già in corso."; return; }
        if (SelectedResult == null || SelectedSeason == null) return;

        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;
        IsBatchActive = true;

        try
        {
            var seriesName = SelectedResult.Title;
            if (Seasons.Count > 1)
                seriesName = $"{SelectedResult.Title} S{SelectedSeason.Number:D2}";

            var seasonNumber = SelectedSeason.Number;

            var toDownload = new List<StreamingEpisode>();
            int skipped = 0;
            foreach (var ep in selected)
            {
                var epNum = int.TryParse(ep.Number, out var n) ? n : 0;
                if (_queueService.ContainsEpisode(CategoryName, seriesName, epNum)) { skipped++; continue; }
                toDownload.Add(ep);
            }

            if (toDownload.Count == 0)
            {
                StatusMessage = $"Tutti i {skipped} episodi selezionati già in coda.";
                return;
            }

            var queued = await EnqueueEpisodesAsync(
                SelectedResult.Link, seasonNumber, seriesName, toDownload, token);

            var msg = $"{queued} episodi accodati";
            if (skipped > 0) msg += $", {skipped} già in coda";
            StatusMessage = msg + ".";
        }
        catch (OperationCanceledException) { StatusMessage = "Annullato."; }
        catch (Exception ex) { StatusMessage = $"Errore: {ex.Message}"; }
        finally
        {
            _batchCts?.Dispose();
            _batchCts = null;
            IsBatchActive = false;
        }
    }

    // ============================================================
    //  SCARICA STAGIONE / SERIE
    // ============================================================

    [RelayCommand]
    private async Task DownloadCurrentSeasonAsync()
    {
        if (SelectedResult == null || SelectedSeason == null) return;
        if (IsBatchActive) { StatusMessage = "Accodamento già in corso."; return; }

        var season = SelectedSeason;
        var series = SelectedResult;

        if (Episodes.Count == 0)
            await LoadEpisodesForSeasonAsync(series.Link, season.Number);

        if (Episodes.Count == 0)
        {
            StatusMessage = $"Nessun episodio per la stagione {season.Number}.";
            return;
        }

        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;
        IsBatchActive = true;

        try
        {
            var seriesName = Seasons.Count > 1
                ? $"{series.Title} S{season.Number:D2}"
                : series.Title;

            var toAdd = new List<StreamingEpisode>();
            foreach (var ep in Episodes)
            {
                var epNum = int.TryParse(ep.Number, out var n) ? n : 0;
                if (epNum <= 0) continue;
                if (_queueService.ContainsEpisode(CategoryName, seriesName, epNum)) continue;
                toAdd.Add(ep);
            }

            var queued = await EnqueueEpisodesAsync(
                series.Link, season.Number, seriesName, toAdd, token);

            StatusMessage = queued > 0
                ? $"Stagione {season.Number}: {queued} episodi accodati."
                : "Nessun nuovo episodio da accodare.";
        }
        catch (OperationCanceledException) { StatusMessage = "Annullato."; }
        catch (Exception ex)
        {
            StatusMessage = $"Errore: {ex.Message}";
            _logger.LogWarning(ex, "[STREAMING-TAB] Errore scarica stagione");
        }
        finally
        {
            _batchCts?.Dispose();
            _batchCts = null;
            IsBatchActive = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAllSeasonsAsync()
    {
        if (SelectedResult == null) return;
        if (Seasons.Count == 0) { StatusMessage = "Nessuna stagione."; return; }
        if (IsBatchActive) { StatusMessage = "Accodamento già in corso."; return; }

        var series = SelectedResult;
        _batchCts = new CancellationTokenSource();
        var token = _batchCts.Token;
        IsBatchActive = true;

        int totalQueued = 0;
        int seasonIdx = 0;

        try
        {
            foreach (var season in Seasons)
            {
                token.ThrowIfCancellationRequested();
                seasonIdx++;

                var seriesName = Seasons.Count > 1
                    ? $"{series.Title} S{season.Number:D2}"
                    : series.Title;

                StatusMessage = $"Stagione {season.Number} ({seasonIdx}/{Seasons.Count})...";

                try
                {
                    var eps = await _service.GetEpisodesAsync(series.Link, season.Number);
                    var toAdd = eps.Where(ep => ep.EpisodeId > 0).ToList();

                    var queued = await EnqueueEpisodesAsync(
                        series.Link, season.Number, seriesName, toAdd, token);

                    totalQueued += queued;
                    StatusMessage = $"Accodati: {totalQueued} episodi ({seasonIdx}/{Seasons.Count} stagioni)";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[STREAMING-TAB] Errore stagione {N}", season.Number);
                }
            }

            StatusMessage = $"Tutta la serie: {totalQueued} episodi accodati.";
        }
        catch (OperationCanceledException) { StatusMessage = $"Annullato dopo {totalQueued}."; }
        catch (Exception ex)
        {
            StatusMessage = $"Errore: {ex.Message}";
        }
        finally
        {
            _batchCts?.Dispose();
            _batchCts = null;
            IsBatchActive = false;
        }
    }

    // ============================================================
    //  ACCODAMENTO EPISODI
    // ============================================================

    /// <summary>
    /// Accoda subito gli episodi senza estrarre prima gli URL: l'estrazione avviene
    /// all'avvio di ciascun download. I token vixcloud scadono in poco tempo, quindi
    /// pre-estrarli per un'intera stagione era solo attesa sprecata (venivano
    /// comunque ri-estratti per tutti gli episodi non partiti subito).
    /// </summary>
    private async Task<int> EnqueueEpisodesAsync(
        string seriesLink, int seasonNumber, string seriesName,
        List<StreamingEpisode> episodes, CancellationToken ct)
    {
        if (episodes.Count == 0) return 0;

        var existing = _queueService.SnapshotExistingEpisodeKeys(CategoryName, seriesName);
        var items = new List<DownloadItem>(episodes.Count);

        foreach (var ep in episodes.OrderBy(e => int.TryParse(e.Number, out var n) ? n : 0))
        {
            ct.ThrowIfCancellationRequested();

            var epNum = int.TryParse(ep.Number, out var parsed) ? parsed : 0;
            if (epNum <= 0 || !existing.Add(epNum)) continue;

            int? epId = ep.EpisodeId > 0 ? ep.EpisodeId : null;
            Func<CancellationToken, Task<string?>> extractor = _ => _service.GetVideoUrlAsync(
                seriesLink, isSeries: true,
                seasonNumber: seasonNumber, episodeNumber: epNum,
                episodeId: epId);

            items.Add(BuildDownloadItem(
                "", seriesName, epNum, ep.Title, seasonNumber,
                extractor, null,
                contentLink: seriesLink,
                episodeId: ep.EpisodeId,
                isSeries: true));
        }

        await _queueService.EnqueueManyAsync(items);
        _logger.LogInformation(
            "[STREAMING-TAB] Accodati {Count} episodi di '{Series}' (URL estratti all'avvio di ogni download)",
            items.Count, seriesName);
        return items.Count;
    }

    [RelayCommand]
    private void CancelBatch()
    {
        _batchCts?.Cancel();
        StatusMessage = "Annullamento...";
    }

    // ============================================================
    //  HELPER
    // ============================================================

    private DownloadItem BuildDownloadItem(
        string videoUrl, string seriesName,
        int episodeNumber, string title,
        int seasonNumber = 0,
        Func<CancellationToken, Task<string?>>? urlExtractor = null,
        Func<CancellationToken, Task<string?>>? audioExtractor = null,
        string contentLink = "",
        int episodeId = 0,
        bool isSeries = false,
        string year = "")
    {
        // === Pattern corretto in base al tipo ===
        var pattern = isSeries ? SeriesNamingPattern : MovieNamingPattern;

        var item = new DownloadItem
        {
            Url = videoUrl,
            SeriesName = seriesName,
            EpisodeNumber = episodeNumber,
            SeasonNumber = seasonNumber,
            EpisodeTitle = title,
            Year = year,
            Status = DownloadStatus.Pending,
            Category = CategoryName,
            UrlExtractor = urlExtractor,
            AudioExtractor = audioExtractor,
            CookieExtractor = async token =>
            {
                return await _service.GetSessionCookiesNetscapeAsync();
            },
            QualityName = SelectedQuality?.Name ?? "",
            ProviderKey = "StreamingCommunity",
            AudioLanguages = string.Join("|", AudioTracks.Where(t => t.IsSelected).Select(t => t.Key)),
            SubtitleLanguages = string.Join("|", SubtitleTracks.Where(t => t.IsSelected).Select(t => t.Key)),
            BurnSubtitles = BurnSubtitles && SubtitleTracks.Any(t => t.IsSelected),

            ContentLink = contentLink,
            EpisodeId = episodeId,
            IsSeries = isSeries
        };

        var dummy = string.IsNullOrEmpty(videoUrl)
            ? "https://placeholder/episode.mp4"
            : videoUrl;
        item.DestinationPath = _fileNameBuilder.BuildDestinationPath(
            item, dummy, title, patternOverride: pattern);
        return item;
    }

    // ============================================================
    //  SELEZIONE EPISODI
    // ============================================================

    [RelayCommand]
    private void SelectAllEpisodes()
    {
        foreach (var ep in Episodes) ep.IsSelected = true;
        StatusMessage = $"Selezionati {Episodes.Count} episodi.";
    }

    [RelayCommand]
    private void DeselectAllEpisodes()
    {
        foreach (var ep in Episodes) ep.IsSelected = false;
        StatusMessage = "Deselezionati.";
    }

    [RelayCommand]
    private void InvertEpisodeSelection()
    {
        foreach (var ep in Episodes) ep.IsSelected = !ep.IsSelected;
        StatusMessage = "Selezione invertita.";
    }

    // ============================================================
    //  CONTROLLI GLOBALI CODA
    // ============================================================

    [RelayCommand]
    private void StartAll()
    {
        var count = _queueService.AllItems.Count(i =>
            i.Category == CategoryName &&
            (i.Status == DownloadStatus.Pending ||
             i.Status == DownloadStatus.Paused ||
             i.Status == DownloadStatus.Failed));

        if (count == 0) { StatusMessage = "Nessun download da avviare."; return; }
        _queueService.StartAll(CategoryName);
        StatusMessage = $"Avvio di {count} download.";
    }

    [RelayCommand]
    private void PauseAll()
    {
        _queueService.PauseAll(CategoryName);
        StatusMessage = "Download di film e serie in pausa.";
    }

    [RelayCommand]
    private void CancelAllDownloads()
    {
        _queueService.CancelAll(CategoryName);
        StatusMessage = "Annullati.";
    }

    [RelayCommand]
    private async Task DeleteEverythingAsync()
    {
        await _queueService.DeleteEverythingAsync(CategoryName);
        StatusMessage = "Eliminati.";
    }

    [RelayCommand]
    private void ClearCompleted()
    {
        _queueService.ClearCompleted(CategoryName);
        StatusMessage = "Rimossi completati.";
    }

    [RelayCommand]
    private void ClearCancelledDownloads()
    {
        _queueService.ClearCancelled(CategoryName);
        StatusMessage = "Rimossi annullati.";
    }

    [RelayCommand]
    private void ClearFailedDownloads()
    {
        _queueService.ClearFailed(CategoryName);
        StatusMessage = "Rimossi failed.";
    }

    // ============================================================
    //  METODI PUBBLICI (per code-behind)
    // ============================================================

    public void ResumeItemPublic(DownloadItem item) => _queueService.ResumeItem(item);
    public void ForceItemPublic(DownloadItem item) => _queueService.ForceQueueItem(item);
    public void UnforceItemPublic(DownloadItem item) => _queueService.UnforceQueueItem(item);
    public void CancelItemPublic(DownloadItem item) => _queueService.CancelItem(item);
    public Task RemoveItemPublicAsync(DownloadItem item) => _queueService.RemoveItemAsync(item);
    public Task RetryItemPublicAsync(DownloadItem item) => _queueService.RetryItemAsync(item);

    public void ResumeGroupPublic(SeriesGroup g)
    {
        foreach (var i in g.Items.ToList()) _queueService.ResumeItem(i);
    }

    public void ForceGroupPublic(SeriesGroup g) => _queueService.ForceQueueGroup(g);
    public void UnforceGroupPublic(SeriesGroup g) => _queueService.UnforceQueueGroup(g);
    public void CancelGroupPublic(SeriesGroup g) => _queueService.CancelGroup(g);

    public async Task RemoveGroupPublicAsync(SeriesGroup g)
    {
        foreach (var i in g.Items.ToList())
            await _queueService.RemoveItemAsync(i);
    }

    public Task RetryFailedGroupPublicAsync(SeriesGroup g)
        => _queueService.RetryFailedInGroupAsync(g);

    public Task RetryCancelledGroupPublicAsync(SeriesGroup g)
        => _queueService.RetryCancelledInGroupAsync(g);

    // ============================================================
    //  IMPOSTAZIONI
    // ============================================================

    [RelayCommand]
    private void SaveSettings()
    {
        _settings.Current.StreamingMovieNamingPattern = MovieNamingPattern;
        _settings.Current.StreamingSeriesNamingPattern = SeriesNamingPattern;
        _settings.Current.StreamingCreateSeriesFolder = CreateSeriesFolder;
        _settings.Current.StreamingBaseDownloadFolder = BaseDownloadFolder;
        _settings.Current.SeasonFolderPattern = SeasonFolderPattern;
        _settings.Current.MaxConcurrencyStreaming = MaxConcurrency;
        _settings.Save();
        StatusMessage = "Impostazioni salvate.";
    }

    public void SetBaseDownloadFolder(string folder)
    {
        _settings.Current.StreamingBaseDownloadFolder = folder;
        _settings.Save();
        OnPropertyChanged(nameof(BaseDownloadFolder));
    }
}