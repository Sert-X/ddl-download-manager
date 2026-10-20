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
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public List<string> Links { get; set; } = new();
    public int LinkCount => Links.Count;

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

    private readonly HashSet<string> _globalValidKeys = new(StringComparer.OrdinalIgnoreCase);

    private int _currentPostAuthorId = 0;
    private string _currentServerId = "5";
    private int _currentPostOriginalCategoryId = 0;

    // Aggiornamento automatico dell'albero SFTP
    private static readonly TimeSpan TreeRefreshDebounce = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan TreePollInterval = TimeSpan.FromSeconds(60);
    private readonly DispatcherTimer _treePollTimer;
    private CancellationTokenSource? _treeRefreshDebounceCts;
    private CancellationTokenSource? _fullTreeScanCts;
    private bool _isRefreshingTree;
    /// <summary>Cartelle da riallineare (path → ricorsivo). Accesso solo dal thread UI.</summary>
    private readonly Dictionary<string, bool> _pendingTreeDirs = new(StringComparer.Ordinal);
    private bool _pendingFullTreeScan;

    [ObservableProperty] private string configIpsBaseUrl = string.Empty;
    [ObservableProperty] private string configIpsApiKey = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UrlPreview))] private string configPrimaryStorageUrl = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UrlPreview))] private string configPublicPath = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UrlPreview), nameof(UrlPreviewSource))] private string configSftpBasePath = string.Empty;

    public ObservableCollection<DomainItem> SecondaryDomains { get; } = new();
    public bool HasSecondaryDomains => SecondaryDomains.Count > 0;
    
    // CATEGORIE
    public ObservableCollection<CategoryItem> AvailableCategories { get; } = new();
    [ObservableProperty] private CategoryItem? selectedCategory;

    [ObservableProperty] private ObservableCollection<VideoboxPostItem> categoryPosts = new();
    [ObservableProperty] private VideoboxPostItem? selectedPost;
    [ObservableProperty] private bool hideOkPosts = false;
    [ObservableProperty] private string totalPostsText = "Totale post: 0";
    [ObservableProperty] private string searchQuery = string.Empty;

    public ObservableCollection<int> PageNumbers { get; } = new();
    [ObservableProperty] private int currentPageDisplay = 1;

    [ObservableProperty] private ObservableCollection<SftpNode> sftpRootNodes = new();
    [ObservableProperty] private SftpNode? selectedSftpNode;
    [ObservableProperty] private string sftpPathInput = string.Empty;
    [ObservableProperty] private bool isSftpConnected = false;

    [ObservableProperty] private ObservableCollection<EpisodeMappingItem> mappingItems = new();
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

    public VideoboxTabViewModel(VideoboxApiService apiService, ISftpService sftpService, SettingsService settingsService, SftpUploadQueueService uploadQueue)
    {
        _apiService = apiService;
        _sftpService = sftpService;
        _settingsService = settingsService;

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

        var current = _settingsService.Current;
        ConfigIpsBaseUrl = current.IpsBaseUrl;
        ConfigIpsApiKey = current.IpsApiKey;
        ConfigPrimaryStorageUrl = current.PrimaryStorageUrl;
        ConfigPublicPath = current.VideoboxPublicPath;
        ConfigSftpBasePath = current.SftpBasePath;

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
            }
        };
    }

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
        current.PrimaryStorageUrl = ConfigPrimaryStorageUrl.Trim();
        current.VideoboxPublicPath = ConfigPublicPath.Trim().Trim('/');
        current.SftpBasePath = ConfigSftpBasePath.Trim();
        current.SecondaryStorageUrlsRaw = string.Join(",", SecondaryDomains.Select(d => d.Url).Where(u => !string.IsNullOrWhiteSpace(u)));

        _settingsService.Save();
        UpdateSftpTreeColors(); 
        StatusMessage = "Impostazioni salvate e colori aggiornati!";
    }

    partial void OnHideOkPostsChanged(bool value) { _currentPage = 1; ApplyFilterAndPaginate(); }
    partial void OnSearchQueryChanged(string value) { _currentPage = 1; ApplyFilterAndPaginate(); }

    private void ApplyFilterAndPaginate()
    {
        var filtered = _allPosts.AsEnumerable();

        if (HideOkPosts)
            filtered = filtered.Where(p => p.Status != PostStatus.Valid);

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
        catch (OperationCanceledException) { StatusMessage = "Caricamento dei post annullato."; }
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
        catch (OperationCanceledException)
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
            var remoteServer = post.videos?.Values.FirstOrDefault(v => v.vbs_name != null && v.vbs_name.Contains("Link Remoto", StringComparison.OrdinalIgnoreCase)) ?? post.videos?.Values.FirstOrDefault(v => v.online != null && v.online.Count > 0);
            var urls = remoteServer?.online?.Values.Select(e => e.vbo_url).ToList() ?? new List<string>();

            var item = new VideoboxPostItem {
                Id = post.id, Title = post.title, Url = post.url, Links = urls
            };
            _allPosts.Add(item);
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

    [RelayCommand]
    public async Task LoadSftpTreeAsync()
    {
        if (!IsSftpConnected) { StatusMessage = "Connettiti prima all'SFTP."; return; }
        if (string.IsNullOrWhiteSpace(ConfigSftpBasePath)) { StatusMessage = "Configura il Base Path SFTP."; return; }
        if (IsLoadingTree) return;
        using var cts = _treeCts = new CancellationTokenSource();
        try
        {
            IsBusy = true;
            IsLoadingTree = true;
            _treeFoldersRead = 0;
            TreeProgressText = "Lettura cartelle...";
            StatusMessage = "Caricamento albero SFTP..."; SftpRootNodes.Clear();
            var rootNodes = await BuildTreeRecursiveAsync(ConfigSftpBasePath, null, cts.Token);
            foreach (var node in rootNodes) SftpRootNodes.Add(node);

            UpdateSftpTreeColors();
            StatusMessage = $"Albero SFTP caricato: {_treeFoldersRead} cartelle (aggiornamento automatico attivo).";
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

    /// <summary>Accoda il riallineamento di una cartella remota. Thread-safe.</summary>
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

    /// <summary>
    /// Esegue i refresh in coda fondendo i risultati nell'albero esistente,
    /// così le cartelle espanse e la selezione restano invariate.
    /// </summary>
    private async Task ProcessTreeRefreshAsync()
    {
        if (SftpRootNodes.Count == 0 || !IsSftpConnected || string.IsNullOrWhiteSpace(ConfigSftpBasePath))
        {
            _pendingTreeDirs.Clear();
            _pendingFullTreeScan = false;
            return;
        }
        if (_isRefreshingTree) return; // il giro in corso riprocessa la coda prima di finire
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
                        changed |= await RefreshTreeDirectoryAsync(path, recursive);
                    continue;
                }

                _pendingFullTreeScan = false;
                var scanCts = _fullTreeScanCts = new CancellationTokenSource();
                try
                {
                    var freshNodes = await BuildTreeRecursiveAsync(ConfigSftpBasePath, null, scanCts.Token);
                    changed |= MergeNodes(SftpRootNodes, freshNodes, null, recurse: true);
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
        catch (Exception ex) { StatusMessage = $"Aggiornamento albero SFTP fallito: {ex.Message}"; }
        finally { _isRefreshingTree = false; }
    }

    /// <summary>
    /// Riallinea una singola cartella. Se non è (ancora) nell'albero si riallinea
    /// l'antenato più vicino presente. Ritorna true se è cambiato qualcosa.
    /// </summary>
    private async Task<bool> RefreshTreeDirectoryAsync(string path, bool recursive)
    {
        var basePath = NormalizeRemotePath(ConfigSftpBasePath);
        if (path != basePath && !path.StartsWith(basePath.TrimEnd('/') + "/", StringComparison.Ordinal))
            return false; // fuori dal Base Path: non ci riguarda

        // Scende nell'albero fin dove la cartella esiste.
        SftpNode? node = null;
        var collection = SftpRootNodes;
        var relative = path.Length > basePath.Length ? path.Substring(basePath.Length).Trim('/') : string.Empty;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var child = collection.FirstOrDefault(c => c.IsDirectory && c.Name == segment);
            if (child == null) { recursive = false; break; } // le cartelle nuove vengono comunque lette per intero
            node = child;
            collection = child.Children;
        }

        var dirPath = node?.FullPath ?? ConfigSftpBasePath;

        if (recursive)
        {
            var fresh = await BuildTreeRecursiveAsync(dirPath, node);
            return MergeNodes(collection, fresh, node, recurse: true);
        }

        // Solo il contenuto diretto; si leggono per intero solo le sottocartelle nuove.
        var nodes = new List<SftpNode>();
        foreach (var entry in await ListSortedEntriesAsync(dirPath))
        {
            var newNode = new SftpNode { Name = entry.Name, FullPath = entry.FullPath, IsDirectory = entry.IsDirectory, Parent = node };
            bool isNewDir = entry.IsDirectory && !collection.Any(c => c.IsDirectory && c.Name == entry.Name);
            if (isNewDir)
                foreach (var child in await BuildTreeRecursiveAsync(entry.FullPath, newNode)) newNode.Children.Add(child);
            nodes.Add(newNode);
        }
        return MergeNodes(collection, nodes, node, recurse: false);
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

    private async Task<List<SftpRemoteEntry>> ListSortedEntriesAsync(string path, CancellationToken ct = default)
    {
        var entries = await _sftpService.ListDirectoryAsync(path, ct);
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

    private async Task<List<SftpNode>> BuildTreeRecursiveAsync(string path, SftpNode? parent, CancellationToken ct = default)
    {
        var nodes = new List<SftpNode>();
        foreach (var entry in await ListSortedEntriesAsync(path, ct))
        {
            var node = new SftpNode { Name = entry.Name, FullPath = entry.FullPath, IsDirectory = entry.IsDirectory, Parent = parent };
            if (entry.IsDirectory) { var children = await BuildTreeRecursiveAsync(entry.FullPath, node, ct); foreach (var child in children) node.Children.Add(child); }
            nodes.Add(node);
        }
        return nodes;
    }

    private void UpdateSftpTreeColors(bool repaginate = true)
    {
        _globalValidKeys.Clear();
        foreach (var post in _allPosts)
        {
            int validCount = post.Links.Count(u => LinkValidator.IsValidDomain(u, _settingsService.Current.AllValidStorageDomains));
            post.ValidLinkCount = validCount;
            post.BaseStatus = GetBasePostStatus(post.Links, validCount);
            post.Status = post.BaseStatus;

            foreach (var url in post.Links)
            {
                if (LinkValidator.IsValidDomain(url, _settingsService.Current.AllValidStorageDomains))
                {
                    string key = GetNormalizedKey(url);
                    if (!string.IsNullOrEmpty(key)) _globalValidKeys.Add(key);
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

        string key = GetNormalizedKey(node.FullPath);
        node.BaseStatus = _globalValidKeys.Contains(key) ? NodeStatus.Valid : NodeStatus.Invalid;
    }

    private void IndexFolders(IEnumerable<SftpNode> nodes, Dictionary<string, SftpNode> map)
    {
        foreach (var node in nodes)
        {
            if (!node.IsDirectory) continue;
            string key = GetNormalizedKey(node.FullPath);
            if (!string.IsNullOrEmpty(key)) map[key] = node;
            IndexFolders(node.Children, map);
        }
    }

    private SftpNode? FindFolderForPost(VideoboxPostItem post, Dictionary<string, SftpNode> foldersByKey)
    {
        var domains = _settingsService.Current.AllValidStorageDomains;
        var dirs = new List<string[]>();

        foreach (var url in post.Links)
        {
            if (!LinkValidator.IsValidDomain(url, domains)) continue;
            string key = GetNormalizedKey(url);
            int idx = key.LastIndexOf('/');
            if (idx <= 0) continue;
            dirs.Add(key.Substring(0, idx).Split('/', StringSplitOptions.RemoveEmptyEntries));
        }

        if (dirs.Count == 0) return null;

        var common = dirs[0];
        int len = common.Length;
        foreach (var d in dirs.Skip(1))
        {
            int i = 0;
            while (i < len && i < d.Length && string.Equals(common[i], d[i], StringComparison.OrdinalIgnoreCase)) i++;
            len = i;
        }

        if (len == 0) return null;

        string folderKey = string.Join("/", common.Take(len));
        return foldersByKey.TryGetValue(folderKey, out var folder) ? folder : null;
    }

    partial void OnSelectedSftpNodeChanged(SftpNode? value)
    {
        if (value != null && value.IsDirectory) { SftpPathInput = value.FullPath; }
    }

    [RelayCommand]
    public async Task MapSftpFolderAsync()
    {
        if (!IsSftpConnected) { StatusMessage = "Connettiti prima all'SFTP."; return; }
        if (string.IsNullOrWhiteSpace(SftpPathInput)) { StatusMessage = "Seleziona una cartella dall'albero."; return; }
        if (MappingItems.Count == 0) { StatusMessage = "Seleziona prima un post."; return; }

        try
        {
            IsBusy = true; StatusMessage = $"Mappatura in corso: {SftpPathInput}...";

            var files = await _sftpService.ListDirectoryAsync(SftpPathInput);
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
                MappingItems[i].NewUrl = GeneratePublicUrl(file.FullPath);
                MappingItems[i].IsSelectedForUpdate = true;
                MappingItems[i].EvaluateStatus(_settingsService.Current.AllValidStorageDomains);
            }
            
            if (string.IsNullOrEmpty(StatusMessage)) StatusMessage = "Mappatura completata.";
        }
        catch (Exception ex) { StatusMessage = $"Errore: {ex.Message}"; }
        finally { IsBusy = false; }
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

            if (SelectedPost != null)
            {
                var postData = await _apiService.GetVideoByIdAsync(CurrentPostId);
                var remoteServer = postData.videos?.Values.FirstOrDefault(v => v.vbs_name != null && v.vbs_name.Contains("Link Remoto", StringComparison.OrdinalIgnoreCase)) ?? postData.videos?.Values.FirstOrDefault(v => v.online != null && v.online.Count > 0);
                var urls = remoteServer?.online?.Values.Select(e => e.vbo_url).ToList() ?? new List<string>();

                SelectedPost.Links = urls;
            }

            UpdateSftpTreeColors();
            StatusMessage = "Aggiornamento completato con successo!";
        }
        catch (Exception ex) 
        { 
            StatusMessage = $"ERRORE: {ex.Message}"; 
        }
        finally { IsBusy = false; }
    }

    private string GetNormalizedKey(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return string.Empty;

        string path;
        if (fullPath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try { path = Uri.UnescapeDataString(new Uri(fullPath).AbsolutePath); }
            catch { path = Uri.UnescapeDataString(fullPath); }
            path = path.Replace("\\", "/");
        }
        else
        {
            path = Uri.UnescapeDataString(fullPath).Replace("\\", "/");
            string sftpBase = Uri.UnescapeDataString(_settingsService.Current.SftpBasePath).Replace("\\", "/").TrimEnd('/');
            if (!string.IsNullOrEmpty(sftpBase) && path.StartsWith(sftpBase, StringComparison.OrdinalIgnoreCase))
                path = path.Substring(sftpBase.Length);
        }

        path = path.Replace("//", "/").Trim('/', ' ');

        string publicPath = Uri.UnescapeDataString(_settingsService.Current.VideoboxPublicPath).Replace("\\", "/").Trim('/');
        if (!string.IsNullOrEmpty(publicPath))
        {
            if (path.Equals(publicPath, StringComparison.OrdinalIgnoreCase))
                path = string.Empty;
            else if (path.StartsWith(publicPath + "/", StringComparison.OrdinalIgnoreCase))
                path = path.Substring(publicPath.Length + 1);
        }

        return path.Trim('/', ' ').ToLowerInvariant();
    }

    private string GeneratePublicUrl(string fullSftpPath)
    {
        var settings = _settingsService.Current;
        return BuildPublicUrl(settings.SftpBasePath, settings.VideoboxPublicPath, settings.PrimaryStorageUrl, fullSftpPath);
    }

    /// <summary>
    /// Trasforma un percorso SFTP nell'URL pubblico: toglie la cartella base SFTP,
    /// antepone dominio primario e percorso pubblico, codifica ogni segmento.
    /// </summary>
    private static string BuildPublicUrl(string sftpBasePath, string publicPath, string primaryUrl, string fullSftpPath)
    {
        string basePath = sftpBasePath.Replace("\\", "/").TrimEnd('/');
        string fullPath = fullSftpPath.Replace("\\", "/");

        if (fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            fullPath = fullPath.Substring(basePath.Length);

        static string Encode(string path) =>
            string.Join("/", path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        string encodedPath = Encode(fullPath);
        string baseUrl = primaryUrl.Trim().TrimEnd('/');
        string encodedPublicPath = Encode(publicPath.Trim().Trim('/'));

        return string.IsNullOrEmpty(encodedPublicPath)
            ? $"{baseUrl}/{encodedPath}"
            : $"{baseUrl}/{encodedPublicPath}/{encodedPath}";
    }

    /// <summary>Anteprima live, nelle impostazioni, dell'URL che verrà generato per un file d'esempio.</summary>
    public string UrlPreview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ConfigPrimaryStorageUrl)) return "Inserisci il dominio primario per vedere l'anteprima.";
            var sample = $"{ConfigSftpBasePath.TrimEnd('/')}/Nome Serie/Nome Serie - Ep 01.mp4";
            return BuildPublicUrl(ConfigSftpBasePath, ConfigPublicPath, ConfigPrimaryStorageUrl, sample);
        }
    }

    /// <summary>Esempio di percorso SFTP usato dall'anteprima.</summary>
    public string UrlPreviewSource => $"{(string.IsNullOrWhiteSpace(ConfigSftpBasePath) ? "" : ConfigSftpBasePath.TrimEnd('/'))}/Nome Serie/Nome Serie - Ep 01.mp4";

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