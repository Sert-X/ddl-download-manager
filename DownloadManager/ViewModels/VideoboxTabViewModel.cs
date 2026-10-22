using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DownloadManager.Helpers;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Sftp;
using DownloadManager.Services.Videobox;
using System.Text.Json;

namespace DownloadManager.ViewModels;

public enum MappingStatus { Unknown, Valid, Invalid, Mismatch }
public enum PostStatus { Unknown, Valid, Mixed, Invalid, Mismatch }

public partial class DomainItem : ObservableObject
{
    [ObservableProperty] private string url = string.Empty;
}

public partial class CategoryItem : ObservableObject
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Voce "Tutti i media" (Id 0): nella tendina ha un'icona diversa.</summary>
    public bool IsAll => Id == 0;
}

public partial class EpisodeMappingItem : ObservableObject
{
    public int EpisodeId { get; set; }
    [ObservableProperty] private string oldUrl = string.Empty;
    [ObservableProperty] private string? sftpFileName;
    [ObservableProperty] private string? newUrl;
    [ObservableProperty] private MappingStatus status = MappingStatus.Unknown;
    [ObservableProperty] private bool isSelectedForUpdate = true;

    public void EvaluateStatus(IEnumerable<string> validDomains)
    {
        if (string.IsNullOrEmpty(NewUrl))
            Status = LinkValidator.IsValidDomain(OldUrl, validDomains) ? MappingStatus.Valid : MappingStatus.Invalid;
        else
            Status = LinkValidator.IsValidDomain(NewUrl, validDomains) ? MappingStatus.Valid : MappingStatus.Invalid;
    }
}

public partial class VideoboxPostItem : ObservableObject
{
    public int Id { get; set; }
    [ObservableProperty] private string title = string.Empty;
    public string Url { get; set; } = string.Empty;
    public List<string> Links { get; set; } = new();
    public int LinkCount => Links.Count;

    /// <summary>Link video del post: server "Link Remoto" se c'è, altrimenti il primo con link.</summary>
    public static List<string> ExtractLinks(VideoboxDto dto)
    {
        var servers = dto.videos?.Values;
        var remote = servers?.FirstOrDefault(v => v.vbs_name != null && v.vbs_name.Contains("Link Remoto", StringComparison.OrdinalIgnoreCase))
                     ?? servers?.FirstOrDefault(v => v.online != null && v.online.Count > 0);
        return remote?.online?.Values.Select(e => e.vbo_url).ToList() ?? new List<string>();
    }

    /// <summary>Aggiorna titolo, indirizzo e link dai dati freschi del sito. Ritorna true se è cambiato qualcosa.</summary>
    public bool UpdateFrom(VideoboxDto dto)
    {
        var links = ExtractLinks(dto);
        if (Title == dto.title && Url == dto.url && Links.SequenceEqual(links)) return false;

        Title = dto.title;
        Url = dto.url;
        Links = links;
        OnPropertyChanged(nameof(Links));
        OnPropertyChanged(nameof(LinkCount));
        return true;
    }

    public int ValidLinkCount { get; set; }
    public PostStatus BaseStatus { get; set; } = PostStatus.Unknown;

    [ObservableProperty] private PostStatus status = PostStatus.Unknown;
}

public partial class VideoboxTabViewModel : ObservableObject
{
    private readonly VideoboxApiService _apiService;
    private readonly ISftpService _sftpService;
    private readonly SettingsService _settingsService;
    private SharedState _shared = null!;

    private int _currentPage = 1;
    private List<VideoboxPostItem> _allPosts = new();
    private const int PageSize = 50;

    /// <summary>Chiavi dei link validi dei post, per sorgente (Id sorgente → chiavi relative).</summary>
    private readonly Dictionary<string, HashSet<string>> _validKeysBySource = new();
    private readonly VideoboxSftpPool _pool;

    private int _currentPostAuthorId = 0;
    private string _currentServerId = "5";
    private int _currentPostOriginalCategoryId = 0;

    // Aggiornamento automatico dell'albero SFTP
    private static readonly TimeSpan TreeRefreshDebounce = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan TreePollInterval = TimeSpan.FromSeconds(60);
    private readonly DispatcherTimer _treePollTimer;
    /// <summary>Contatore dei caricamenti completi: un refresh partito prima di un "Carica" viene scartato.</summary>
    private int _treeGeneration;

    private static readonly TimeSpan PostsRefreshInterval = TimeSpan.FromSeconds(90);
    private readonly DispatcherTimer _postsRefreshTimer;
    private bool _refreshingPosts;
    private CancellationTokenSource? _treeRefreshDebounceCts;
    private CancellationTokenSource? _fullTreeScanCts;
    private bool _isRefreshingTree;
    /// <summary>Cartelle da riallineare (path → ricorsivo). Accesso solo dal thread UI.</summary>
    private readonly Dictionary<string, bool> _pendingTreeDirs = new(StringComparer.Ordinal);
    private bool _pendingFullTreeScan;

    [ObservableProperty] private string configIpsBaseUrl = string.Empty;
    [ObservableProperty] private string configIpsApiKey = string.Empty;
    // === Sorgenti SFTP (più server e più cartelle) ===
    public ObservableCollection<VideoboxSource> Sources { get; } = new();
    public ObservableCollection<SftpProfileOption> SftpProfiles { get; } = new();
    public bool HasSources => Sources.Count > 0;
    public string SourcesSelectionText
    {
        get
        {
            int on = Sources.Count(x => x.IsEnabled);
            return Sources.Count == 0 ? "Nessuna sorgente" : $"Sorgenti: {on} di {Sources.Count}";
        }
    }
    private bool _applyingCategorySources;

    public ObservableCollection<DomainItem> SecondaryDomains { get; } = new();
    public bool HasSecondaryDomains => SecondaryDomains.Count > 0;
    
    // CATEGORIE
    public ObservableCollection<CategoryItem> AvailableCategories { get; } = new();
    [ObservableProperty] private CategoryItem? selectedCategory;

    [ObservableProperty] private ObservableCollection<VideoboxPostItem> categoryPosts = new();
    [ObservableProperty] private VideoboxPostItem? selectedPost;
    // Filtro per colore dei post (ogni colore si mostra o nasconde a parte)
    [ObservableProperty] private bool showValidPosts = true;
    [ObservableProperty] private bool showMixedPosts = true;
    [ObservableProperty] private bool showInvalidPosts = true;
    [ObservableProperty] private bool showMismatchPosts = true;
    [ObservableProperty] private bool showUnknownPosts = true;
    [ObservableProperty] private int countValidPosts;
    [ObservableProperty] private int countMixedPosts;
    [ObservableProperty] private int countInvalidPosts;
    [ObservableProperty] private int countMismatchPosts;
    [ObservableProperty] private int countUnknownPosts;
    private bool _suspendFilter;
    [ObservableProperty] private string totalPostsText = "Totale post: 0";
    [ObservableProperty] private string searchQuery = string.Empty;

    public ObservableCollection<int> PageNumbers { get; } = new();
    [ObservableProperty] private int currentPageDisplay = 1;

