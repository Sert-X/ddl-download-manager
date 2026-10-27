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
    /// <summary>Dopo "Applica": true = il forum ha salvato questo link, false = inviato ma non confermato, null = niente.</summary>
    [ObservableProperty] private bool? savedOnForum;
    /// <summary>Numero della riga (1, 2, 3...).</summary>
    [ObservableProperty] private int index;

    // Nella tabella si mostra il nome del file in grande e, sotto, server e cartella:
    // l'URL intero (tutto uguale fino al nome del file) resta nel suggerimento.
    /// <summary>Riga aggiunta per un file in più: sul forum non c'era nessun link.</summary>
    public string OldName => FileNameOf(OldUrl) ?? "nuovo link (file in più)";
    public string OldWhere => WhereOf(OldUrl);
    public bool HasOld => !string.IsNullOrEmpty(OldUrl);
    public string NewName => !string.IsNullOrEmpty(SftpFileName) ? SftpFileName! : FileNameOf(NewUrl) ?? "";
    public string NewWhere => WhereOf(NewUrl);
    public bool HasNew => !string.IsNullOrEmpty(NewUrl);
    /// <summary>In "Diventa" si mostra il nuovo file solo se la riga verrà inviata (con "Usa").</summary>
    public bool ShowNew => HasNew && IsSelectedForUpdate;
    /// <summary>Riga senza nuovo link: sul forum resta com'è (o viene tolta se "Usa" è spento).</summary>
    public string UnchangedText => SavedOnForum switch
    {
        true => "✓ salvato sul forum",
        false => "inviato, non ancora confermato dal forum",
        _ => IsSelectedForUpdate ? "resta com'è" : "verrà tolto dal post"
    };
    public bool IsSavedOnForum => SavedOnForum == true;
    public bool IsNotConfirmed => SavedOnForum == false;

    partial void OnSavedOnForumChanged(bool? value)
    {
        OnPropertyChanged(nameof(UnchangedText));
        OnPropertyChanged(nameof(IsSavedOnForum));
        OnPropertyChanged(nameof(IsNotConfirmed));
    }

    partial void OnOldUrlChanged(string value)
    {
        OnPropertyChanged(nameof(OldName));
        OnPropertyChanged(nameof(OldWhere));
        OnPropertyChanged(nameof(HasOld));
    }
    partial void OnNewUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(NewName));
        OnPropertyChanged(nameof(NewWhere));
        OnPropertyChanged(nameof(HasNew));
        OnPropertyChanged(nameof(ShowNew));
    }
    partial void OnSftpFileNameChanged(string? value) => OnPropertyChanged(nameof(NewName));
    partial void OnIsSelectedForUpdateChanged(bool value)
    {
        OnPropertyChanged(nameof(UnchangedText));
        OnPropertyChanged(nameof(ShowNew));
    }

    private static string? FileNameOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;
        var name = path.TrimEnd('/').Split('/').LastOrDefault() ?? url;
        try { return Uri.UnescapeDataString(name); } catch { return name; }
    }

    /// <summary>"server · cartella" del link (es. "data2ddl.ichigolink.top · Anime/Slime/2").</summary>
    private static string WhereOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return "";
        string folder;
        try { folder = Uri.UnescapeDataString(u.AbsolutePath); } catch { folder = u.AbsolutePath; }
        folder = folder.Trim('/');
        int slash = folder.LastIndexOf('/');
        folder = slash > 0 ? folder.Substring(0, slash) : "";
        return folder.Length > 0 ? $"{u.Host} · {folder}" : u.Host;
    }

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
    /// <summary>Server "Link Remoto" visto nei post: si usa per i post che non hanno ancora link.</summary>
    private string _remoteServerId = "5";
    private int _currentPostOriginalCategoryId = 0;

    // Aggiornamento automatico dell'albero SFTP
    private static readonly TimeSpan TreeRefreshDebounce = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan TreePollInterval = TimeSpan.FromSeconds(60);
    private readonly DispatcherTimer _treePollTimer;
    /// <summary>Contatore dei caricamenti completi: un refresh partito prima di un "Carica" viene scartato.</summary>
    private int _treeGeneration;

    private static readonly TimeSpan PostsRefreshInterval = TimeSpan.FromSeconds(30);
    private readonly DispatcherTimer _postsRefreshTimer;
    private bool _refreshingPosts;
    private CancellationTokenSource? _treeRefreshDebounceCts;
    private CancellationTokenSource? _fullTreeScanCts;
    private bool _isRefreshingTree;
    /// <summary>Cartelle da riallineare ("server\npath" → ricorsivo). Accesso solo dal thread UI.</summary>
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
    /// <summary>Titolo nella casella modificabile: "Rinomina" o "Applica" lo inviano al forum.</summary>
    [ObservableProperty] private string editablePostTitle = string.Empty;
    public bool IsTitleChanged =>
        CurrentPostId != 0 && !string.IsNullOrWhiteSpace(EditablePostTitle) && EditablePostTitle.Trim() != CurrentPostTitle;
    partial void OnEditablePostTitleChanged(string value) => OnPropertyChanged(nameof(IsTitleChanged));
    partial void OnCurrentPostTitleChanged(string? oldValue, string newValue)
    {
        // Titolo cambiato sul sito: la casella lo segue, se non lo si stava modificando
        if (EditablePostTitle == oldValue) EditablePostTitle = newValue;
        OnPropertyChanged(nameof(IsTitleChanged));
    }

    // Conteggi della tabella di anteprima
    [ObservableProperty] private int oldLinkCount;
    [ObservableProperty] private int newLinkCount;
    [ObservableProperty] private int sftpFileCount;
    /// <summary>Riepilogo dell'ultimo "Applica" sul post aperto ("" = nessuno).</summary>
    [ObservableProperty] private string lastApplySummary = string.Empty;
    public bool HasLastApplySummary => LastApplySummary.Length > 0;
    partial void OnLastApplySummaryChanged(string value) => OnPropertyChanged(nameof(HasLastApplySummary));

    /// <summary>C'è qualcosa da inviare (abilita "Applica").</summary>
    public bool HasMappingItems => MappingItems.Count > 0;

    // Riepilogo sotto "Anteprima modifiche", calcolato sulle righe come sono davvero (spunte
    // comprese): quello che dice è esattamente quello che farà "Applica".
    private int RowsRemoved => MappingItems.Count(m => !m.IsSelectedForUpdate && m.HasOld);
    private int RowsAdded => MappingItems.Count(m => m.IsSelectedForUpdate && m.HasNew && !m.HasOld);
    private int RowsKept => MappingItems.Count(m => m.IsSelectedForUpdate && !m.HasNew && m.HasOld);

    public string MappingSummaryText
    {
        get
        {
            if (MappingItems.Count == 0) return "";
            if (SftpFileCount == 0) return "Nessun file abbinato: clicca una cartella nell'albero e premi Mappa";
            var parts = new List<string> { $"{SftpFileCount} {(SftpFileCount == 1 ? "file abbinato" : "file abbinati")}" };
            int removed = RowsRemoved, added = RowsAdded, kept = RowsKept;
            if (removed > 0) parts.Add($"{removed} {(removed == 1 ? "link verrà tolto" : "link verranno tolti")}");
            if (added > 0) parts.Add($"{added} {(added == 1 ? "link verrà aggiunto" : "link verranno aggiunti")}");
            if (kept > 0) parts.Add($"{kept} {(kept == 1 ? "resta com'è" : "restano com'erano")}");
            return string.Join(" · ", parts);
        }
    }
    /// <summary>Tutto abbinato, niente tolto o aggiunto (verde); altrimenti da guardare (arancione).</summary>
    public bool MappingSummaryOk => SftpFileCount > 0 && RowsRemoved == 0 && RowsAdded == 0 && RowsKept == 0;
    public bool MappingSummaryWarn => SftpFileCount > 0 && !MappingSummaryOk;
    public bool MappingSummaryInfo => MappingItems.Count > 0 && SftpFileCount == 0;
    /// <summary>Post aperto ma tabella vuota: si mostra cosa fare.</summary>
    public bool ShowMappingHint => SelectedPost != null && MappingItems.Count == 0;

    private void UpdateMappingCounts()
    {
        OnPropertyChanged(nameof(HasMappingItems));
        for (int i = 0; i < MappingItems.Count; i++) MappingItems[i].Index = i + 1;
        OldLinkCount = MappingItems.Count(m => !string.IsNullOrEmpty(m.OldUrl));
        NewLinkCount = MappingItems.Count(m => m.IsSelectedForUpdate && !string.IsNullOrEmpty(m.NewUrl));
        SftpFileCount = MappingItems.Count(m => !string.IsNullOrEmpty(m.SftpFileName));
        OnPropertyChanged(nameof(MappingSummaryText));
        OnPropertyChanged(nameof(MappingSummaryOk));
        OnPropertyChanged(nameof(MappingSummaryWarn));
        OnPropertyChanged(nameof(MappingSummaryInfo));
        OnPropertyChanged(nameof(ShowMappingHint));
    }

    private void OnMappingItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null) foreach (EpisodeMappingItem m in e.OldItems) m.PropertyChanged -= OnMappingItemPropertyChanged;
        if (e.NewItems != null) foreach (EpisodeMappingItem m in e.NewItems) m.PropertyChanged += OnMappingItemPropertyChanged;
        UpdateMappingCounts();
    }

    private void OnMappingItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EpisodeMappingItem.OldUrl) or nameof(EpisodeMappingItem.NewUrl)
            or nameof(EpisodeMappingItem.SftpFileName) or nameof(EpisodeMappingItem.IsSelectedForUpdate))
            UpdateMappingCounts();
    }
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
    /// <summary>Totale delle cartelle da leggere (somma dei conteggi delle sorgenti); 0 = non noto.</summary>
    [ObservableProperty] private int _treeFoldersTotal;
    [ObservableProperty] private int _treeFoldersReadShown;
    private int _treeCountsPending;
    private bool _treeTotalUnknown;
    public bool HasTreeTotal => TreeFoldersTotal > 0 && _treeCountsPending == 0 && !_treeTotalUnknown;

    private void UpdateTreeProgressText()
    {
        TreeFoldersReadShown = _treeFoldersRead;
        OnPropertyChanged(nameof(HasTreeTotal));
        TreeProgressText = HasTreeTotal
            ? $"{_treeFoldersRead} cartelle lette su {TreeFoldersTotal} ({Math.Min(100, _treeFoldersRead * 100 / TreeFoldersTotal)}%)"
            : $"{_treeFoldersRead} cartelle lette";
    }

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
        MappingItems.CollectionChanged += OnMappingItemsChanged;

        // Modifiche fatte dall'app (tab SFTP, organizer, upload) → refresh immediato (con debounce).
        // Si aggiorna solo la cartella toccata, non tutto l'albero.
        // Modifiche fatte dall'app su qualsiasi server (tab SFTP, secondo server, copie tra server)
        SftpChangeHub.Changed += (server, change) => EnqueueTreeRefresh(server, change.DirectoryPath, change.Recursive);
        // Ogni upload aggiorna l'albero del server su cui è andato (le schede SFTP possono essere più d'una)
        uploadQueue.JobCompleted += job => EnqueueTreeRefresh(job.Server?.ServerKey ?? _sftpService.ServerKey, job.DestinationFolder, false);

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
        _postsRefreshTimer.Tick += async (_, _) =>
        {
            await RefreshVisiblePostsAsync();
            await AddNewPostsAsync();
        };
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
        await ConvertSharedSourcesAsync();
        var options = await _pool.GetProfileOptionsAsync();
        if (!options.SequenceEqual(SftpProfiles))
        {
            SftpProfiles.Clear();
            foreach (var o in options) SftpProfiles.Add(o);
        }
        SyncSourceProfiles();
    }

    /// <summary>
    /// Le sorgenti con la vecchia opzione "connessione della tab SFTP" passano al profilo salvato
    /// dello stesso server: ogni sorgente ha così la sua connessione. Se non si capisce quale sia
    /// (nessuna connessione aperta e più profili) si riprova al prossimo giro.
    /// </summary>
    private async Task ConvertSharedSourcesAsync()
    {
        var legacy = Sources.Where(x => x.ProfileId == VideoboxSftpPool.SharedProfileId).ToList();
        if (legacy.Count == 0) return;
        var profileId = await _pool.ResolveSharedProfileAsync();
        if (profileId == null) return;
        foreach (var src in legacy) src.ProfileId = profileId.Value;
        _settingsService.Current.VideoboxSources = Sources.Select(x => x.ToConfig()).ToList();
        _settingsService.Save();
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
        {
            SaveCategorySources();
            if (sender is VideoboxSource { IsEnabled: false } src) RemoveSourceRoot(src.Id);
        }
        NotifySourcesChanged();
    }

    /// <summary>Toglie dall'albero la radice di una sorgente (senza rileggere le altre).</summary>
    private void RemoveSourceRoot(string sourceId)
    {
        if (IsLoadingTree) return;   // il caricamento in corso compone l'albero da sé
        var root = SftpRootNodes.FirstOrDefault(r => r.IsSourceRoot && r.Source?.Id == sourceId);
        if (root == null) return;

        if (SelectedSftpNode != null && (ReferenceEquals(SelectedSftpNode, root) || IsUnder(SelectedSftpNode, root)))
        {
            SelectedSftpNode = null;
            SftpPathInput = "";   // il percorso scelto apparteneva alla sorgente tolta
        }
        SftpRootNodes.Remove(root);
        UpdateSftpTreeColors(repaginate: false);
        StatusMessage = $"'{root.Source!.DisplayName}' tolta dall'albero.";
    }

    private static bool IsUnder(SftpNode node, SftpNode root)
    {
        for (var p = node.Parent; p != null; p = p.Parent) if (ReferenceEquals(p, root)) return true;
        return false;
    }

    /// <summary>Clic destro "Togli dall'albero" su una radice: le toglie anche la spunta nel menu Sorgenti.</summary>
    public void DiscardSourceRoot(SftpNode root)
    {
        if (!root.IsSourceRoot || root.Source == null) return;
        var source = Sources.FirstOrDefault(s => s.Id == root.Source.Id);
        if (source != null && source.IsEnabled) source.IsEnabled = false;   // salva la scelta e toglie la radice
        else RemoveSourceRoot(root.Source.Id);
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
            // Connessione propria col primo profilo salvato: non dipende da cosa è aperto nella tab SFTP
            ProfileId = SftpProfiles.FirstOrDefault(p => p.Id != VideoboxSftpPool.SharedProfileId)?.Id ?? VideoboxSftpPool.SharedProfileId,
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

    /// <summary>
    /// Le spunte delle sorgenti sono una sola scelta per tutte le categorie: cambiare categoria cambia
    /// solo i post, spunte e alberi caricati restano. (Prima ogni categoria aveva le sue spunte e,
    /// cambiandola, sparivano gli alberi delle sorgenti non spuntate.)
    /// </summary>
    private const string SourcesKey = "global";

    /// <summary>Spunte salvate (tutte attive se non c'è una scelta salvata). Si applicano all'avvio.</summary>
    private void ApplyCategorySources()
    {
        // Prima volta con la scelta unica: si parte da quella salvata per la categoria corrente
        var s = _settingsService.Current;
        var key = s.VideoboxCategoryDisabledSources.ContainsKey(SourcesKey) || s.VideoboxCategorySources.ContainsKey(SourcesKey)
            ? SourcesKey
            : (SelectedCategory?.Id ?? 0).ToString();
        _applyingCategorySources = true;
        try
        {
            if (s.VideoboxCategoryDisabledSources.TryGetValue(key, out var disabled))
                foreach (var src in Sources) src.IsEnabled = !disabled.Contains(src.Id);
            else
            {
                // Scelte salvate dalle versioni precedenti (elenco delle attive)
                var saved = s.VideoboxCategorySources.TryGetValue(key, out var ids) ? ids : null;
                foreach (var src in Sources) src.IsEnabled = saved == null || saved.Contains(src.Id);
            }
        }
        finally { _applyingCategorySources = false; }
        NotifySourcesChanged();
        // L'albero segue le spunte: via le radici delle sorgenti ora escluse
        foreach (var src in Sources.Where(x => !x.IsEnabled).ToList()) RemoveSourceRoot(src.Id);
    }

    private void SaveCategorySources()
    {
        const string key = SourcesKey;
        _settingsService.Current.VideoboxCategorySources[key] = Sources.Where(x => x.IsEnabled).Select(x => x.Id).ToList();
        _settingsService.Current.VideoboxCategoryDisabledSources[key] = Sources.Where(x => !x.IsEnabled).Select(x => x.Id).ToList();
        _settingsService.Save();
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

    /// <param name="keepOpenPost">
    /// Aggiornamento automatico (colori, post nuovi o modificati): il post aperto resta in lista e
    /// selezionato. false quando è l'utente a cambiare pagina, filtro o ricerca.
    /// </param>
    private void ApplyFilterAndPaginate(bool keepOpenPost = false)
    {
        // Il post aperto resta in lista e selezionato anche se, cambiando colore (es. diventa
        // verde dopo "Applica"), il filtro degli stati lo nasconderebbe o finirebbe in un'altra
        // pagina: prima si deselezionava e la tabella della mappatura spariva.
        var open = SelectedPost;
        bool openWasShown = keepOpenPost && open != null && CategoryPosts.Contains(open);

        var filtered = _allPosts.AsEnumerable();

        filtered = filtered.Where(p => IsStatusShown(p.Status) || (openWasShown && ReferenceEquals(p, open)));

        if (!string.IsNullOrWhiteSpace(SearchQuery))
            filtered = filtered.Where(p => p.Title.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) || (openWasShown && ReferenceEquals(p, open)));

        var filteredList = filtered.ToList();

        int totalPages = (int)Math.Ceiling((double)filteredList.Count / PageSize);
        PageNumbers.Clear();
        for (int i = 1; i <= totalPages; i++) PageNumbers.Add(i);

        if (openWasShown)
        {
            int idx = filteredList.IndexOf(open!);
            if (idx >= 0) _currentPage = idx / PageSize + 1;   // la pagina dove si trova ora
        }
        if (_currentPage > totalPages) _currentPage = Math.Max(1, totalPages);
        CurrentPageDisplay = _currentPage;

        var pageItems = filteredList.Skip((_currentPage - 1) * PageSize).Take(PageSize).ToList();
        _restoringSelection = true;
        try
        {
            CategoryPosts.Clear();
            foreach (var item in pageItems) CategoryPosts.Add(item);
            if (openWasShown && CategoryPosts.Contains(open!) && !ReferenceEquals(SelectedPost, open))
                SelectedPost = open;   // senza ricaricare la mappatura
        }
        finally { _restoringSelection = false; }

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
            _forumMaxId = 0;
            _postsCategoryId = SelectedCategory?.Id;
            var pagedData = await _apiService.GetVideosByCategoryAsync(_postsCategoryId, 1, cts.Token);
            ProcessPagedData(pagedData);
            try { _forumMaxId = await GetForumMaxIdAsync(SelectedCategory?.Id, pagedData, cts.Token); } catch (OperationCanceledException) { throw; } catch { }
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
            _forumMaxId = 0;
            _postsCategoryId = SelectedCategory?.Id;
            var firstPage = await _apiService.GetVideosByCategoryAsync(_postsCategoryId, 1, cts.Token);
            ProcessPagedData(firstPage);
            try { _forumMaxId = await GetForumMaxIdAsync(SelectedCategory?.Id, firstPage, cts.Token); } catch (OperationCanceledException) { throw; } catch { }

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
        OnPropertyChanged(nameof(ShowMappingHint));
        if (_restoringSelection) return;
        if (value != null) _ = LoadPostDetailsAsync(value.Id);
    }

    private async Task LoadPostDetailsAsync(int postId)
    {
        try
        {
            IsBusy = true;
            StatusMessage = $"Caricamento post {postId}...";
            VideoboxDto postData;
            try { postData = await _apiService.GetVideoByIdAsync(postId); }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var missing = _allPosts.Where(p => p.Id == postId).ToList();
                if (missing.Count > 0) RemovePosts(missing);
                StatusMessage = $"Il post {postId} non esiste più sul forum: tolto dalla lista.";
                return;
            }
            ApplyFreshPostData(postData);
            CurrentPostId = postData.id;
            CurrentPostTitle = postData.title;
            EditablePostTitle = postData.title;
            LastApplySummary = string.Empty;
            _currentPostAuthorId = postData.author?.id ?? 0;
            _currentPostOriginalCategoryId = ParseCategoryId(postData.category);

            var servers = postData.videos?.Values;
            var namedRemote = servers?.FirstOrDefault(v => v.vbs_name != null && v.vbs_name.Contains("Link Remoto", StringComparison.OrdinalIgnoreCase));
            var remoteServer = namedRemote ?? servers?.FirstOrDefault(v => v.online != null && v.online.Count > 0);
            if (namedRemote != null) _remoteServerId = namedRemote.id.ToString();

            if (remoteServer?.online == null || remoteServer.online.Count == 0)
            {
                // Post senza link: la mappatura resta possibile, i link vanno sul server "Link Remoto"
                // (quello del post se c'è, altrimenti quello visto negli altri post)
                _currentServerId = namedRemote?.id.ToString() ?? _remoteServerId;
                MappingItems.Clear();
                StatusMessage = "Il post non ha link: seleziona una cartella o un file nell'albero e premi Mappa.";
                return;
            }
            _currentServerId = remoteServer.id.ToString();

            FillMappingItems(remoteServer.online.Values);

            StatusMessage = $"Caricati {MappingItems.Count} episodi.";
        }
        catch (Exception ex) { StatusMessage = $"Errore: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    /// <summary>Tabella degli episodi con i link del post, nell'ordine del forum.</summary>
    private void FillMappingItems(IEnumerable<VideoboxEpisodeDto> links)
    {
        MappingItems.Clear();
        foreach (var ep in links.OrderBy(e => e.vbo_id))
        {
            var item = new EpisodeMappingItem { EpisodeId = ep.vbo_id, OldUrl = ep.vbo_url };
            item.EvaluateStatus(_settingsService.Current.AllValidStorageDomains);
            MappingItems.Add(item);
        }
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
        if (_refreshingPosts || IsLoadingPosts || CategoryPosts.Count == 0) return;
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
            var gone = new List<VideoboxPostItem>();
            foreach (var page in pages)
            {
                VideoboxPagedDto data;
                try { data = await _apiService.GetVideosByCategoryAsync(_postsCategoryId, page); }
                catch { return; }   // forum lento o non raggiungibile: si riprova al prossimo giro
                if (IsLoadingPosts) return;

                foreach (var dto in data.results)
                    if (ApplyFreshPostData(dto, recolor: false)) changed++;

                // Post eliminati (o spostati in un'altra categoria): la pagina del sito copre un
                // intervallo di numeri di post; quelli della lista in quell'intervallo che il sito
                // non restituisce più non esistono più qui.
                if (data.results.Count > 0)
                {
                    int min = data.results.Min(r => r.id), max = data.results.Max(r => r.id);
                    var ids = data.results.Select(r => r.id).ToHashSet();
                    gone.AddRange(_allPosts.Where(p => p.Id >= min && p.Id <= max && !ids.Contains(p.Id)));
                }
            }

            if (gone.Count > 0) RemovePosts(gone.Distinct().ToList());
            if (changed > 0) UpdateSftpTreeColors(repaginate: false);
            if (changed > 0 || gone.Count > 0)
            {
                var parts = new List<string>();
                if (changed > 0) parts.Add($"{changed} post aggiornati");
                if (gone.Count > 0) parts.Add($"{gone.Count} post eliminati");
                StatusMessage = $"{string.Join(", ", parts)} dal sito ({DateTime.Now:HH:mm}).";
            }
        }
        finally { _refreshingPosts = false; }
    }

    /// <summary>Toglie dalla lista post che sul forum non ci sono più (mantiene la selezione se possibile).</summary>
    private void RemovePosts(IReadOnlyCollection<VideoboxPostItem> posts)
    {
        var set = posts.ToHashSet();
        _allPosts.RemoveAll(p => set.Contains(p));

        var selected = SelectedPost;
        if (selected != null && set.Contains(selected))
        {
            // Il post aperto non esiste più: niente mappatura su un post eliminato
            SelectedPost = null;
            MappingItems.Clear();
            CurrentPostId = 0;
            CurrentPostTitle = "";
            selected = null;
        }

        UpdateSftpTreeColors(repaginate: true);
        if (selected != null && CategoryPosts.Contains(selected) && !ReferenceEquals(SelectedPost, selected))
        {
            _restoringSelection = true;
            try { SelectedPost = selected; } finally { _restoringSelection = false; }
        }
    }

    private bool _checkingNewPosts;
    private bool _restoringSelection;
    /// <summary>Categoria con cui sono stati caricati i post (gli aggiornamenti automatici usano questa).</summary>
    private int? _postsCategoryId;
    /// <summary>Numero del post più recente sul forum al momento del caricamento (0 = non noto).</summary>
    private int _forumMaxId;

    /// <summary>Numero del post più recente della categoria sul forum (l'API va dal più vecchio al più nuovo).</summary>
    private async Task<int> GetForumMaxIdAsync(int? category, VideoboxPagedDto first, CancellationToken ct = default)
    {
        if (first.results.Count == 0) return 0;
        bool ascending = first.results.Count < 2 || first.results[0].id < first.results[^1].id;
        if (!ascending) return first.results.Max(r => r.id);
        if (first.totalPages <= 1) return first.results.Max(r => r.id);
        var last = await _apiService.GetVideosByCategoryAsync(category, first.totalPages, ct);
        return last.results.Count > 0 ? last.results.Max(r => r.id) : first.results.Max(r => r.id);
    }

    /// <summary>
    /// Post creati sul forum dopo il caricamento: si leggono la prima e l'ultima pagina
    /// dell'API (i post nuovi finiscono in una delle due, secondo l'ordine del forum) e si
    /// aggiungono quelli con un numero più alto di tutti i post già caricati.
    /// </summary>
    private async Task AddNewPostsAsync()
    {
        if (_checkingNewPosts || IsLoadingPosts || IsBusy || _allPosts.Count == 0 || _forumMaxId == 0) return;
        _checkingNewPosts = true;
        try
        {
            int maxKnownId = _forumMaxId;
            var category = _postsCategoryId;

            VideoboxPagedDto first;
            try { first = await _apiService.GetVideosByCategoryAsync(category, 1); }
            catch { return; }

            var candidates = first.results.ToList();
            // Ordine crescente (i nuovi in fondo): serve anche l'ultima pagina
            bool ascending = first.results.Count < 2 || first.results[0].id < first.results[^1].id;
            if (ascending && first.totalPages > 1)
            {
                try { candidates.AddRange((await _apiService.GetVideosByCategoryAsync(category, first.totalPages)).results); }
                catch { return; }
            }

            var fresh = candidates.Where(d => d.id > maxKnownId)
                                  .GroupBy(d => d.id).Select(g => g.First())
                                  .OrderBy(d => d.id)
                                  .ToList();
            if (fresh.Count == 0 || IsLoadingPosts || _postsCategoryId != category) return;
            _forumMaxId = fresh.Max(d => d.id);

            foreach (var dto in fresh)
            {
                var item = new VideoboxPostItem { Id = dto.id, Title = dto.title, Url = dto.url, Links = VideoboxPostItem.ExtractLinks(dto) };
                if (ascending) _allPosts.Add(item);
                else _allPosts.Insert(0, item);
            }

            // Si ricostruisce la lista mostrata tenendo il post selezionato (senza ricaricarne la mappatura)
            var selected = SelectedPost;
            UpdateSftpTreeColors(repaginate: true);
            if (selected != null && CategoryPosts.Contains(selected) && !ReferenceEquals(SelectedPost, selected))
            {
                _restoringSelection = true;
                try { SelectedPost = selected; } finally { _restoringSelection = false; }
            }

            StatusMessage = fresh.Count == 1
                ? $"Nuovo post dal sito: {fresh[0].title}"
                : $"{fresh.Count} nuovi post dal sito ({DateTime.Now:HH:mm}).";
        }
        finally { _checkingNewPosts = false; }
    }

    [RelayCommand]
    public async Task LoadSftpTreeAsync()
    {
        var sources = Sources.Where(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.RootPath)).ToList();
        if (sources.Count == 0) { StatusMessage = "Nessuna sorgente attiva: aggiungile in Impostazioni e Domini o attivale dal menu Sorgenti."; return; }
        if (IsLoadingTree) return;

        // Si tengono le sorgenti già caricate (stessa configurazione) e si leggono solo le
        // nuove; se sono già tutte nell'albero, "Carica" le rilegge tutte (ricarica).
        var keep = SftpRootNodes
            .Where(r => r.IsSourceRoot && !r.HasLoadError && r.Source != null &&
                        sources.Any(s => s.Id == r.Source.Id && SourceSignature(s) == r.LoadedSignature))
            .ToList();
        var toLoad = sources.Where(s => keep.All(r => r.Source!.Id != s.Id)).ToList();
        if (toLoad.Count == 0)
        {
            var stale = SftpRootNodes.Where(r => !keep.Contains(r)).ToList();
            if (stale.Count > 0)
            {
                // Sono state solo tolte sorgenti: via quelle, le altre restano come sono
                foreach (var r in stale) SftpRootNodes.Remove(r);
                if (SelectedSftpNode != null && !IsInTree(SelectedSftpNode)) SelectedSftpNode = null;
                UpdateSftpTreeColors(repaginate: false);
                StatusMessage = $"Albero aggiornato: {Plural(stale.Count, "sorgente tolta", "sorgenti tolte")}, le altre restano caricate.";
                return;
            }
            keep.Clear(); toLoad = sources;   // tutto già caricato: Carica = ricarica
        }

        using var cts = _treeCts = new CancellationTokenSource();
        try
        {
            IsBusy = true;
            IsLoadingTree = true;
            _treeFoldersRead = 0;
            TreeFoldersTotal = 0;
            _treeCountsPending = toLoad.Count;
            _treeTotalUnknown = false;
            OnPropertyChanged(nameof(HasTreeTotal));
            TreeProgressText = "Connessione alle sorgenti...";
            // Un aggiornamento automatico in corso scriverebbe nella lista mentre la si
            // ricarica (era la causa dei doppioni): lo si invalida e si interrompe.
            _treeGeneration++;
            _fullTreeScanCts?.Cancel();
            _pendingTreeDirs.Clear();
            _pendingFullTreeScan = false;

            StatusMessage = keep.Count > 0
                ? $"Caricamento di {Plural(toLoad.Count, "sorgente nuova", "sorgenti nuove")} (già caricate: {keep.Count})..."
                : $"Caricamento di {Plural(toLoad.Count, "sorgente", "sorgenti")}...";
            // Restano solo le radici tenute (via quelle disattivate o da rileggere)
            for (int i = SftpRootNodes.Count - 1; i >= 0; i--)
                if (!keep.Contains(SftpRootNodes[i])) SftpRootNodes.RemoveAt(i);

            // Le sorgenti si leggono in parallelo (quelle sullo stesso server condividono la connessione)
            var roots = await Task.WhenAll(toLoad.Select(src => LoadSourceRootAsync(src, cts.Token)));
            cts.Token.ThrowIfCancellationRequested();

            // Nell'albero nell'ordine delle sorgenti; se durante il caricamento una sorgente è stata
            // esclusa (spunta tolta o cambio di categoria) non va aggiunta: albero e spunte coincidono
            var all = keep.Concat(roots)
                .Where(r => r.Source is { IsEnabled: true })
                .OrderBy(r => sources.FindIndex(s => s.Id == r.Source!.Id)).ToList();
            SftpRootNodes.Clear();
            foreach (var root in all) SftpRootNodes.Add(root);

            UpdateSftpTreeColors();
            int failed = roots.Count(r => r.HasLoadError);
            StatusMessage = failed == 0
                ? keep.Count > 0
                    ? $"Caricate: {Plural(toLoad.Count, "sorgente nuova", "sorgenti nuove")} ({Plural(_treeFoldersRead, "cartella", "cartelle")}); già caricate: {keep.Count}."
                    : $"Albero caricato: {toLoad.Count} sorgenti, {_treeFoldersRead} cartelle (aggiornamento automatico attivo)."
                : $"Albero caricato con {failed} sorgenti non raggiungibili su {toLoad.Count} (vedi l'albero).";
        }
        catch (OperationCanceledException)
        {
            // Un albero a metà darebbe colori sbagliati: restano solo le sorgenti già complete
            for (int i = SftpRootNodes.Count - 1; i >= 0; i--)
                if (!keep.Contains(SftpRootNodes[i])) SftpRootNodes.RemoveAt(i);
            UpdateSftpTreeColors(repaginate: false);
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

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    /// <summary>Configurazione che conta per l'albero e i link: se cambia, la sorgente va riletta.</summary>
    private static string SourceSignature(VideoboxSource s) =>
        $"{s.ProfileId}|{s.RootPath.Trim()}|{s.Domain.Trim()}|{s.PublicPath.Trim()}";

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
            IsSourceRoot = true,
            LoadedSignature = SourceSignature(source)
        };
        try
        {
            var service = await _pool.GetAsync(source.ProfileId, ct);
            _ = CountSourceFoldersAsync(service, source.RootPath, ct);   // totale per l'avanzamento
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

    /// <summary>Conta le cartelle della sorgente (sul server) e le somma al totale da leggere.</summary>
    private async Task CountSourceFoldersAsync(ISftpService service, string rootPath, CancellationToken ct)
    {
        int? count = null;
        try { count = await service.CountDirectoriesAsync(rootPath, ct); }
        catch { }
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsLoadingTree) return;
            if (count is > 0) TreeFoldersTotal += count.Value;
            else _treeTotalUnknown = true;
            _treeCountsPending = Math.Max(0, _treeCountsPending - 1);
            UpdateTreeProgressText();
        });
    }

    /// <summary>Prima riga di un messaggio d'errore (quelli del server SFTP sono su più righe).</summary>
    private static string FirstLine(string message) =>
        message.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim().TrimEnd('.') ?? message;

    /// <summary>Accoda il riallineamento di una cartella remota (dalla connessione della tab SFTP). Thread-safe.</summary>
    private void EnqueueTreeRefresh(string? serverKey, string directoryPath, bool recursive)
    {
        if (string.IsNullOrEmpty(serverKey)) return;
        Dispatcher.UIThread.Post(() =>
        {
            var path = serverKey + "\n" + NormalizeRemotePath(directoryPath);
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
        SftpRootNodes.Where(r => r.IsSourceRoot && !r.HasLoadError && r.Source != null &&
                                 (r.Source.ProfileId != VideoboxSftpPool.SharedProfileId || _pool.IsConnected(r.Source.ProfileId))).ToList();

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
                    foreach (var (key, recursive) in dirs)
                    {
                        // La modifica riguarda le sorgenti sullo stesso server (qualunque connessione
                        // l'abbia fatta) che contengono quel percorso.
                        var split = key.IndexOf('\n');
                        var server = key[..split];
                        var path = key[(split + 1)..];
                        foreach (var root in LoadedSourceRoots().Where(r => _pool.ServerKeyOf(r.Source!.ProfileId) == server))
                            changed |= await RefreshDirectoryWithRetryAsync(root, path, recursive);
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
                            // Connessione probabilmente bloccata: si chiude, al prossimo giro si riapre
                            if (ShouldResetConnection(ex)) await _pool.ResetAsync(source.ProfileId);
                            StatusMessage = $"Aggiornamento di '{source.DisplayName}' non riuscito: {FirstLine(ex.Message)}";
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally { _fullTreeScanCts = null; scanCts.Dispose(); }
            }

        }
        catch (Exception ex) { StatusMessage = $"Aggiornamento albero SFTP fallito: {FirstLine(ex.Message)}"; }
        finally
        {
            // Anche se una sorgente è andata in errore, le altre possono essere già cambiate:
            // i colori vanno ricalcolati comunque (prima restavano quelli vecchi, es. radice gialla).
            if (changed && !IsLoadingTree)
            {
                if (SelectedSftpNode != null && !IsInTree(SelectedSftpNode)) SelectedSftpNode = null;
                UpdateSftpTreeColors(repaginate: false);
                StatusMessage = $"Albero SFTP aggiornato ({DateTime.Now:HH:mm:ss}).";
            }
            _isRefreshingTree = false;
        }
    }

    /// <summary>
    /// Riallinea una cartella; se la connessione della sorgente non risponde (es. timeout di
    /// WinSCP) la si chiude, se ne apre una nuova e si riprova una volta. Un errore su una
    /// sorgente non ferma le altre.
    /// </summary>
    /// <summary>
    /// La connessione va chiusa e riaperta solo se è davvero bloccata. Non se il server ha risposto
    /// con un errore (es. "No such file" per una cartella appena eliminata), e mai mentre si carica
    /// l'albero: la stessa connessione serve al caricamento, che si interrompeva a metà
    /// ("Element session@0 already read to the end").
    /// </summary>
    private bool ShouldResetConnection(Exception ex) =>
        !IsLoadingTree && ex is not WinSCP.SessionRemoteException;

    private async Task<bool> RefreshDirectoryWithRetryAsync(SftpNode root, string path, bool recursive)
    {
        var source = root.Source!;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await RefreshTreeDirectoryAsync(root, path, recursive, _treeGeneration);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Il server ha risposto (es. cartella appena eliminata): la connessione è sana,
                // non si riprova e soprattutto non la si chiude
                if (!ShouldResetConnection(ex)) return false;
                await _pool.ResetAsync(source.ProfileId);
                if (attempt >= 2 || source.ProfileId == VideoboxSftpPool.SharedProfileId)
                {
                    StatusMessage = $"Aggiornamento di '{source.DisplayName}' non riuscito: {FirstLine(ex.Message)}";
                    return false;
                }
            }
        }
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
            UpdateTreeProgressText();
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

    /// <summary>
    /// Analisi dei link di un post: validità, host e percorso di ogni link. Si rifà solo quando
    /// cambiano i link del post o i domini validi (prima si rianalizzava ogni link più volte a
    /// ogni ricolorazione: con ~30.000 link e un albero grande l'interfaccia si fermava per
    /// 300-500 ms dopo ogni upload completato).
    /// </summary>
    private sealed class PostLinks
    {
        public List<string> Links = null!;
        public string DomainsStamp = "";
        public bool[] Valid = Array.Empty<bool>();
        public string[] Host = Array.Empty<string>();
        public string?[] Path = Array.Empty<string?>();
        public int ValidCount;
        /// <summary>Chiavi dei link per percorso pubblico della sorgente (calcolate una volta).</summary>
        public readonly Dictionary<string, string?[]> KeysByPublicPath = new(StringComparer.Ordinal);

        /// <summary>Cartella comune ai link del post per una sorgente ("" = nessuna), calcolata una volta.</summary>
        public readonly Dictionary<string, string> CommonDirBySource = new(StringComparer.Ordinal);

        public string?[] KeysFor(string publicPath)
        {
            if (!KeysByPublicPath.TryGetValue(publicPath, out var keys))
            {
                keys = new string?[Path.Length];
                for (int i = 0; i < Path.Length; i++)
                    if (Valid[i] && Path[i] != null) keys[i] = VideoboxSource.KeyFromLinkPath(Path[i]!, publicPath);
                KeysByPublicPath[publicPath] = keys;
            }
            return keys;
        }
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<VideoboxPostItem, PostLinks> _postLinks = new();

    private PostLinks AnalyzeLinks(VideoboxPostItem post, HashSet<string> validHosts, string domainsStamp)
    {
        if (_postLinks.TryGetValue(post, out var info) && ReferenceEquals(info.Links, post.Links) && info.DomainsStamp == domainsStamp)
            return info;

        int n = post.Links.Count;
        info = new PostLinks
        {
            Links = post.Links, DomainsStamp = domainsStamp,
            Valid = new bool[n], Host = new string[n], Path = new string?[n]
        };
        for (int i = 0; i < n; i++)
        {
            var url = post.Links[i];
            var host = LinkValidator.HostOf(url);
            info.Valid[i] = host.Length > 0 && validHosts.Contains(host);
            info.Host[i] = host.ToLowerInvariant();
            if (info.Valid[i])
            {
                info.ValidCount++;
                info.Path[i] = VideoboxSource.LinkPath(url);
            }
        }
        _postLinks.AddOrUpdate(post, info);
        return info;
    }

    private void UpdateSftpTreeColors(bool repaginate = true)
    {
        var domains = _settingsService.Current.AllValidStorageDomains.ToList();
        var domainsStamp = string.Join("|", domains);
        var validHosts = domains.Select(LinkValidator.CleanDomain).Where(d => d.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var treeSources = SftpRootNodes.Where(r => r.Source != null).Select(r => r.Source!).Distinct().ToList();

        // Domini e percorsi pubblici delle sorgenti: calcolati una volta per giro
        var configuredHosts = Sources.Select(x => x.DomainHost).Where(h => h.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Cambiano i domini delle sorgenti: le cartelle comuni ricordate non valgono più
        var hostsStamp = string.Join("|", configuredHosts.OrderBy(h => h, StringComparer.Ordinal)) + "#" + string.Join("|", treeSources.Select(x => x.Id + "=" + sourceHostOf(x)));
        static string sourceHostOf(VideoboxSource x) => x.DomainHost;
        var sourceHost = treeSources.ToDictionary(x => x, x => x.DomainHost);
        var sourcePub = treeSources.ToDictionary(x => x, x => x.NormalizedPublicPath);
        var sourcesByHost = new Dictionary<string, List<VideoboxSource>>(StringComparer.OrdinalIgnoreCase);
        List<VideoboxSource> SourcesForHost(string host)
        {
            // Come SourcesForUrl: con una sorgente dello stesso dominio contano solo quelle
            if (host.Length == 0 || !configuredHosts.Contains(host)) return treeSources;
            if (!sourcesByHost.TryGetValue(host, out var list))
                sourcesByHost[host] = list = treeSources.Where(x => string.Equals(sourceHost[x], host, StringComparison.OrdinalIgnoreCase)).ToList();
            return list;
        }

        _validKeysBySource.Clear();
        // Chiavi già tutte minuscole (link e nodi): confronto esatto, più veloce
        foreach (var src in treeSources) _validKeysBySource[src.Id] = new HashSet<string>(StringComparer.Ordinal);

        // Chiavi dei link validi di ogni post, per sorgente (servono anche a trovare la sua cartella)
        var keysByPost = new Dictionary<VideoboxPostItem, Dictionary<VideoboxSource, List<string>>>();
        foreach (var post in _allPosts)
        {
            var info = AnalyzeLinks(post, validHosts, domainsStamp);
            post.ValidLinkCount = info.ValidCount;
            post.BaseStatus = GetBasePostStatus(post.Links, info.ValidCount);
            post.Status = post.BaseStatus;
            if (treeSources.Count == 0) continue;

            Dictionary<VideoboxSource, List<string>>? perSource = null;
            for (int i = 0; i < info.Valid.Length; i++)
            {
                if (!info.Valid[i] || info.Path[i] == null) continue;
                foreach (var src in SourcesForHost(info.Host[i]))
                {
                    var key = info.KeysFor(sourcePub[src])[i];
                    if (string.IsNullOrEmpty(key)) continue;
                    _validKeysBySource[src.Id].Add(key);
                    perSource ??= new Dictionary<VideoboxSource, List<string>>();
                    if (!perSource.TryGetValue(src, out var keys)) perSource[src] = keys = new List<string>();
                    keys.Add(key);
                }
            }
            if (perSource != null) keysByPost[post] = perSource;
        }

        if (SftpRootNodes.Count > 0)
        {
            // Niente azzeramento preventivo: ogni nodo riceve il suo colore una volta sola e cambia
            // (avvisando l'albero) solo se il colore cambia davvero.
            foreach (var node in SftpRootNodes) ColorNodeRecursive(node);
            foreach (var node in SftpRootNodes) node.RefreshStatus();

            var foldersByKey = new Dictionary<string, SftpNode>(StringComparer.Ordinal);
            IndexFolders(SftpRootNodes, foldersByKey);

            foreach (var post in _allPosts)
            {
                if (post.Status is not (PostStatus.Valid or PostStatus.Mixed)) continue;
                if (!keysByPost.TryGetValue(post, out var perSource)) continue;

                var folder = FindFolderForPost(post, perSource, treeSources, foldersByKey, hostsStamp);
                if (folder == null || !folder.IsDirectory) continue;

                // Post giallo (alcuni link fuori dai domini impostati): anche la sua cartella,
                // le sottocartelle e le cartelle superiori diventano gialle, per confronto.
                if (post.Status == PostStatus.Mixed)
                {
                    folder.ApplyMixed();
                    folder.RaiseAncestors(NodeStatus.Mixed);
                    continue;
                }

                int totalFilesInFolder = CountFiles(folder);
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
        if (repaginate) ApplyFilterAndPaginate(keepOpenPost: true);
        else UpdateStatusCounts();
    }

    private static int CountFiles(SftpNode node)
    {
        if (!node.IsDirectory) return 1;
        int n = 0;
        foreach (var child in node.Children) n += CountFiles(child);
        return n;
    }

    private void ColorNodeRecursive(SftpNode node)
    {
        if (node.IsDirectory)
        {
            foreach (var child in node.Children) ColorNodeRecursive(child);
            return;
        }

        var source = node.Source;
        bool valid = source != null && _validKeysBySource.TryGetValue(source.Id, out var keys) && keys.Contains(node.KeyFor(source));
        node.BaseStatus = valid ? NodeStatus.Valid : NodeStatus.Invalid;
    }

    private void IndexFolders(IEnumerable<SftpNode> nodes, Dictionary<string, SftpNode> map)
    {
        foreach (var node in nodes)
        {
            if (!node.IsDirectory) continue;
            if (node.Source != null && !node.IsSourceRoot)
            {
                string key = node.KeyFor(node.Source);
                if (!string.IsNullOrEmpty(key)) map[$"{node.Source.Id}|{key}"] = node;
            }
            IndexFolders(node.Children, map);
        }
    }

    /// <summary>
    /// Cartella che contiene i file del post: la cartella comune ai suoi link validi,
    /// cercata in ogni sorgente caricata (la prima che la contiene). Le chiavi dei link
    /// arrivano già calcolate dalla ricolorazione.
    /// </summary>
    private SftpNode? FindFolderForPost(VideoboxPostItem post, Dictionary<VideoboxSource, List<string>> keysBySource,
                                        List<VideoboxSource> treeSources, Dictionary<string, SftpNode> foldersByKey, string hostsStamp)
    {
        _postLinks.TryGetValue(post, out var info);
        foreach (var src in treeSources)
        {
            if (!keysBySource.TryGetValue(src, out var keys)) continue;
            var stamp = src.Id + "|" + src.NormalizedPublicPath + "|" + hostsStamp;
            if (info == null || !info.CommonDirBySource.TryGetValue(stamp, out var dir))
            {
                dir = CommonDir(keys);
                info?.CommonDirBySource.TryAdd(stamp, dir);
            }
            if (dir.Length == 0) continue;
            if (foldersByKey.TryGetValue($"{src.Id}|{dir}", out var folder))
                return folder;
        }
        return null;
    }

    /// <summary>Cartella comune ai file dei link (le chiavi sono percorsi "cartella/file").</summary>
    private static string CommonDir(List<string> keys)
    {
        string[]? common = null;
        int len = 0;
        foreach (var key in keys)
        {
            int idx = key.LastIndexOf('/');
            if (idx <= 0) continue;
            var d = key.Substring(0, idx).Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (common == null) { common = d; len = d.Length; continue; }
            int i = 0;
            while (i < len && i < d.Length && string.Equals(common[i], d[i], StringComparison.OrdinalIgnoreCase)) i++;
            len = i;
        }
        return common == null || len == 0 ? "" : string.Join("/", common.Take(len));
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
        // Post senza link: si crea una riga per ogni file della cartella (vedi sotto)
        string? warning = null;
        try
        {
            IsBusy = true; StatusMessage = $"Mappatura in corso: {SftpPathInput}...";

            var source = SourceForPath(SftpPathInput)
                         ?? throw new InvalidOperationException("il percorso non appartiene a nessuna sorgente");
            var service = await _pool.GetAsync(source.ProfileId);
            var files = await service.ListDirectoryAsync(SftpPathInput);
            var videoFiles = files.Where(f => !f.IsDirectory).OrderBy(f => f.Name, new NaturalSortComparer()).ToList();

            if (videoFiles.Count == 0) { StatusMessage = "Nessun file trovato nella cartella."; return; }

            // Righe aggiunte da una mappatura precedente (file in più, mai state sul forum): si
            // ripartono da zero, così con una cartella più piccola non restano righe vuote
            for (int i = MappingItems.Count - 1; i >= 0; i--)
                if (string.IsNullOrEmpty(MappingItems[i].OldUrl)) MappingItems.RemoveAt(i);

            int oldRows = MappingItems.Count;
            if (videoFiles.Count > oldRows)
            {
                while (MappingItems.Count < videoFiles.Count)
                    MappingItems.Add(new EpisodeMappingItem { EpisodeId = 0, OldUrl = "" });
                if (oldRows > 0)
                    warning = $"Attenzione: {Plural(videoFiles.Count - oldRows, "link aggiunto", "link aggiunti")} per i file in più ({videoFiles.Count} file, {oldRows} link nel post).";
            }
            else if (videoFiles.Count < oldRows)
            {
                for (int i = videoFiles.Count; i < oldRows; i++)
                {
                    MappingItems[i].SftpFileName = null;
                    MappingItems[i].NewUrl = null;
                    MappingItems[i].IsSelectedForUpdate = false;
                }
                warning = $"Attenzione: {Plural(oldRows - videoFiles.Count, "link in eccesso verrà rimosso", "link in eccesso verranno rimossi")} se applichi ({videoFiles.Count} file, {oldRows} link nel post).";
            }

            for (int i = 0; i < videoFiles.Count; i++)
            {
                var file = videoFiles[i];
                MappingItems[i].SftpFileName = file.Name;
                MappingItems[i].NewUrl = source.BuildPublicUrl(file.FullPath);
                MappingItems[i].IsSelectedForUpdate = true;
                MappingItems[i].EvaluateStatus(_settingsService.Current.AllValidStorageDomains);
            }

            // Prima il messaggio "Mappatura in corso" restava anche a mappatura finita
            StatusMessage = warning ?? $"Mappatura completata: {Plural(videoFiles.Count, "file abbinato", "file abbinati")}.";
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

        // I file hanno la precedenza: le righe rimaste senza file perdono la spunta e con
        // "Applica" vengono tolte, qualunque sia il loro link (come per una cartella con meno
        // file). Per un film in più parti, il file successivo riprende la spunta sulla sua riga.
        int removed = 0;
        foreach (var row in MappingItems)
        {
            if (ReferenceEquals(row, target) || row.HasNew || !row.IsSelectedForUpdate) continue;
            row.IsSelectedForUpdate = false;
            removed++;
        }

        // Si passa alla riga successiva: più file (es. film in due parti) si mappano in sequenza
        int idx = MappingItems.IndexOf(target);
        SelectedMappingItem = idx >= 0 && idx < MappingItems.Count - 1 ? MappingItems[idx + 1] : null;
        StatusMessage = removed > 0
            ? $"Mappato il file {file.Name} sulla riga {idx + 1}; {Plural(removed, "link senza file verrà tolto", "link senza file verranno tolti")} se applichi."
            : $"Mappato il file {file.Name} sulla riga {idx + 1}.";
    }

    // ============================================================
    //  SELEZIONE MULTIPLA (Ctrl / Maiusc) NELLA TABELLA
    // ============================================================

    /// <summary>Righe selezionate nella tabella (aggiornate dalla finestra).</summary>
    public List<EpisodeMappingItem> SelectedMappingRows { get; } = new();

    /// <summary>Spunta "Usa" su tutte le righe selezionate (clic destro).</summary>
    [RelayCommand] private void UseSelectedRows() => SetUseForSelected(true);

    /// <summary>Toglie la spunta "Usa" a tutte le righe selezionate (clic destro).</summary>
    [RelayCommand] private void SkipSelectedRows() => SetUseForSelected(false);

    /// <summary>Spazio: se almeno una riga selezionata è senza spunta le spunta tutte, altrimenti le toglie.</summary>
    public void ToggleSelectedRows()
    {
        if (SelectedMappingRows.Count == 0) return;
        SetUseForSelected(SelectedMappingRows.Any(r => !r.IsSelectedForUpdate));
    }

    private void SetUseForSelected(bool use)
    {
        var rows = SelectedMappingRows.ToList();
        if (rows.Count == 0) { StatusMessage = "Seleziona prima le righe (Ctrl o Maiusc per selezionarne più di una)."; return; }
        foreach (var r in rows) r.IsSelectedForUpdate = use;
        StatusMessage = use
            ? $"{Plural(rows.Count, "riga verrà inviata", "righe verranno inviate")} al forum."
            : $"{Plural(rows.Count, "riga verrà tolta", "righe verranno tolte")} dal post se applichi.";
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

        // Si scambia l'abbinamento (file e nuovo link) e con lui la spunta "Usa": prima la spunta
        // restava dov'era e una riga senza file poteva risultare "resta com'è" invece di "tolto"
        var tempFile = item1.SftpFileName; var tempUrl = item1.NewUrl; var tempUse = item1.IsSelectedForUpdate;
        item1.SftpFileName = item2.SftpFileName; item1.NewUrl = item2.NewUrl; item1.IsSelectedForUpdate = item2.IsSelectedForUpdate;
        item2.SftpFileName = tempFile; item2.NewUrl = tempUrl; item2.IsSelectedForUpdate = tempUse;

        item1.EvaluateStatus(_settingsService.Current.AllValidStorageDomains);
        item2.EvaluateStatus(_settingsService.Current.AllValidStorageDomains);
    }

    /// <summary>Link oltre i quali il forum, con le impostazioni PHP predefinite, inizia a scartarne.</summary>
    private const int MaxLinksPerRequest = 996;

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

            // Il forum (PHP) accetta al massimo "max_input_vars" campi per richiesta, di
            // solito 1000: titolo, categoria e autore + un campo per link. Oltre, i link in
            // più vengono scartati senza errore: si avvisa prima e si controlla dopo.
            if (finalUrls.Count > MaxLinksPerRequest)
                StatusMessage = $"Invio di {finalUrls.Count} link: il forum potrebbe accettarne solo ~{MaxLinksPerRequest} (limite max_input_vars di PHP)...";

            // Conteggi dell'anteprima prima dell'invio (la tabella poi si ricarica dal forum)
            int oldBefore = OldLinkCount, newBefore = NewLinkCount, filesBefore = SftpFileCount;
            // File SFTP abbinati a ogni link, per ritrovarli nella tabella ricaricata
            var fileByUrl = MappingItems
                .Where(m => m.IsSelectedForUpdate && !string.IsNullOrEmpty(m.NewUrl) && !string.IsNullOrEmpty(m.SftpFileName))
                .GroupBy(m => m.NewUrl!).ToDictionary(g => g.Key, g => g.First().SftpFileName!);

            var title = string.IsNullOrWhiteSpace(EditablePostTitle) ? CurrentPostTitle : EditablePostTitle.Trim();
            await _apiService.UpdateVideoUrlsAsync(CurrentPostId, _currentServerId, title, catId, _currentPostAuthorId, finalUrls);

            // Il post appena modificato si aggiorna subito nella lista, e la tabella mostra i link
            // come li ha salvati il forum. Se la risposta non li contiene (server del post appena
            // creato, risposta non ancora aggiornata) si riprova una volta e, in ultimo, la tabella
            // mostra i link appena inviati: non deve mai svuotarsi dopo "Applica".
            var (updatedPost, savedServer) = await ReadSavedLinksAsync(finalUrls);
            ApplyFreshPostData(updatedPost, recolor: false);
            if (!string.IsNullOrEmpty(updatedPost.title))
            {
                CurrentPostTitle = updatedPost.title;
                EditablePostTitle = updatedPost.title;
            }
            var sent = finalUrls.ToHashSet(StringComparer.Ordinal);
            if (savedServer?.online is { Count: > 0 } online)
            {
                _currentServerId = savedServer.id.ToString();
                FillMappingItems(online.Values);
                // Ogni riga dice se il suo link è tra quelli salvati sul forum
                foreach (var m in MappingItems) m.SavedOnForum = sent.Contains(m.OldUrl) ? true : null;
                // Inviati ma non tornati dal forum: si aggiungono in fondo, segnati come non confermati
                var savedSet = online.Values.Select(e => e.vbo_url).ToHashSet(StringComparer.Ordinal);
                foreach (var u in finalUrls.Where(u => !savedSet.Contains(u)))
                {
                    var missing = new EpisodeMappingItem { OldUrl = u, SavedOnForum = false };
                    missing.EvaluateStatus(_settingsService.Current.AllValidStorageDomains);
                    MappingItems.Add(missing);
                }
            }
            else
            {
                FillMappingItems(finalUrls.Select((u, i) => new VideoboxEpisodeDto { vbo_id = i, vbo_url = u }));
                foreach (var m in MappingItems) m.SavedOnForum = false;
            }
            foreach (var m in MappingItems)
                if (fileByUrl.TryGetValue(m.OldUrl, out var file)) m.SftpFileName = file;
            UpdateMappingCounts();

            UpdateSftpTreeColors();

            // Quanti link ha davvero salvato il forum
            int saved = savedServer?.online?.Count ?? (updatedPost.videos == null ? -1 : 0);
            LastApplySummary = $"Ultimo Applica ({DateTime.Now:HH:mm}): vecchi link {oldBefore} · nuovi link {newBefore} · " +
                               $"file SFTP {filesBefore} · salvati sul forum {(saved >= 0 ? saved : finalUrls.Count)}";
            if (saved >= 0 && saved < finalUrls.Count)
            {
                StatusMessage = $"ATTENZIONE: il forum ha salvato solo {saved} link su {finalUrls.Count}. " +
                                "Il server del forum limita i campi per richiesta (PHP max_input_vars, di solito 1000): " +
                                "va alzato nel php.ini del forum (es. max_input_vars = 10000) e poi riapplicato.";
                return;
            }
            StatusMessage = $"Aggiornamento completato: {Plural(finalUrls.Count, "link salvato", "link salvati")} sul forum.";
        }
        catch (Exception ex) 
        { 
            StatusMessage = $"ERRORE: {ex.Message}"; 
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Rilegge il post dopo "Applica" e trova il server con i link salvati: quello usato, poi
    /// "Link Remoto", poi il primo con link. Se i link inviati non ci sono ancora, riprova una volta.
    /// </summary>
    private async Task<(VideoboxDto Post, VideoboxServerDto? Server)> ReadSavedLinksAsync(List<string> sent)
    {
        VideoboxDto post = new();
        VideoboxServerDto? server = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0) await Task.Delay(1500);
            post = await _apiService.GetVideoByIdAsync(CurrentPostId);
            var servers = post.videos?.Values;
            server = servers?.FirstOrDefault(v => v.id.ToString() == _currentServerId && v.online is { Count: > 0 })
                     ?? servers?.FirstOrDefault(v => v.vbs_name != null && v.vbs_name.Contains("Link Remoto", StringComparison.OrdinalIgnoreCase) && v.online is { Count: > 0 })
                     ?? servers?.FirstOrDefault(v => v.online is { Count: > 0 });
            if (sent.Count == 0) break;
            var saved = server?.online?.Values.Select(e => e.vbo_url).ToHashSet(StringComparer.Ordinal);
            if (saved != null && sent.Any(saved.Contains)) break;
        }
        return (post, server);
    }

    /// <summary>
    /// Cambia solo il titolo del post: i link si rileggono dal forum e si rimandano uguali,
    /// la mappatura in tabella non viene inviata e la categoria resta quella del post.
    /// </summary>
    [RelayCommand]
    public async Task RenamePostAsync()
    {
        var title = EditablePostTitle?.Trim() ?? "";
        if (CurrentPostId == 0) { StatusMessage = "Seleziona prima un post."; return; }
        if (title.Length == 0) { StatusMessage = "Il titolo non può essere vuoto."; return; }
        if (title == CurrentPostTitle) { StatusMessage = "Il titolo è già questo."; return; }
        try
        {
            IsBusy = true; StatusMessage = "Rinomina del post...";
            var post = await _apiService.GetVideoByIdAsync(CurrentPostId);
            var server = post.videos?.Values.FirstOrDefault(v => v.id.ToString() == _currentServerId);
            var links = server?.online?.Values.OrderBy(e => e.vbo_id).Select(e => e.vbo_url).ToList() ?? new List<string>();
            if (links.Count > MaxLinksPerRequest)
            {
                StatusMessage = $"Rinomina non eseguita: il post ha {links.Count} link e il forum potrebbe salvarne solo ~{MaxLinksPerRequest} (limite max_input_vars di PHP).";
                return;
            }
            int catId = _currentPostOriginalCategoryId > 0 ? _currentPostOriginalCategoryId : SelectedCategory?.Id ?? 0;

            await _apiService.UpdateVideoUrlsAsync(CurrentPostId, _currentServerId, title, catId, _currentPostAuthorId, links);

            var updated = await _apiService.GetVideoByIdAsync(CurrentPostId);
            ApplyFreshPostData(updated, recolor: false);
            var old = CurrentPostTitle;
            CurrentPostTitle = updated.title;
            EditablePostTitle = updated.title;
            StatusMessage = $"Post rinominato: \"{old}\" → \"{updated.title}\".";
        }
        catch (Exception ex) { StatusMessage = $"ERRORE nella rinomina: {ex.Message}"; }
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