    [ObservableProperty] private ObservableCollection<SftpNode> sftpRootNodes = new();
    [ObservableProperty] private SftpNode? selectedSftpNode;
    [ObservableProperty] private string sftpPathInput = string.Empty;
    [ObservableProperty] private bool isSftpConnected = false;

    [ObservableProperty] private ObservableCollection<EpisodeMappingItem> mappingItems = new();
    /// <summary>Riga della mappatura selezionata: destinazione della mappatura di un singolo file.</summary>
    [ObservableProperty] private EpisodeMappingItem? selectedMappingItem;
    [ObservableProperty] private int currentPostId;
    [ObservableProperty] private string currentPostTitle = "Nessun post caricato";
    [ObservableProperty] private string statusMessage = "Pronto.";
    [ObservableProperty] private bool isBusy;

    // === Caricamenti annullabili (post / albero SFTP) ===
    // Durante il caricamento i pulsanti "Carica" lasciano il posto a avanzamento + Stop.
    [ObservableProperty] private bool isLoadingPosts;
    [ObservableProperty] private string postsProgressText = string.Empty;
    [ObservableProperty] private bool isLoadingTree;
    [ObservableProperty] private string treeProgressText = string.Empty;
    private CancellationTokenSource? _postsCts;
    private CancellationTokenSource? _treeCts;
    private int _treeFoldersRead;

    [RelayCommand]
    private void StopLoadingPosts()
    {
        _postsCts?.Cancel();
        PostsProgressText = "Interruzione...";
    }

    [RelayCommand]
    private void StopLoadingTree()
    {
        _treeCts?.Cancel();
        TreeProgressText = "Interruzione...";
    }

    public VideoboxTabViewModel(VideoboxApiService apiService, ISftpService sftpService, SettingsService settingsService,
        SftpUploadQueueService uploadQueue, VideoboxSftpPool pool)
    {
        _apiService = apiService;
        _sftpService = sftpService;
        _settingsService = settingsService;
        _pool = pool;

        // Modifiche fatte dall'app (tab SFTP, organizer, upload) → refresh immediato (con debounce).
        // Si aggiorna solo la cartella toccata, non tutto l'albero.
        _sftpService.RemoteChanged += change => EnqueueTreeRefresh(change.DirectoryPath, change.Recursive);
        uploadQueue.JobCompleted += job => EnqueueTreeRefresh(job.DestinationFolder, false);

        // Modifiche esterne (altri client, server) → scansione completa periodica.
        _treePollTimer = new DispatcherTimer { Interval = TreePollInterval };
        _treePollTimer.Tick += (_, _) =>
        {
            _pendingFullTreeScan = true;
            _ = ProcessTreeRefreshAsync();
        };
        _treePollTimer.Start();

        // Post modificati sul sito (titolo, link...): ogni tanto si rileggono quelli
        // visibili nella pagina e si aggiornano sul posto, senza ricaricare la lista.
        _postsRefreshTimer = new DispatcherTimer { Interval = PostsRefreshInterval };
        _postsRefreshTimer.Tick += async (_, _) => await RefreshVisiblePostsAsync();
        _postsRefreshTimer.Start();

        var current = _settingsService.Current;
        ConfigIpsBaseUrl = current.IpsBaseUrl;
        ConfigIpsApiKey = current.IpsApiKey;
        LoadSources();
        _ = LoadProfilesAsync();

        if (!string.IsNullOrWhiteSpace(current.SecondaryStorageUrlsRaw))
        {
            foreach (var d in current.SecondaryStorageUrlsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                SecondaryDomains.Add(new DomainItem { Url = d.Trim() });
        }

        SecondaryDomains.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSecondaryDomains));

        // Inizializza l'opzione "Tutti"
        AvailableCategories.Add(new CategoryItem { Id = 0, Name = "Tutti i media" });
        SelectedCategory = AvailableCategories[0];
        _ = LoadCategoriesAsync();
    }

    public void Initialize(SharedState shared)
    {
        _shared = shared;
        IsSftpConnected = _shared.IsSftpConnected;

        _shared.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(SharedState.IsSftpConnected))
            {
                IsSftpConnected = _shared.IsSftpConnected;
                _ = LoadProfilesAsync();   // i profili salvati possono essere cambiati nella tab SFTP
            }
        };
    }

    /// <summary>Sorgenti dalle impostazioni; la prima volta si crea dalla vecchia configurazione singola.</summary>
    private void LoadSources()
    {
        var current = _settingsService.Current;
        if (current.VideoboxSources.Count == 0 && !string.IsNullOrWhiteSpace(current.SftpBasePath))
        {
            current.VideoboxSources.Add(new VideoboxSourceConfig
            {
                Name = string.IsNullOrWhiteSpace(current.VideoboxPublicPath) ? "Principale" : current.VideoboxPublicPath,
                ProfileId = VideoboxSftpPool.SharedProfileId,
                RootPath = current.SftpBasePath,
                Domain = current.PrimaryStorageUrl,
                PublicPath = current.VideoboxPublicPath
            });
            _settingsService.Save();
        }

        foreach (var src in Sources) src.PropertyChanged -= OnSourcePropertyChanged;
        Sources.Clear();
        foreach (var c in current.VideoboxSources)
        {
            var src = VideoboxSource.FromConfig(c);
            src.PropertyChanged += OnSourcePropertyChanged;
            Sources.Add(src);
        }
        SyncSourceProfiles();
        ApplyCategorySources();
        NotifySourcesChanged();
    }

    private async Task LoadProfilesAsync()
    {
        var options = await _pool.GetProfileOptionsAsync();
        if (!options.SequenceEqual(SftpProfiles))
        {
            SftpProfiles.Clear();
            foreach (var o in options) SftpProfiles.Add(o);
        }
        SyncSourceProfiles();
    }

    /// <summary>Riporta nel menu "Connessione SFTP" di ogni sorgente il profilo salvato.</summary>
    private void SyncSourceProfiles()
    {
        foreach (var src in Sources) SyncSourceProfile(src);
    }

    private void SyncSourceProfile(VideoboxSource src)
    {
        var match = SftpProfiles.FirstOrDefault(p => p.Id == src.ProfileId);
        // Prima null: un profilo uguale (record) non notificherebbe e il menu resterebbe vuoto
        src.Profile = null;
        src.Profile = match;
    }

    private void OnSourcePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideoboxSource.IsEnabled) && !_applyingCategorySources)
            SaveCategorySources();
        NotifySourcesChanged();
    }

    private void NotifySourcesChanged()
    {
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(SourcesSelectionText));
    }

    [RelayCommand]
    private void AddSource()
    {
        var src = new VideoboxSource
        {
            Name = $"Sorgente {Sources.Count + 1}",
            ProfileId = VideoboxSftpPool.SharedProfileId,
            Domain = Sources.FirstOrDefault()?.Domain ?? _settingsService.Current.PrimaryStorageUrl
        };
        src.PropertyChanged += OnSourcePropertyChanged;
        Sources.Add(src);
        SyncSourceProfile(src);
        NotifySourcesChanged();
    }

    [RelayCommand]
    private void RemoveSource(VideoboxSource source)
    {
        source.PropertyChanged -= OnSourcePropertyChanged;
        Sources.Remove(source);
        NotifySourcesChanged();
    }

    [RelayCommand] private void EnableAllSources() { foreach (var x in Sources) x.IsEnabled = true; }
    [RelayCommand] private void EnableNoSources() { foreach (var x in Sources) x.IsEnabled = false; }

    /// <summary>Sorgenti da caricare per la categoria selezionata (tutte se non c'è una scelta salvata).</summary>
    private void ApplyCategorySources()
    {
        var key = (SelectedCategory?.Id ?? 0).ToString();
        _applyingCategorySources = true;
        try
        {
            var saved = _settingsService.Current.VideoboxCategorySources.TryGetValue(key, out var ids) ? ids : null;
            foreach (var src in Sources) src.IsEnabled = saved == null || saved.Contains(src.Id);
        }
        finally { _applyingCategorySources = false; }
        NotifySourcesChanged();
    }

    private void SaveCategorySources()
    {
        var key = (SelectedCategory?.Id ?? 0).ToString();
        _settingsService.Current.VideoboxCategorySources[key] = Sources.Where(x => x.IsEnabled).Select(x => x.Id).ToList();
        _settingsService.Save();
    }

    partial void OnSelectedCategoryChanged(CategoryItem? value) => ApplyCategorySources();

    [RelayCommand] public void AddSecondaryDomain() => SecondaryDomains.Add(new DomainItem());
    [RelayCommand] public void RemoveSecondaryDomain(DomainItem domain) => SecondaryDomains.Remove(domain);

    [RelayCommand]
    public async Task LoadCategoriesAsync()
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Caricamento categorie...";
            var cats = await _apiService.GetCategoriesAsync();
            
            var selectedId = SelectedCategory?.Id ?? 0;
            AvailableCategories.Clear();
            
            AvailableCategories.Add(new CategoryItem { Id = 0, Name = "Tutti i media" });
            
            // FILTRO: Solo "IPS\\videobox\\Category" e "perm_2" = "*"
            var filteredCats = cats
                .Where(c => c.@class == "IPS\\videobox\\Category" && c.permissions?.perm_2 == "*")
                .OrderBy(x => x.name);

            foreach (var c in filteredCats)
            {
                AvailableCategories.Add(new CategoryItem { Id = c.id, Name = c.name });
            }

            SelectedCategory = AvailableCategories.FirstOrDefault(c => c.Id == selectedId) ?? AvailableCategories[0];
            StatusMessage = $"Caricate {filteredCats.Count()} categorie.";
        }
        catch (Exception ex) { StatusMessage = $"Errore categorie: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    public void SaveSettings()
    {
        var current = _settingsService.Current;
        current.IpsBaseUrl = ConfigIpsBaseUrl.Trim();
        current.IpsApiKey = ConfigIpsApiKey.Trim();
        current.VideoboxSources = Sources.Select(x => x.ToConfig()).ToList();
        // Compatibilità: i vecchi campi seguono la prima sorgente
        if (Sources.FirstOrDefault() is { } first)
        {
            current.PrimaryStorageUrl = first.Domain.Trim();
            current.VideoboxPublicPath = first.PublicPath.Trim().Trim('/');
            current.SftpBasePath = first.RootPath.Trim();
        }
        current.SecondaryStorageUrlsRaw = string.Join(",", SecondaryDomains.Select(d => d.Url).Where(u => !string.IsNullOrWhiteSpace(u)));

        _settingsService.Save();
        UpdateSftpTreeColors(); 
        StatusMessage = "Impostazioni salvate e colori aggiornati!";
    }

    partial void OnShowValidPostsChanged(bool value) => OnStatusFilterChanged();
    partial void OnShowMixedPostsChanged(bool value) => OnStatusFilterChanged();
    partial void OnShowInvalidPostsChanged(bool value) => OnStatusFilterChanged();
    partial void OnShowMismatchPostsChanged(bool value) => OnStatusFilterChanged();
    partial void OnShowUnknownPostsChanged(bool value) => OnStatusFilterChanged();

    private void OnStatusFilterChanged()
    {
        if (_suspendFilter) return;
        _currentPage = 1;
        ApplyFilterAndPaginate();
    }

    private bool IsStatusShown(PostStatus status) => status switch
    {
        PostStatus.Valid => ShowValidPosts,
        PostStatus.Mixed => ShowMixedPosts,
        PostStatus.Invalid => ShowInvalidPosts,
        PostStatus.Mismatch => ShowMismatchPosts,
        _ => ShowUnknownPosts
    };

    /// <summary>Solo i post da sistemare: nasconde i verdi.</summary>
    [RelayCommand]
    private void ShowPostsToFix() => SetStatusFilter(valid: false, mixed: true, invalid: true, mismatch: true, unknown: true);

    [RelayCommand]
    private void ShowAllPosts() => SetStatusFilter(true, true, true, true, true);

    private void SetStatusFilter(bool valid, bool mixed, bool invalid, bool mismatch, bool unknown)
    {
        _suspendFilter = true;
        ShowValidPosts = valid; ShowMixedPosts = mixed; ShowInvalidPosts = invalid;
        ShowMismatchPosts = mismatch; ShowUnknownPosts = unknown;
        _suspendFilter = false;
        OnStatusFilterChanged();
    }

    /// <summary>Numero di post per colore (tra quelli che corrispondono alla ricerca).</summary>
    private void UpdateStatusCounts()
    {
        var posts = string.IsNullOrWhiteSpace(SearchQuery)
            ? _allPosts
            : _allPosts.Where(p => p.Title.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)).ToList();
        CountValidPosts = posts.Count(p => p.Status == PostStatus.Valid);
        CountMixedPosts = posts.Count(p => p.Status == PostStatus.Mixed);
        CountInvalidPosts = posts.Count(p => p.Status == PostStatus.Invalid);
        CountMismatchPosts = posts.Count(p => p.Status == PostStatus.Mismatch);
        CountUnknownPosts = posts.Count(p => p.Status == PostStatus.Unknown);
    }
    partial void OnSearchQueryChanged(string value) { _currentPage = 1; ApplyFilterAndPaginate(); }

    private void ApplyFilterAndPaginate()
    {
        var filtered = _allPosts.AsEnumerable();

        filtered = filtered.Where(p => IsStatusShown(p.Status));

        if (!string.IsNullOrWhiteSpace(SearchQuery))
            filtered = filtered.Where(p => p.Title.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));

        var filteredList = filtered.ToList();

        int totalPages = (int)Math.Ceiling((double)filteredList.Count / PageSize);
        PageNumbers.Clear();
        for (int i = 1; i <= totalPages; i++) PageNumbers.Add(i);

        if (_currentPage > totalPages) _currentPage = Math.Max(1, totalPages);
        CurrentPageDisplay = _currentPage;

        CategoryPosts.Clear();
        var pageItems = filteredList.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToList();
        foreach (var item in pageItems) CategoryPosts.Add(item);

        int hiddenCount = _allPosts.Count - filteredList.Count;
        TotalPostsText = $"Mostrati: {filteredList.Count} | Nascosti: {hiddenCount} | Totale: {_allPosts.Count}";
        UpdateStatusCounts();
    }

    [RelayCommand]
    public void OpenPostUrl(VideoboxPostItem post)
    {
        if (!string.IsNullOrEmpty(post.Url)) Process.Start(new ProcessStartInfo(post.Url) { UseShellExecute = true });
    }

    [RelayCommand]
    public async Task LoadCategoryAsync()
    {
        if (IsLoadingPosts) return;
        using var cts = _postsCts = new CancellationTokenSource();
        try
        {
            IsBusy = true;
            IsLoadingPosts = true;
            PostsProgressText = "Caricamento pagina 1...";
            _allPosts.Clear();
            var pagedData = await _apiService.GetVideosByCategoryAsync(SelectedCategory?.Id, 1, cts.Token);
            ProcessPagedData(pagedData);
            _currentPage = 1;
            UpdateSftpTreeColors();
            StatusMessage = $"Caricata pagina 1.";
        }
        // Solo Stop è un annullamento: un timeout della rete arriva anch'esso come
        // OperationCanceledException, ma va segnalato come errore.
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { StatusMessage = "Caricamento dei post annullato."; }
        catch (Exception ex) { StatusMessage = $"Errore: {ex.Message}"; }
        finally
        {
            _postsCts = null;
            IsLoadingPosts = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task LoadAllCategoryAsync()
    {
        if (IsLoadingPosts) return;
        using var cts = _postsCts = new CancellationTokenSource();
        try
        {
            IsBusy = true;
            IsLoadingPosts = true;
            PostsProgressText = "Caricamento pagina 1...";
            _allPosts.Clear();
            var firstPage = await _apiService.GetVideosByCategoryAsync(SelectedCategory?.Id, 1, cts.Token);
            ProcessPagedData(firstPage);

            for (int i = 2; i <= firstPage.totalPages; i++)
            {
                PostsProgressText = $"Pagina {i} di {firstPage.totalPages} · {_allPosts.Count} post";
                StatusMessage = $"Caricamento pagina {i} di {firstPage.totalPages}...";
                await Task.Delay(250, cts.Token);
                var pageData = await _apiService.GetVideosByCategoryAsync(SelectedCategory?.Id, i, cts.Token);
                ProcessPagedData(pageData);
            }

            _currentPage = 1;
            UpdateSftpTreeColors();
            StatusMessage = $"Caricati tutti i {_allPosts.Count} post.";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Si tengono i post già scaricati: utili anche se il caricamento è stato fermato
            _currentPage = 1;
            UpdateSftpTreeColors();
            StatusMessage = $"Caricamento fermato: {_allPosts.Count} post caricati.";
        }
        catch (Exception ex) { StatusMessage = $"Errore di rete: {ex.Message}"; }
        finally
        {
            _postsCts = null;
            IsLoadingPosts = false;
            IsBusy = false;
        }
    }

    private void ProcessPagedData(VideoboxPagedDto pagedData)
    {
        foreach (var post in pagedData.results)
        {
            _allPosts.Add(new VideoboxPostItem
            {
                Id = post.id, Title = post.title, Url = post.url, Links = VideoboxPostItem.ExtractLinks(post)
            });
        }
    }

    [RelayCommand]
    public void GoToPage(int page)
    {
        _currentPage = page;
        ApplyFilterAndPaginate();
    }

    private PostStatus GetBasePostStatus(List<string> urls, int validCount)
    {
        if (urls.Count == 0) return PostStatus.Unknown;
        if (validCount == 0) return PostStatus.Invalid;
        if (validCount < urls.Count) return PostStatus.Mixed;
        return PostStatus.Valid;
    }

    partial void OnSelectedPostChanged(VideoboxPostItem? value)
    {
        if (value != null) _ = LoadPostDetailsAsync(value.Id);
    }

    private async Task LoadPostDetailsAsync(int postId)
    {
        try
        {
            IsBusy = true;
            StatusMessage = $"Caricamento post {postId}...";
            var postData = await _apiService.GetVideoByIdAsync(postId);
            ApplyFreshPostData(postData);
            CurrentPostId = postData.id;
            CurrentPostTitle = postData.title;
            _currentPostAuthorId = postData.author?.id ?? 0;
            _currentPostOriginalCategoryId = ParseCategoryId(postData.category);

            var servers = postData.videos?.Values;
            var remoteServer = servers?.FirstOrDefault(v => v.vbs_name != null && v.vbs_name.Contains("Link Remoto", StringComparison.OrdinalIgnoreCase)) ?? servers?.FirstOrDefault(v => v.online != null && v.online.Count > 0);
            
            if (remoteServer?.online == null || remoteServer.online.Count == 0) { StatusMessage = "Nessun link trovato."; return; }
            _currentServerId = remoteServer.id.ToString();

            var episodes = remoteServer.online.Values.OrderBy(e => e.vbo_id).ToList();
            MappingItems.Clear();
            foreach (var ep in episodes)
            {
                var item = new EpisodeMappingItem { EpisodeId = ep.vbo_id, OldUrl = ep.vbo_url };
                item.EvaluateStatus(_settingsService.Current.AllValidStorageDomains);
                MappingItems.Add(item);
            }

            StatusMessage = $"Caricati {MappingItems.Count} episodi.";
        }
        catch (Exception ex) { StatusMessage = $"Errore: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>Aggiorna nella lista il post con i dati appena letti dal sito.</summary>
    private bool ApplyFreshPostData(VideoboxDto dto, bool recolor = true)
    {
        var item = _allPosts.FirstOrDefault(p => p.Id == dto.id);
        if (item == null || !item.UpdateFrom(dto)) return false;

        if (item.Id == CurrentPostId) CurrentPostTitle = dto.title;
        if (recolor) UpdateSftpTreeColors(repaginate: false);
        return true;
    }

    /// <summary>
    /// Rilegge dal sito i post visibili e aggiorna sul posto quelli modificati (l'API non
    /// dice cosa è cambiato). Per non pesare sul forum si rileggono le PAGINE dell'API che
    /// li contengono (50 post per richiesta, di solito una sola), non un post alla volta.
    /// </summary>
    private async Task RefreshVisiblePostsAsync()
    {
        if (_refreshingPosts || IsLoadingPosts || IsBusy || CategoryPosts.Count == 0) return;
        _refreshingPosts = true;
        try
        {
            // L'elenco locale segue l'ordine dell'API a blocchi da PageSize: dalla posizione
            // di un post nella lista completa si ricava la pagina dell'API che lo contiene.
            var pages = CategoryPosts
                .Select(post => _allPosts.IndexOf(post))
                .Where(i => i >= 0)
                .Select(i => i / PageSize + 1)
                .Distinct()
                .Take(3)
                .ToList();

            int changed = 0;
            foreach (var page in pages)
            {
                VideoboxPagedDto data;
                try { data = await _apiService.GetVideosByCategoryAsync(SelectedCategory?.Id, page); }
                catch { return; }   // forum lento o non raggiungibile: si riprova al prossimo giro

                foreach (var dto in data.results)
                    if (ApplyFreshPostData(dto, recolor: false)) changed++;
            }

            if (changed > 0)
            {
                UpdateSftpTreeColors(repaginate: false);
                StatusMessage = $"{changed} post aggiornati dal sito ({DateTime.Now:HH:mm}).";
            }
        }
        finally { _refreshingPosts = false; }
    }

    [RelayCommand]
    public async Task LoadSftpTreeAsync()
    {
        var sources = Sources.Where(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.RootPath)).ToList();
        if (sources.Count == 0) { StatusMessage = "Nessuna sorgente attiva: aggiungile in Impostazioni e Domini o attivale dal menu Sorgenti."; return; }
        if (IsLoadingTree) return;
        using var cts = _treeCts = new CancellationTokenSource();
        try
        {
            IsBusy = true;
            IsLoadingTree = true;
            _treeFoldersRead = 0;
            TreeProgressText = "Connessione alle sorgenti...";
            // Un aggiornamento automatico in corso scriverebbe nella lista mentre la si
            // ricarica (era la causa dei doppioni): lo si invalida e si interrompe.
            _treeGeneration++;
            _fullTreeScanCts?.Cancel();
            _pendingTreeDirs.Clear();
            _pendingFullTreeScan = false;

            StatusMessage = $"Caricamento di {sources.Count} sorgenti...";
            SftpRootNodes.Clear();

            // Le sorgenti si leggono in parallelo (quelle sullo stesso server condividono la connessione)
            var roots = await Task.WhenAll(sources.Select(src => LoadSourceRootAsync(src, cts.Token)));
            cts.Token.ThrowIfCancellationRequested();

            SftpRootNodes.Clear();   // la lista contiene solo l'albero appena letto
            foreach (var root in roots) SftpRootNodes.Add(root);

            UpdateSftpTreeColors();
            int failed = roots.Count(r => r.HasLoadError);
            StatusMessage = failed == 0
                ? $"Albero caricato: {sources.Count} sorgenti, {_treeFoldersRead} cartelle (aggiornamento automatico attivo)."
                : $"Albero caricato con {failed} sorgenti non raggiungibili su {sources.Count} (vedi l'albero).";
        }
        catch (OperationCanceledException)
        {
            SftpRootNodes.Clear();   // un albero a metà darebbe colori sbagliati
            StatusMessage = "Caricamento dell'albero SFTP annullato.";
        }
        catch (Exception ex) { StatusMessage = $"Errore SFTP: {ex.Message}"; }
        finally
        {
            _treeCts = null;
            IsLoadingTree = false;
            IsBusy = false;
        }
    }

    /// <summary>Radice dell'albero per una sorgente; se il server non risponde, radice vuota con l'errore nel nome.</summary>
    private async Task<SftpNode> LoadSourceRootAsync(VideoboxSource source, CancellationToken ct)
    {
        var profile = SftpProfiles.FirstOrDefault(p => p.Id == source.ProfileId)?.Name ?? "SFTP";
        var root = new SftpNode
        {
            Name = $"{source.DisplayName}  ·  {profile}",
            FullPath = source.RootPath,
            IsDirectory = true,
            Source = source,
            IsSourceRoot = true
        };
        try
        {
            var service = await _pool.GetAsync(source.ProfileId, ct);
            foreach (var child in await BuildTreeRecursiveAsync(service, source, source.RootPath, root, ct))
                root.Children.Add(child);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            root.HasLoadError = true;
            root.Name += $" — non raggiungibile: {FirstLine(ex.Message)}";
        }
        return root;
    }

    /// <summary>Prima riga di un messaggio d'errore (quelli del server SFTP sono su più righe).</summary>
    private static string FirstLine(string message) =>
        message.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().TrimEnd('.') ?? message;

    /// <summary>Accoda il riallineamento di una cartella remota (dalla connessione della tab SFTP). Thread-safe.</summary>
    private void EnqueueTreeRefresh(string directoryPath, bool recursive)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var path = NormalizeRemotePath(directoryPath);
            _pendingTreeDirs[path] = recursive || (_pendingTreeDirs.TryGetValue(path, out var r) && r);

            // Una modifica nota ha la precedenza sulla scansione completa (lenta):
            // la interrompiamo, ripartirà al prossimo giro del timer.
            _fullTreeScanCts?.Cancel();

            ScheduleTreeRefresh();
        });
    }

    /// <summary>Avvia il refresh dopo un breve debounce, raggruppando eventi ravvicinati. Solo thread UI.</summary>
    private async void ScheduleTreeRefresh()
    {
        _treeRefreshDebounceCts?.Cancel();
        var cts = _treeRefreshDebounceCts = new CancellationTokenSource();
        try { await Task.Delay(TreeRefreshDebounce, cts.Token); }
        catch (OperationCanceledException) { return; }
        await ProcessTreeRefreshAsync();
    }

    /// <summary>Radici dell'albero caricate e leggibili (sorgente con almeno il suo contenuto).</summary>
    private List<SftpNode> LoadedSourceRoots() =>
        SftpRootNodes.Where(r => r.IsSourceRoot && !r.HasLoadError && r.Source != null && _pool.IsConnected(r.Source.ProfileId)).ToList();

    /// <summary>
    /// Esegue i refresh in coda fondendo i risultati nell'albero esistente,
    /// così le cartelle espanse e la selezione restano invariate.
    /// </summary>
    private async Task ProcessTreeRefreshAsync()
    {
        if (SftpRootNodes.Count == 0)
        {
            _pendingTreeDirs.Clear();
            _pendingFullTreeScan = false;
            return;
        }
        if (_isRefreshingTree || IsLoadingTree) return; // il giro in corso riprocessa la coda prima di finire
        if (IsBusy) { ScheduleTreeRefresh(); return; }

        _isRefreshingTree = true;
        bool changed = false;
        try
        {
            while (_pendingTreeDirs.Count > 0 || _pendingFullTreeScan)
            {
                if (_pendingTreeDirs.Count > 0)
                {
                    var dirs = _pendingTreeDirs.ToList();
                    _pendingTreeDirs.Clear();
                    foreach (var (path, recursive) in dirs)
                    {
                        // Le modifiche arrivano dalla tab SFTP: riguardano le sorgenti sullo stesso
                        // server (anche con un profilo dedicato) che contengono quel percorso.
                        foreach (var root in LoadedSourceRoots().Where(r => _pool.SharesServerWithTab(r.Source!.ProfileId)))
                            changed |= await RefreshTreeDirectoryAsync(root, path, recursive, _treeGeneration);
                        if (IsLoadingTree) return;
                    }
                    continue;
                }

                _pendingFullTreeScan = false;
                var scanCts = _fullTreeScanCts = new CancellationTokenSource();
                try
                {
                    int generation = _treeGeneration;
                    foreach (var root in LoadedSourceRoots())
                    {
                        var source = root.Source!;
                        try
                        {
                            var service = await _pool.GetAsync(source.ProfileId, scanCts.Token);
                            var fresh = await BuildTreeRecursiveAsync(service, source, source.RootPath, root, scanCts.Token);
                            if (generation != _treeGeneration || IsLoadingTree) return;   // nel frattempo "Carica"
                            changed |= MergeNodes(root.Children, fresh, root, recurse: true);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            StatusMessage = $"Aggiornamento di '{source.DisplayName}' non riuscito: {FirstLine(ex.Message)}";
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally { _fullTreeScanCts = null; scanCts.Dispose(); }
            }

            if (changed)
            {
                if (SelectedSftpNode != null && !IsInTree(SelectedSftpNode)) SelectedSftpNode = null;
                UpdateSftpTreeColors(repaginate: false);
                StatusMessage = $"Albero SFTP aggiornato ({DateTime.Now:HH:mm:ss}).";
            }
        }
        catch (Exception ex) { StatusMessage = $"Aggiornamento albero SFTP fallito: {FirstLine(ex.Message)}"; }
        finally { _isRefreshingTree = false; }
    }

    /// <summary>
    /// Riallinea una singola cartella dentro la radice di una sorgente. Se non è (ancora)
    /// nell'albero si riallinea l'antenato più vicino presente. Ritorna true se è cambiato qualcosa.
    /// </summary>
    private async Task<bool> RefreshTreeDirectoryAsync(SftpNode root, string path, bool recursive, int generation)
    {
        var source = root.Source!;
        var basePath = NormalizeRemotePath(source.RootPath);
        if (path != basePath && !path.StartsWith(basePath.TrimEnd('/') + "/", StringComparison.Ordinal))
            return false; // fuori dalla cartella della sorgente: non la riguarda

        // Scende nell'albero fin dove la cartella esiste.
        SftpNode node = root;
        var relative = path.Length > basePath.Length ? path.Substring(basePath.Length).Trim('/') : string.Empty;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = node.Children.FirstOrDefault(c => c.IsDirectory && c.Name == segment);
            if (child == null) { recursive = false; break; } // le cartelle nuove vengono comunque lette per intero
            node = child;
        }

        var service = await _pool.GetAsync(source.ProfileId);
        if (recursive)
        {
            var fresh = await BuildTreeRecursiveAsync(service, source, node.FullPath, node);
            if (generation != _treeGeneration || IsLoadingTree) return false;
            return MergeNodes(node.Children, fresh, node, recurse: true);
        }

        // Solo il contenuto diretto; si leggono per intero solo le sottocartelle nuove.
        var nodes = new List<SftpNode>();
        foreach (var entry in await ListSortedEntriesAsync(service, node.FullPath))
        {
            var newNode = new SftpNode { Name = entry.Name, FullPath = entry.FullPath, IsDirectory = entry.IsDirectory, Parent = node, Source = source };
            bool isNewDir = entry.IsDirectory && !node.Children.Any(c => c.IsDirectory && c.Name == entry.Name);
            if (isNewDir)
                foreach (var child in await BuildTreeRecursiveAsync(service, source, entry.FullPath, newNode)) newNode.Children.Add(child);
            nodes.Add(newNode);
        }
        if (generation != _treeGeneration || IsLoadingTree) return false;
        return MergeNodes(node.Children, nodes, node, recurse: false);
    }

    private static string NormalizeRemotePath(string path)
    {
        var p = path.Replace('\\', '/');
        while (p.Contains("//")) p = p.Replace("//", "/");
        p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    /// <summary>
    /// Aggiorna <paramref name="target"/> sul posto perché corrisponda a <paramref name="fresh"/>.
    /// Con <paramref name="recurse"/> false le sottocartelle già presenti non vengono toccate.
    /// Ritorna true se è cambiato qualcosa.
    /// </summary>
    private static bool MergeNodes(ObservableCollection<SftpNode> target, List<SftpNode> fresh, SftpNode? parent, bool recurse)
    {
        bool changed = false;
        var freshKeys = fresh.Select(n => (n.Name, n.IsDirectory)).ToHashSet();

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!freshKeys.Contains((target[i].Name, target[i].IsDirectory))) { target.RemoveAt(i); changed = true; }
        }

        for (int i = 0; i < fresh.Count; i++)
        {
            var freshNode = fresh[i];
            int existingIdx = -1;
            for (int j = i; j < target.Count; j++)
            {
                if (target[j].Name == freshNode.Name && target[j].IsDirectory == freshNode.IsDirectory) { existingIdx = j; break; }
            }

            if (existingIdx < 0)
            {
                freshNode.Parent = parent;
                target.Insert(i, freshNode);
                changed = true;
                continue;
            }

            if (existingIdx != i) { target.Move(existingIdx, i); changed = true; }

            var existing = target[i];
            if (recurse && existing.IsDirectory)
                changed |= MergeNodes(existing.Children, freshNode.Children.ToList(), existing, recurse: true);
        }
        return changed;
    }

    private bool IsInTree(SftpNode node)
    {
        var current = node;
        while (current.Parent != null)
        {
            if (!current.Parent.Children.Contains(current)) return false;
            current = current.Parent;
        }
        return SftpRootNodes.Contains(current);
    }

    private async Task<List<SftpRemoteEntry>> ListSortedEntriesAsync(ISftpService service, string path, CancellationToken ct = default)
    {
        var entries = await service.ListDirectoryAsync(path, ct);
        if (IsLoadingTree)
        {
            _treeFoldersRead++;
            TreeProgressText = $"{_treeFoldersRead} cartelle lette";
        }
        return entries
            .Where(e => !e.Name.StartsWith("."))
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, new NaturalSortComparer())
            .ToList();
    }

    private async Task<List<SftpNode>> BuildTreeRecursiveAsync(ISftpService service, VideoboxSource source, string path, SftpNode? parent, CancellationToken ct = default)
    {
        var nodes = new List<SftpNode>();
        foreach (var entry in await ListSortedEntriesAsync(service, path, ct))
        {
            var node = new SftpNode { Name = entry.Name, FullPath = entry.FullPath, IsDirectory = entry.IsDirectory, Parent = parent, Source = source };
            if (entry.IsDirectory) { var children = await BuildTreeRecursiveAsync(service, source, entry.FullPath, node, ct); foreach (var child in children) node.Children.Add(child); }
            nodes.Add(node);
        }
        return nodes;
    }

    private void UpdateSftpTreeColors(bool repaginate = true)
    {
        var domains = _settingsService.Current.AllValidStorageDomains.ToList();
        var treeSources = SftpRootNodes.Where(r => r.Source != null).Select(r => r.Source!).Distinct().ToList();

        _validKeysBySource.Clear();
        foreach (var src in treeSources) _validKeysBySource[src.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var post in _allPosts)
        {
            int validCount = post.Links.Count(u => LinkValidator.IsValidDomain(u, domains));
            post.ValidLinkCount = validCount;
            post.BaseStatus = GetBasePostStatus(post.Links, validCount);
            post.Status = post.BaseStatus;

            // Ogni link valido si confronta con TUTTE le sorgenti caricate
            foreach (var url in post.Links)
            {
                if (!LinkValidator.IsValidDomain(url, domains)) continue;
                foreach (var src in treeSources)
                {
                    var key = src.UrlKey(url);
                    if (!string.IsNullOrEmpty(key)) _validKeysBySource[src.Id].Add(key);
                }
            }
        }

        if (SftpRootNodes.Count > 0)
        {
            foreach (var node in SftpRootNodes) node.Reset();
            foreach (var node in SftpRootNodes) ColorNodeRecursive(node);
            foreach (var node in SftpRootNodes) node.RefreshStatus();

            var foldersByKey = new Dictionary<string, SftpNode>(StringComparer.OrdinalIgnoreCase);
            IndexFolders(SftpRootNodes, foldersByKey);

            foreach (var post in _allPosts)
            {
                if (post.Status is not (PostStatus.Valid or PostStatus.Mixed)) continue;

                var folder = FindFolderForPost(post, foldersByKey);
                if (folder == null || !folder.IsDirectory) continue;

                // Post giallo (alcuni link fuori dai domini impostati): anche la sua cartella,
                // le sottocartelle e le cartelle superiori diventano gialle, per confronto.
                if (post.Status == PostStatus.Mixed)
                {
                    folder.ApplyMixed();
                    folder.RaiseAncestors(NodeStatus.Mixed);
                    continue;
                }

                int totalFilesInFolder = GetAllFiles(folder).Count;
                if (totalFilesInFolder > 0 && post.ValidLinkCount != totalFilesInFolder)
                {
                    post.Status = PostStatus.Mismatch;
                    folder.ApplyMismatch();
                    // Anche la cartella principale (es. "MAO" sopra "MAO/1") diventa viola
                    folder.RaiseAncestors(NodeStatus.Mismatch);
                }
            }
        }

        // Durante il refresh automatico non ricostruiamo la lista dei post,
        // altrimenti si perderebbe la selezione (e la mappatura in corso).
        if (repaginate) ApplyFilterAndPaginate();
        else UpdateStatusCounts();
    }

    private List<SftpNode> GetAllFiles(SftpNode node)
    {
        var files = new List<SftpNode>();
        if (!node.IsDirectory) { files.Add(node); return files; }
        foreach (var child in node.Children) files.AddRange(GetAllFiles(child));
        return files;
    }

    private void ColorNodeRecursive(SftpNode node)
    {
        if (node.IsDirectory)
        {
            foreach (var child in node.Children) ColorNodeRecursive(child);
            return;
        }

        var source = node.Source;
        bool valid = source != null && _validKeysBySource.TryGetValue(source.Id, out var keys) && keys.Contains(source.NodeKey(node.FullPath));
        node.BaseStatus = valid ? NodeStatus.Valid : NodeStatus.Invalid;
    }

    private void IndexFolders(IEnumerable<SftpNode> nodes, Dictionary<string, SftpNode> map)
    {
        foreach (var node in nodes)
        {
            if (!node.IsDirectory) continue;
            if (node.Source != null && !node.IsSourceRoot)
            {
                string key = node.Source.NodeKey(node.FullPath);
                if (!string.IsNullOrEmpty(key)) map[$"{node.Source.Id}|{key}"] = node;
            }
            IndexFolders(node.Children, map);
        }
    }

    /// <summary>
    /// Cartella che contiene i file del post: la cartella comune ai suoi link validi,
    /// cercata in ogni sorgente caricata (la prima che la contiene).
    /// </summary>
    private SftpNode? FindFolderForPost(VideoboxPostItem post, Dictionary<string, SftpNode> foldersByKey)
    {
        var domains = _settingsService.Current.AllValidStorageDomains.ToList();
        var validLinks = post.Links.Where(u => LinkValidator.IsValidDomain(u, domains)).ToList();
        if (validLinks.Count == 0) return null;

        foreach (var src in SftpRootNodes.Where(r => r.Source != null).Select(r => r.Source!).Distinct())
        {
            var dirs = new List<string[]>();
            foreach (var url in validLinks)
            {
                var key = src.UrlKey(url);
                if (string.IsNullOrEmpty(key)) continue;
                int idx = key.LastIndexOf('/');
                if (idx <= 0) continue;
                dirs.Add(key.Substring(0, idx).Split('/', StringSplitOptions.RemoveEmptyEntries));
            }
            if (dirs.Count == 0) continue;

            var common = dirs[0];
            int len = common.Length;
            foreach (var d in dirs.Skip(1))
            {
                int i = 0;
                while (i < len && i < d.Length && string.Equals(common[i], d[i], StringComparison.OrdinalIgnoreCase)) i++;
                len = i;
            }
            if (len == 0) continue;

            if (foldersByKey.TryGetValue($"{src.Id}|{string.Join("/", common.Take(len))}", out var folder))
                return folder;
        }
        return null;
    }

    partial void OnSelectedSftpNodeChanged(SftpNode? value)
    {
        // Cartella → mappatura di tutti i suoi file; file → mappatura di quel solo file
        if (value != null) { SftpPathInput = value.FullPath; }
    }

    [RelayCommand]
    public async Task MapSftpFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(SftpPathInput)) { StatusMessage = "Seleziona una cartella o un file dall'albero."; return; }
        if (CurrentPostId == 0) { StatusMessage = "Seleziona prima un post."; return; }

        // Singolo file (es. un film): va sulla riga selezionata, o sulla prima ancora da mappare
        if (SelectedSftpNode is { IsDirectory: false } fileNode && fileNode.FullPath == SftpPathInput)
        {
            MapSingleFile(fileNode);
            return;
        }
        if (MappingItems.Count == 0) { StatusMessage = "Il post non ha link da mappare."; return; }

        try
        {
            IsBusy = true; StatusMessage = $"Mappatura in corso: {SftpPathInput}...";

            var source = SourceForPath(SftpPathInput)
                         ?? throw new InvalidOperationException("il percorso non appartiene a nessuna sorgente");
            var service = await _pool.GetAsync(source.ProfileId);
            var files = await service.ListDirectoryAsync(SftpPathInput);
            var videoFiles = files.Where(f => !f.IsDirectory).OrderBy(f => f.Name, new NaturalSortComparer()).ToList();

            if (videoFiles.Count == 0) { StatusMessage = "Nessun file trovato nella cartella."; return; }

            if (videoFiles.Count > MappingItems.Count)
            {
                while (MappingItems.Count < videoFiles.Count)
                {
                    MappingItems.Add(new EpisodeMappingItem { EpisodeId = 0, OldUrl = "" });
                }
                StatusMessage = $"⚠ Aggiunti {videoFiles.Count - MappingItems.Count} link per i file extra.";
            }
            else if (videoFiles.Count < MappingItems.Count)
            {
                for (int i = videoFiles.Count; i < MappingItems.Count; i++)
                {
                    MappingItems[i].SftpFileName = null;
                    MappingItems[i].NewUrl = null;
                    MappingItems[i].IsSelectedForUpdate = false; 
                }
                StatusMessage = $"⚠ {MappingItems.Count - videoFiles.Count} link in eccesso verranno rimossi se applichi.";
            }

            for (int i = 0; i < videoFiles.Count; i++)
            {
                var file = videoFiles[i];
                MappingItems[i].SftpFileName = file.Name;
                MappingItems[i].NewUrl = source.BuildPublicUrl(file.FullPath);
                MappingItems[i].IsSelectedForUpdate = true;
                MappingItems[i].EvaluateStatus(_settingsService.Current.AllValidStorageDomains);
            }
            
            if (string.IsNullOrEmpty(StatusMessage)) StatusMessage = "Mappatura completata.";
        }
        catch (Exception ex) { StatusMessage = $"Errore: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    private void MapSingleFile(SftpNode file)
    {
        var target = SelectedMappingItem
                     ?? MappingItems.FirstOrDefault(m => string.IsNullOrEmpty(m.NewUrl))
                     ?? MappingItems.FirstOrDefault();
        if (target == null)
        {
            // Post senza link: se ne crea uno nuovo per questo file
            target = new EpisodeMappingItem { EpisodeId = 0, OldUrl = "" };
            MappingItems.Add(target);
        }

        target.SftpFileName = file.Name;
        target.NewUrl = GetPublicUrl(file);
        target.IsSelectedForUpdate = true;
        target.EvaluateStatus(_settingsService.Current.AllValidStorageDomains);

        // Si passa alla riga successiva: più file (es. film in due parti) si mappano in sequenza
        int idx = MappingItems.IndexOf(target);
        SelectedMappingItem = idx >= 0 && idx < MappingItems.Count - 1 ? MappingItems[idx + 1] : null;
        StatusMessage = $"Mappato il file {file.Name} sulla riga {idx + 1}.";
    }

    [RelayCommand]
    public void SwapUp(EpisodeMappingItem item)
    {
        var index = MappingItems.IndexOf(item);
        if (index > 0) SwapItems(index, index - 1);
    }

    [RelayCommand]
    public void SwapDown(EpisodeMappingItem item)
    {
        var index = MappingItems.IndexOf(item);
        if (index >= 0 && index < MappingItems.Count - 1) SwapItems(index, index + 1);
    }

    private void SwapItems(int index1, int index2)
    {
        var item1 = MappingItems[index1];
        var item2 = MappingItems[index2];

        var tempFile = item1.SftpFileName; var tempUrl = item1.NewUrl;
        item1.SftpFileName = item2.SftpFileName; item1.NewUrl = item2.NewUrl;
        item2.SftpFileName = tempFile; item2.NewUrl = tempUrl;

        item1.EvaluateStatus(_settingsService.Current.AllValidStorageDomains);
        item2.EvaluateStatus(_settingsService.Current.AllValidStorageDomains);
    }

    [RelayCommand]
    public async Task ApplyUpdatesAsync()
    {
        try
        {
            IsBusy = true; StatusMessage = "Invio a Videobox...";
            
            var finalUrls = MappingItems.Where(x => x.IsSelectedForUpdate).Select(x => !string.IsNullOrEmpty(x.NewUrl) ? x.NewUrl : x.OldUrl).ToList();

            // Usa la categoria selezionata se valida, altrimenti quella originale del post
            int catId = (SelectedCategory != null && SelectedCategory.Id > 0) 
                        ? SelectedCategory.Id 
                        : _currentPostOriginalCategoryId;

            // Chiamata pulita
            await _apiService.UpdateVideoUrlsAsync(CurrentPostId, _currentServerId, CurrentPostTitle, catId, _currentPostAuthorId, finalUrls);

            foreach (var item in MappingItems)
            {
                if (item.IsSelectedForUpdate && !string.IsNullOrEmpty(item.NewUrl))
                {
                    item.OldUrl = item.NewUrl;
                    item.NewUrl = null;
                    item.SftpFileName = null;
                    item.IsSelectedForUpdate = false;
                }
                item.EvaluateStatus(_settingsService.Current.AllValidStorageDomains);
            }

            var itemsToRemove = MappingItems.Where(x => !x.IsSelectedForUpdate && string.IsNullOrEmpty(x.SftpFileName)).ToList();
            foreach (var item in itemsToRemove) MappingItems.Remove(item);

            // Il post appena modificato si aggiorna subito nella lista
            var updatedPost = await _apiService.GetVideoByIdAsync(CurrentPostId);
            ApplyFreshPostData(updatedPost, recolor: false);

            UpdateSftpTreeColors();
            StatusMessage = "Aggiornamento completato con successo!";
        }
        catch (Exception ex) 
        { 
            StatusMessage = $"ERRORE: {ex.Message}"; 
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Sorgente di un percorso: quella del nodo selezionato se coincide, altrimenti la
    /// sorgente attiva la cui cartella contiene il percorso (la più specifica).
    /// </summary>
    private VideoboxSource? SourceForPath(string path)
    {
        if (SelectedSftpNode?.Source != null && SelectedSftpNode.FullPath == path) return SelectedSftpNode.Source;
        var p = NormalizeRemotePath(path);
        return Sources
            .Where(x => !string.IsNullOrWhiteSpace(x.RootPath))
            .Where(x => p == NormalizeRemotePath(x.RootPath) || p.StartsWith(NormalizeRemotePath(x.RootPath).TrimEnd('/') + "/", StringComparison.Ordinal))
            .OrderByDescending(x => x.IsEnabled)
            .ThenByDescending(x => x.RootPath.Length)
            .FirstOrDefault();
    }

    /// <summary>Link pubblico di un file dell'albero, col dominio della sua sorgente.</summary>
    public string GetPublicUrl(SftpNode node) =>
        (node.Source ?? SourceForPath(node.FullPath))?.BuildPublicUrl(node.FullPath) ?? string.Empty;

    /// <summary>Link pubblici di tutti i file sotto un nodo, in ordine naturale.</summary>
    public List<string> GetPublicUrlsUnder(SftpNode node)
    {
        var files = new List<SftpNode>();
        void Walk(SftpNode n)
        {
            if (!n.IsDirectory) { files.Add(n); return; }
            foreach (var c in n.Children) Walk(c);
        }
        Walk(node);
        return files.OrderBy(f => f.FullPath, new NaturalSortComparer()).Select(GetPublicUrl).ToList();
    }

    private int ParseCategoryId(JsonElement catElement)
    {
        if (catElement.ValueKind == JsonValueKind.Object && catElement.TryGetProperty("id", out var idProp))
            return idProp.GetInt32();
        if (catElement.ValueKind == JsonValueKind.Array && catElement.GetArrayLength() > 0)
        {
            var first = catElement[0];
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("id", out var idPropArr))
                return idPropArr.GetInt32();
        }
        if (catElement.ValueKind == JsonValueKind.Number) return catElement.GetInt32();
        return 0;
    }
}