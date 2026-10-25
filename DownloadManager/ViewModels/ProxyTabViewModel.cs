using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Anime.AnimeWorld;
using DownloadManager.Services.Anime.AnimeSaturn;
using DownloadManager.Services.Domains;
using DownloadManager.Services.Proxy;
using DownloadManager.Services.Streaming.StreamingCommunity;

namespace DownloadManager.ViewModels;

/// <summary>Una scheda della tab Proxy: la lista di un provider oppure l'archivio di tutti i proxy.</summary>
public partial class ProxyListTab : ObservableObject
{
    public string? Key { get; init; }
    public string Title { get; init; } = "";
    public bool IsArchive => Key == null;
    [ObservableProperty] private string _countsText = "";

    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasOk))] private int _okCount;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasUntested))] private int _untestedCount;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasFailed))] private int _failedCount;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(IsEmpty))] private int _totalCount;
    [ObservableProperty] private bool _hasActive;
    public bool HasOk => OkCount > 0;
    public bool HasUntested => UntestedCount > 0;
    public bool HasFailed => FailedCount > 0;
    public bool IsEmpty => !IsArchive && OkCount + UntestedCount + FailedCount == 0;
}

/// <summary>Casella "presente nella lista X" nel pannello dettagli.</summary>
public partial class ProxyMembershipItem : ObservableObject
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    [ObservableProperty] private bool _isMember;
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusOk), nameof(StatusFailed), nameof(StatusExcluded), nameof(StatusUntested), nameof(StatusText))]
    private ProxyEntryStatus? _status;
    [ObservableProperty] private bool _isActive;
    public bool StatusOk => Status == ProxyEntryStatus.Ok;
    public bool StatusFailed => Status == ProxyEntryStatus.Failed;
    public bool StatusExcluded => Status == ProxyEntryStatus.Excluded;
    public bool StatusUntested => Status == ProxyEntryStatus.Untested;
    public string StatusText => Status is { } s ? ProxyListBadge.Label(s) : "";
    public Action<ProxyMembershipItem>? Changed { get; init; }
    public bool Syncing { get; set; }
    partial void OnIsMemberChanged(bool value) { if (!Syncing) Changed?.Invoke(this); }
}

public partial class ProxyTabViewModel : ViewModelBase
{
    private readonly ProxyService _proxyService;
    private readonly IAnimeWorldService _animeWorldService;
    private readonly IAnimeSaturnService _animeSaturnService;
    private readonly IStreamingCommunityService _streamingCommunityService;
    private readonly SettingsService _settings;
    private readonly SharedState _shared;

    private CancellationTokenSource? _proxyTestCts;

    private static readonly (string Key, string Title, string Short)[] ProviderLists =
    {
        (ProviderKeys.AnimeWorld, "AnimeWorld", "AW"),
        (ProviderKeys.AnimeSaturn, "AnimeSaturn", "AS"),
        (ProviderKeys.StreamingCommunity, "StreamingCommunity", "SC"),
        (ProviderKeys.Cb01, "CB01 · Maxstream", "CB"),
        (ProviderKeys.Downloads, "Download diretti", "DL"),
    };

    private const string FilterAll = "Tutti";
    private const string FilterOk = "Funzionanti";
    private const string FilterFailed = "Falliti";
    private const string FilterUntested = "Da testare";
    private const string FilterExcluded = "Esclusi";
    private const string FilterNoLists = "In nessuna lista";
    private const string FilterUseless = "Inutilizzabili";

    /// <summary>Tutti i proxy dell'archivio (dal DB).</summary>
    public ObservableCollection<ProxyConfig> Proxies { get; } = new();

    /// <summary>Proxy mostrati nella scheda e nel filtro correnti.</summary>
    [ObservableProperty] private ObservableCollection<ProxyConfig> _visibleProxies = new();

    public ObservableCollection<ProxyListTab> Tabs { get; } = new();
    [ObservableProperty] private ProxyListTab? _selectedTab;

    public ObservableCollection<string> Filters { get; } = new();
    [ObservableProperty] private string _selectedFilter = FilterAll;
    [ObservableProperty] private string _searchText = "";

    [ObservableProperty] private ProxyConfig? _selectedProxy;
    public ObservableCollection<ProxyMembershipItem> Memberships { get; } = new();

    [ObservableProperty] private bool _isProxyBusy;
    [ObservableProperty] private string _proxyStatusMessage = string.Empty;

    public string VisibleCountText => $"{VisibleProxies.Count} di {Proxies.Count}";

    // ==== Riquadro "proxy attivo" del provider aperto ====
    [ObservableProperty] private ProxyConfig? _activeProxy;
    [ObservableProperty] private string _activeDetailText = "";
    [ObservableProperty] private bool _isActivating;
    [ObservableProperty] private string _activatingText = "";
    [ObservableProperty] private string _activationResultText = "";
    [ObservableProperty] private bool _activationResultOk;
    [ObservableProperty] private bool _hasActivationResult;
    [ObservableProperty] private string _filterHint = "";
    [ObservableProperty] private string _archiveSummary = "";
    public bool HasActiveProxy => ActiveProxy != null;
    public bool NoActiveProxy => IsProviderTab && ActiveProxy == null;
    partial void OnActiveProxyChanged(ProxyConfig? value)
    {
        OnPropertyChanged(nameof(HasActiveProxy));
        OnPropertyChanged(nameof(NoActiveProxy));
    }

    /// <summary>Interruttore "usa il proxy" del provider della scheda aperta.</summary>
    public bool CurrentUseProxy
    {
        get => CurrentKey switch
        {
            ProviderKeys.AnimeWorld => UseProxyAnimeWorld,
            ProviderKeys.AnimeSaturn => UseProxyAnimeSaturn,
            ProviderKeys.StreamingCommunity => UseProxyStreamingCommunity,
            ProviderKeys.Cb01 => UseProxyCb01,
            ProviderKeys.Downloads => UseProxyDownloads,
            _ => false
        };
        set
        {
            switch (CurrentKey)
            {
                case ProviderKeys.AnimeWorld: UseProxyAnimeWorld = value; break;
                case ProviderKeys.AnimeSaturn: UseProxyAnimeSaturn = value; break;
                case ProviderKeys.StreamingCommunity: UseProxyStreamingCommunity = value; break;
                case ProviderKeys.Cb01: UseProxyCb01 = value; break;
                case ProviderKeys.Downloads: UseProxyDownloads = value; break;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(UseProxyHint));
            NotifyAllProvidersInBackground();
        }
    }

    public string UseProxyHint => CurrentUseProxy
        ? $"{CurrentListTitle} passa dal proxy attivo qui sotto."
        : $"Spento: {CurrentListTitle} usa la connessione diretta, qualunque proxy sia attivo.";
    public bool IsProviderTab => SelectedTab is { IsArchive: false };
    public bool IsArchiveTab => SelectedTab is { IsArchive: true };
    public string CurrentListTitle => SelectedTab?.Title ?? "";
    private string? CurrentKey => SelectedTab?.Key;

    public int ProxyTestConcurrency
    {
        get => _settings.Current.ProxyTestConcurrency;
        set { _settings.Current.ProxyTestConcurrency = Math.Clamp(value, 1, 30); _settings.Save(); OnPropertyChanged(); }
    }

    public bool AutoExcludeFailed
    {
        get => _settings.Current.ProxyAutoExcludeFailed;
        set { _settings.Current.ProxyAutoExcludeFailed = value; _settings.Save(); OnPropertyChanged(); }
    }

    // ==== Interruttori globali "usa proxy per..." ====
    public bool UseProxyAnimeWorld
    {
        get => _settings.Current.UseProxyForAnimeWorld;
        set { _settings.Current.UseProxyForAnimeWorld = value; _settings.Save(); OnPropertyChanged(); _shared.NotifyActiveProxyChanged(); }
    }
    public bool UseProxyAnimeSaturn
    {
        get => _settings.Current.UseProxyForAnimeSaturn;
        set { _settings.Current.UseProxyForAnimeSaturn = value; _settings.Save(); OnPropertyChanged(); _shared.NotifyActiveProxyChanged(); }
    }
    public bool UseProxyStreamingCommunity
    {
        get => _settings.Current.UseProxyForStreamingCommunity;
        set { _settings.Current.UseProxyForStreamingCommunity = value; _settings.Save(); OnPropertyChanged(); _shared.NotifyActiveProxyChanged(); }
    }
    public bool UseProxyDownloads
    {
        get => _settings.Current.UseProxyForDownloads;
        set { _settings.Current.UseProxyForDownloads = value; _settings.Save(); OnPropertyChanged(); _shared.NotifyActiveProxyChanged(); }
    }
    public bool UseProxyCb01
    {
        get => _settings.Current.UseProxyForCb01;
        set { _settings.Current.UseProxyForCb01 = value; _settings.Save(); OnPropertyChanged(); _shared.NotifyActiveProxyChanged(); }
    }

    public ProxyTabViewModel(
        ProxyService proxyService,
        IAnimeWorldService animeWorldService,
        IAnimeSaturnService animeSaturnService,
        IStreamingCommunityService streamingCommunityService,
        DomainResolver domainResolver,
        SettingsService settings,
        SharedState shared)
    {
        _proxyService = proxyService;
        _animeWorldService = animeWorldService;
        _animeSaturnService = animeSaturnService;
        _streamingCommunityService = streamingCommunityService;
        _settings = settings;
        _shared = shared;

        foreach (var (key, title, _) in ProviderLists)
            Tabs.Add(new ProxyListTab { Key = key, Title = title });
        Tabs.Add(new ProxyListTab { Key = null, Title = "Archivio" });
        SelectedTab = Tabs[0];

        _proxyService.ActiveProxyChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            RefreshView(keepList: true);
            _shared.NotifyActiveProxyChanged();
        });

        _ = Task.Run(ProxyLoadAllAsync);
    }

    // ============================================================
    //  VISTA
    // ============================================================

    partial void OnSelectedTabChanged(ProxyListTab? value)
    {
        var filters = value is { IsArchive: true }
            ? new[] { FilterAll, FilterNoLists, FilterUseless }
            : new[] { FilterAll, FilterOk, FilterFailed, FilterUntested, FilterExcluded };
        Filters.Clear();
        foreach (var f in filters) Filters.Add(f);
        SelectedFilter = FilterAll;

        OnPropertyChanged(nameof(IsProviderTab));
        OnPropertyChanged(nameof(IsArchiveTab));
        OnPropertyChanged(nameof(CurrentListTitle));
        OnPropertyChanged(nameof(CurrentUseProxy));
        OnPropertyChanged(nameof(UseProxyHint));
        HasActivationResult = false;
        RefreshView();
    }

    partial void OnSelectedFilterChanged(string value) => RefreshView();
    partial void OnSearchTextChanged(string value) => RefreshView();
    partial void OnVisibleProxiesChanged(ObservableCollection<ProxyConfig> value) => OnPropertyChanged(nameof(VisibleCountText));
    partial void OnSelectedProxyChanged(ProxyConfig? value) => RebuildMemberships();

    /// <summary>Ricalcola righe visibili, testi per riga e contatori delle schede.</summary>
    private void RefreshView(bool keepList = false)
    {
        var key = CurrentKey;
        foreach (var p in Proxies) UpdateRowDisplay(p, key);
        UpdateTabCounts();
        if (!keepList)
        {
            var search = SearchText?.Trim() ?? "";
            var rows = Proxies.Where(p =>
                MatchesFilter(p, key) &&
                (search.Length == 0 ||
                 p.Host.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                 p.Name.Contains(search, StringComparison.OrdinalIgnoreCase)));
            // Nella lista di un provider: prima l'attivo, poi i funzionanti dal più veloce
            if (key != null)
                rows = rows.OrderByDescending(p => p.EntryFor(key)?.IsActive == true)
                           .ThenBy(p => StatusOrder(p.EntryFor(key)?.Status))
                           .ThenBy(p => p.EntryFor(key)?.LatencyMs ?? int.MaxValue);
            VisibleProxies = new ObservableCollection<ProxyConfig>(rows);
        }
        OnPropertyChanged(nameof(VisibleCountText));
        RebuildMemberships();
        UpdateActiveCard();
        UpdateHints();
    }

    private void UpdateActiveCard()
    {
        OnPropertyChanged(nameof(NoActiveProxy));
        if (CurrentKey is not { } key) { ActiveProxy = null; ActiveDetailText = ""; return; }
        var active = _proxyService.GetActiveFor(key);
        ActiveProxy = active;
        if (active?.EntryFor(key) is not { } e) { ActiveDetailText = ""; return; }
        var parts = new List<string> { active.ProtocolText };
        if (e.LatencyMs is { } ms) parts.Add($"{ms} ms");
        if (e.LastTestedUtc is { } t) parts.Add($"verificato {t.ToLocalTime():dd/MM HH:mm}");
        if (active.Auth == ProxyAuthType.UserPassword) parts.Add($"utente {active.Username}");
        ActiveDetailText = string.Join(" · ", parts);
        OnPropertyChanged(nameof(NoActiveProxy));
    }

    private void UpdateHints()
    {
        FilterHint = SelectedFilter switch
        {
            FilterExcluded => $"Esclusi = proxy tolti dalla lista {CurrentListTitle} (falliti al test o esclusi a mano). Non vengono più testati né riaggiunti importando; restano validi per gli altri provider. Spuntali e premi \"Ripristina\" per riportarli in lista.",
            FilterFailed => "Falliti all'ultimo test ma ancora in lista (succede se l'esclusione automatica è spenta). \"Escludi falliti\" li toglie.",
            FilterUntested => "Mai provati su questo provider: \"Testa da testare\" li prova tutti.",
            FilterNoLists => "Proxy rimasti solo nell'archivio: spunta le liste nel pannello a destra per usarli di nuovo.",
            FilterUseless => "Falliti o esclusi in tutte le loro liste: \"Pulisci archivio\" li elimina.",
            _ => ""
        };
        var none = Proxies.Count(p => p.Lists.Count == 0);
        var useless = Proxies.Count(p => p.Lists.Count > 0 && p.Lists.Values.All(e => e.Status is ProxyEntryStatus.Failed or ProxyEntryStatus.Excluded));
        ArchiveSummary = $"{Proxies.Count} proxy in archivio · {none} in nessuna lista · {useless} inutilizzabili";
    }

    private static int StatusOrder(ProxyEntryStatus? s) => s switch
    {
        ProxyEntryStatus.Ok => 0,
        ProxyEntryStatus.Untested => 1,
        ProxyEntryStatus.Failed => 2,
        _ => 3
    };

    private bool MatchesFilter(ProxyConfig p, string? key)
    {
        if (key == null)
        {
            return SelectedFilter switch
            {
                FilterNoLists => p.Lists.Count == 0,
                FilterUseless => p.Lists.Count > 0 && p.Lists.Values.All(e => e.Status is ProxyEntryStatus.Failed or ProxyEntryStatus.Excluded),
                _ => true
            };
        }

        var e = p.EntryFor(key);
        if (e == null) return false;
        return SelectedFilter switch
        {
            FilterOk => e.Status == ProxyEntryStatus.Ok,
            FilterFailed => e.Status == ProxyEntryStatus.Failed,
            FilterUntested => e.Status == ProxyEntryStatus.Untested,
            FilterExcluded => e.Status == ProxyEntryStatus.Excluded,
            _ => e.Status != ProxyEntryStatus.Excluded
        };
    }

    /// <summary>Messaggio dell'ultimo test senza le emoji delle versioni precedenti.</summary>
    private static string CleanResult(string r) => r.TrimStart('✅', '❌', '⛔', '⏳', ' ', '️');

    private static void UpdateRowDisplay(ProxyConfig p, string? key)
    {
        p.ListBadges = ProviderLists
            .Select(l => p.Lists.TryGetValue(l.Key, out var e)
                ? new ProxyListBadge { Short = l.Short, Title = l.Title, Status = e.Status, IsActive = e.IsActive && e.IsInList }
                : new ProxyListBadge { Short = l.Short, Title = l.Title, IsAbsent = true })
            .ToList();

        // Riga secondaria: protocollo, utente, nome scelto (se non è l'indirizzo stesso)
        var detail = new List<string> { p.ProtocolText };
        if (p.Auth == ProxyAuthType.UserPassword && p.Username.Length > 0) detail.Add($"utente {p.Username}");
        var customName = p.Name.Trim();
        if (customName.Length > 0 && customName != "Proxy" && customName != p.DisplayText && customName != $"{p.Host}:{p.Port}")
            detail.Add(customName);

        if (key == null)
        {
            p.ShowStatus = false;
            p.TestResult = p.Lists.Count == 0 ? "in nessuna lista" : "";
            p.IsActiveForSelectedProvider = false;
            p.LastTestedText = "";
            p.LatencyText = "";
            p.DetailText = string.Join(" · ", detail);
            return;
        }

        var entry = p.EntryFor(key);
        p.ShowStatus = true;
        p.StatusForSelected = entry?.Status ?? ProxyEntryStatus.Untested;
        p.IsActiveForSelectedProvider = entry is { IsActive: true, IsInList: true };
        var result = entry == null ? "" : CleanResult(entry.LastResult);
        // Per chi funziona l'esito è già nella latenza; per gli altri si mostra il motivo
        if (entry is { Status: not ProxyEntryStatus.Ok } && result.Length > 0) detail.Add(result);
        p.DetailText = string.Join(" · ", detail);
        p.TestResult = result;
        p.LastTestedText = entry?.LastTestedUtc is { } t ? t.ToLocalTime().ToString("dd/MM HH:mm") : "";
        p.LatencyText = entry?.LatencyMs is { } ms ? $"{ms} ms" : "";
    }

    private void UpdateTabCounts()
    {
        foreach (var tab in Tabs)
        {
            if (tab.IsArchive)
            {
                tab.TotalCount = Proxies.Count;
                tab.CountsText = $"{Proxies.Count} proxy";
                continue;
            }
            int ok = 0, untested = 0, failed = 0;
            bool active = false;
            foreach (var p in Proxies)
            {
                if (p.EntryFor(tab.Key!) is not { } e) continue;
                if (e.Status == ProxyEntryStatus.Ok) ok++;
                else if (e.Status == ProxyEntryStatus.Untested) untested++;
                else if (e.Status == ProxyEntryStatus.Failed) failed++;
                active |= e.IsActive && e.IsInList;
            }
            tab.OkCount = ok;
            tab.UntestedCount = untested;
            tab.FailedCount = failed;
            tab.TotalCount = ok + untested + failed;
            tab.HasActive = active;
            tab.CountsText = $"{ok} ok · {untested} da testare" + (failed > 0 ? $" · {failed} falliti" : "");
        }
    }

    private bool _syncingMemberships;

    private void RebuildMemberships()
    {
        var p = SelectedProxy;
        _syncingMemberships = true;
        try
        {
            if (Memberships.Count == 0)
                foreach (var (key, title, _) in ProviderLists)
                    Memberships.Add(new ProxyMembershipItem { Key = key, Title = title, Changed = OnMembershipChanged });

            foreach (var m in Memberships)
            {
                m.Syncing = true;
                var e = p?.EntryFor(m.Key);
                m.IsMember = e is { IsInList: true };
                m.Status = e?.Status;
                m.IsActive = e is { IsActive: true, IsInList: true };
                m.StateText = e?.LatencyMs is { } ms && e.Status == ProxyEntryStatus.Ok ? $"{ms} ms" : "";
                m.Syncing = false;
            }
        }
        finally { _syncingMemberships = false; }
    }

    private async void OnMembershipChanged(ProxyMembershipItem item)
    {
        if (_syncingMemberships || SelectedProxy is not { } p) return;
        await _proxyService.SetMembershipAsync(p, item.Key, item.IsMember);
        RefreshView();
        ProxyStatusMessage = item.IsMember
            ? $"{p.Host}:{p.Port} aggiunto alla lista {item.Title}."
            : $"{p.Host}:{p.Port} tolto dalla lista {item.Title}.";
    }

    // ============================================================
    //  CARICAMENTO / CRUD
    // ============================================================

    [RelayCommand]
    private async Task ProxyLoadAllAsync()
    {
        try
        {
            await _proxyService.LoadAllAsync();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Proxies.Clear();
                foreach (var p in _proxyService.All)
                {
                    p.IsSelected = false;
                    Proxies.Add(p);
                }
                RefreshView();
                _shared.NotifyActiveProxyChanged();
                ProxyStatusMessage = Proxies.Count == 0
                    ? "Nessun proxy: importa una lista o aggiungine uno."
                    : $"{Proxies.Count} proxy nell'archivio.";
            });
        }
        catch (Exception ex) { ProxyStatusMessage = $"Errore: {ex.Message}"; }
    }

    [RelayCommand]
    private void ProxyNew()
    {
        var p = new ProxyConfig { Name = "Nuovo proxy" };
        // Nella scheda di un provider entra in quella lista; dall'archivio in tutte
        foreach (var (key, _, _) in ProviderLists)
            if (CurrentKey == null || CurrentKey == key) p.GetOrAddEntry(key);

        Proxies.Insert(0, p);
        SearchText = "";
        SelectedFilter = FilterAll;
        RefreshView();
        SelectedProxy = p;
        ProxyStatusMessage = "Nuovo proxy: compila i campi, scegli le liste e premi Salva.";
    }

    [RelayCommand]
    private async Task ProxySaveAsync()
    {
        if (SelectedProxy is not { } p) return;
        if (string.IsNullOrWhiteSpace(p.Host)) { ProxyStatusMessage = "Inserisci l'host del proxy."; return; }

        var duplicate = Proxies.FirstOrDefault(x => x != p && x.IdentityKey == p.IdentityKey);
        if (duplicate != null) { ProxyStatusMessage = $"Esiste già: {duplicate.Name} ({duplicate.DisplayText})."; return; }

        try
        {
            IsProxyBusy = true;
            if (p.Name is "" or "Nuovo proxy") p.Name = p.DisplayText;
            await _proxyService.SaveAsync(p);
            RefreshView(keepList: true);
            ProxyStatusMessage = $"Proxy '{p.Name}' salvato.";
            _shared.NotifyActiveProxyChanged();
        }
        catch (Exception ex) { ProxyStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsProxyBusy = false; }
    }

    /// <summary>Importazione (da file o testo incollato) verso le liste scelte nella finestra di importazione.</summary>
    public async Task ImportAsync(string content, IReadOnlyCollection<string> providers)
    {
        var (parsed, errors) = ProxyFileParser.Parse(content);
        if (parsed.Count == 0)
        {
            ProxyStatusMessage = errors.Count > 0 ? $"Nessun proxy valido ({errors.Count} righe non riconosciute)." : "Nessun proxy trovato.";
            return;
        }

        IsProxyBusy = true;
        try
        {
            var r = await _proxyService.ImportAsync(parsed, providers, errors.Count);
            foreach (var p in _proxyService.All.Where(p => !Proxies.Contains(p))) Proxies.Add(p);
            RefreshView();

            var where = providers.Count == ProviderLists.Length
                ? "tutte le liste"
                : string.Join(", ", ProviderLists.Where(l => providers.Contains(l.Key)).Select(l => l.Title));
            var parts = new List<string> { $"{r.Added} nuovi", $"{r.AlreadyPresent} già presenti" };
            if (r.KeptExcluded > 0) parts.Add($"{r.KeptExcluded} lasciati esclusi");
            if (r.InvalidLines > 0) parts.Add($"{r.InvalidLines} righe non valide");
            ProxyStatusMessage = $"Importazione in {where}: {string.Join(" · ", parts)}.";
        }
        catch (Exception ex) { ProxyStatusMessage = $"Errore importazione: {ex.Message}"; }
        finally { IsProxyBusy = false; }
    }

    // ============================================================
    //  SELEZIONE
    // ============================================================

    private List<ProxyConfig> Checked() => VisibleProxies.Where(p => p.IsSelected).ToList();
    public int CheckedCount => VisibleProxies.Count(p => p.IsSelected);

    [RelayCommand]
    private void ProxySelectAll()
    {
        foreach (var p in VisibleProxies) p.IsSelected = true;
        ProxyStatusMessage = $"Selezionati {VisibleProxies.Count}.";
    }

    [RelayCommand]
    private void ProxyDeselectAll()
    {
        foreach (var p in Proxies) p.IsSelected = false;
        ProxyStatusMessage = "Selezione azzerata.";
    }

    // ============================================================
    //  TEST
    // ============================================================

    /// <summary>Default: solo quelli mai testati in questa lista.</summary>
    [RelayCommand]
    private async Task ProxyTestUntestedAsync()
    {
        if (CurrentKey is not { } key) return;
        var targets = Proxies.Where(p => p.EntryFor(key)?.Status == ProxyEntryStatus.Untested).ToList();
        if (targets.Count == 0) { ProxyStatusMessage = $"Nessun proxy da testare in {CurrentListTitle}."; return; }
        await RunTestBatchAsync(targets, key, "da testare");
    }

    [RelayCommand]
    private async Task ProxyTestSelectedAsync()
    {
        if (CurrentKey is not { } key) return;
        var targets = Checked().Where(p => ProxyService.IsInList(p, key)).ToList();
        if (targets.Count == 0) { ProxyStatusMessage = "Nessun proxy selezionato (gli esclusi non si testano)."; return; }
        await RunTestBatchAsync(targets, key, "selezionati");
    }

    [RelayCommand]
    private async Task ProxyRetestAllAsync()
    {
        if (CurrentKey is not { } key) return;
        var targets = Proxies.Where(p => ProxyService.IsInList(p, key)).ToList();
        if (targets.Count == 0) { ProxyStatusMessage = $"La lista {CurrentListTitle} è vuota."; return; }
        await RunTestBatchAsync(targets, key, "tutti");
    }

    // ==== Test di tutti i provider in fila ====
    [ObservableProperty] private bool _isTestingAllProviders;
    private bool _stopAllProviders;
    private string _batchPrefix = "";

    /// <summary>Ritesta i proxy di tutte le liste, un provider dopo l'altro (gli esclusi restano fuori).</summary>
    [RelayCommand]
    private async Task ProxyTestAllProvidersAsync()
    {
        if (_proxyTestCts != null || IsTestingAllProviders) { ProxyStatusMessage = "Test già in corso."; return; }
        _stopAllProviders = false;
        IsTestingAllProviders = true;
        var summary = new List<string>();
        int okTot = 0, totalTot = 0;
        try
        {
            for (int i = 0; i < ProviderLists.Length; i++)
            {
                if (_stopAllProviders) break;
                var (key, title, shortName) = ProviderLists[i];
                var targets = Proxies.Where(p => ProxyService.IsInList(p, key)).ToList();
                if (targets.Count == 0) { summary.Add($"{shortName} vuota"); continue; }
                _batchPrefix = $"[{i + 1}/{ProviderLists.Length}] ";
                var (ok, total) = await RunTestBatchAsync(targets, key, "tutti");
                okTot += ok; totalTot += total;
                summary.Add($"{shortName} {ok}/{total}");
            }
        }
        finally
        {
            _batchPrefix = "";
            IsTestingAllProviders = false;
        }
        ProxyStatusMessage = (_stopAllProviders ? "Test interrotto. Funzionanti: " : $"Test di tutti i provider completato: {okTot} funzionanti su {totalTot} · ")
                             + string.Join(" · ", summary);
    }

    private async Task<(int Ok, int Total)> RunTestBatchAsync(List<ProxyConfig> targets, string key, string label)
    {
        if (_proxyTestCts != null) { ProxyStatusMessage = "Test già in corso."; return (0, 0); }

        _proxyTestCts = new CancellationTokenSource();
        var token = _proxyTestCts.Token;
        var title = ProviderLists.First(l => l.Key == key).Title;
        bool autoExclude = AutoExcludeFailed;

        IsProxyBusy = true;
        int total = targets.Count, done = 0, ok = 0, ko = 0;
        bool activeChanged = false;
        var pending = new List<ProxyConfig>();
        try
        {
            var gate = new SemaphoreSlim(ProxyTestConcurrency);
            var tasks = targets.Select(async proxy =>
            {
                await gate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    Dispatcher.UIThread.Post(() => { proxy.IsTesting = true; if (CurrentKey == key) proxy.TestResult = "Test in corso..."; });

                    var (success, msg, latency) = await _proxyService.TestForProviderAsync(proxy, key, token);

                    // L'esito si applica sul thread UI: niente corse sul proxy attivo
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        activeChanged |= _proxyService.ApplyTestResult(proxy, key, success, msg, latency, autoExclude);
                        proxy.IsTesting = false;
                        UpdateRowDisplay(proxy, CurrentKey);
                        done++;
                        if (success) ok++; else ko++;
                        lock (pending) pending.Add(proxy);
                        ProxyStatusMessage = $"{_batchPrefix}Test {label} su {title}: {done}/{total} · {ok} ok · {ko} falliti";
                    });

                    if (pending.Count >= 50) await FlushAsync();
                }
                catch (OperationCanceledException) { Dispatcher.UIThread.Post(() => proxy.IsTesting = false); }
                finally { gate.Release(); }
            }).ToList();

            try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
            await FlushAsync();
            if (token.IsCancellationRequested) _stopAllProviders = true;

            ProxyStatusMessage = token.IsCancellationRequested
                ? $"Test interrotto: {done}/{total} · {ok} ok · {ko} falliti"
                : $"Test {label} su {title} completato: {ok} funzionanti su {total}" +
                  (ko > 0 ? (autoExclude ? $", {ko} esclusi." : $", {ko} falliti.") : ".");
            return (ok, done);
        }
        finally
        {
            _proxyTestCts?.Dispose();
            _proxyTestCts = null;
            IsProxyBusy = false;
            RefreshView();
            if (activeChanged)
            {
                _shared.NotifyActiveProxyChanged();
                NotifyAllProvidersInBackground();
            }
        }

        async Task FlushAsync()
        {
            List<ProxyConfig> batch;
            lock (pending) { batch = pending.ToList(); pending.Clear(); }
            try { await _proxyService.SaveManyAsync(batch); } catch { }
        }
    }

    [RelayCommand]
    private void ProxyStopTest()
    {
        _stopAllProviders = true;
        if (_proxyTestCts == null) return;
        _proxyTestCts.Cancel();
        ProxyStatusMessage = "Interruzione test...";
    }

    // ============================================================
    //  ATTIVAZIONE / ROTAZIONE
    // ============================================================

    [RelayCommand]
    private async Task ProxyActivateAsync()
    {
        if (SelectedProxy is not { } p || CurrentKey is not { } key) return;
        if (IsActivating) return;
        if (!ProxyService.IsInList(p, key))
        {
            ShowActivationResult(false, $"{p.Host}:{p.Port} è escluso da {CurrentListTitle}: ripristinalo prima di attivarlo.");
            return;
        }

        var title = CurrentListTitle;
        BeginActivation($"Verifico {p.Host}:{p.Port} su {title}...");
        try
        {
            // Si verifica sempre prima di attivare: un proxy "funzionante" di ieri può essere morto oggi
            p.IsTesting = true;
            var (ok, msg, latency) = await _proxyService.TestForProviderAsync(p, key);
            p.IsTesting = false;
            _proxyService.ApplyTestResult(p, key, ok, msg, latency, autoExclude: false);
            if (!ok)
            {
                await _proxyService.SaveAsync(p);
                RefreshView(keepList: true);
                ShowActivationResult(false, $"{p.Host}:{p.Port} non funziona su {title}: {CleanResult(msg)}. Il proxy attivo non è cambiato.");
                return;
            }

            p.EntryFor(key)!.ManuallyDisabled = false;
            await CommitActiveAsync(p, key);
            ShowActivationResult(true, $"{p.Host}:{p.Port} attivo su {title} ({latency} ms).");
        }
        catch (Exception ex) { ShowActivationResult(false, $"Errore: {ex.Message}"); }
        finally { p.IsTesting = false; IsActivating = false; }
    }

    /// <summary>
    /// Riprova insieme i migliori funzionanti (più quelli senza latenza nota) e attiva quello che
    /// risponde più in fretta adesso: la latenza salvata può essere vecchia.
    /// </summary>
    [RelayCommand]
    private async Task ProxyActivateFastestAsync()
    {
        if (CurrentKey is not { } key) return;
        if (IsActivating) return;
        var title = CurrentListTitle;

        var ok = Proxies.Where(p => p.EntryFor(key) is { Status: ProxyEntryStatus.Ok }).ToList();
        if (ok.Count == 0)
        {
            var untested = Proxies.Count(p => p.EntryFor(key)?.Status == ProxyEntryStatus.Untested);
            ShowActivationResult(false, untested > 0
                ? $"Nessun proxy funzionante in {title}: premi \"Testa da testare\" ({untested} in attesa) e riprova."
                : $"Nessun proxy funzionante in {title}: importa una lista.");
            return;
        }

        // Ordine: latenza nota crescente, poi quelli mai misurati
        var ordered = ok.OrderBy(p => p.EntryFor(key)!.LatencyMs ?? int.MaxValue).ToList();
        const int batchSize = 20;
        const int maxRounds = 3;

        BeginActivation($"Cerco il proxy più veloce per {title}...");
        var tested = new List<ProxyConfig>();
        try
        {
            for (int round = 0; round < maxRounds; round++)
            {
                var batch = ordered.Skip(round * batchSize).Take(batchSize).ToList();
                if (batch.Count == 0) break;

                ActivatingText = $"Misuro {batch.Count} proxy in parallelo su {title}" +
                                 (round > 0 ? $" (giro {round + 1})..." : "...");
                foreach (var p in batch) p.IsTesting = true;

                var results = await Task.WhenAll(batch.Select(async p =>
                {
                    var r = await _proxyService.TestForProviderAsync(p, key);
                    return (Proxy: p, r.Ok, r.Message, r.LatencyMs);
                }));

                foreach (var r in results)
                {
                    r.Proxy.IsTesting = false;
                    // Niente attivazione automatica qui: decide il confronto qui sotto
                    var entry = r.Proxy.EntryFor(key)!;
                    var wasDisabled = entry.ManuallyDisabled;
                    entry.ManuallyDisabled = true;
                    _proxyService.ApplyTestResult(r.Proxy, key, r.Ok, r.Message, r.LatencyMs, AutoExcludeFailed);
                    entry.ManuallyDisabled = wasDisabled;
                    tested.Add(r.Proxy);
                }

                var best = results.Where(r => r.Ok).OrderBy(r => r.LatencyMs).FirstOrDefault();
                if (best.Proxy == null) continue;

                best.Proxy.EntryFor(key)!.ManuallyDisabled = false;
                await CommitActiveAsync(best.Proxy, key, tested);
                var working = results.Count(r => r.Ok);
                ShowActivationResult(true,
                    $"{best.Proxy.Host}:{best.Proxy.Port} attivo su {title}: {best.LatencyMs} ms, il più rapido tra {working} che rispondono ora.");
                return;
            }

            await _proxyService.SaveManyAsync(tested);
            RefreshView();
            ShowActivationResult(false, $"Nessuno dei {tested.Count} proxy provati risponde ora su {title}. Rilancia il test della lista.");
        }
        catch (Exception ex) { ShowActivationResult(false, $"Errore: {ex.Message}"); }
        finally
        {
            foreach (var p in ordered) p.IsTesting = false;
            IsActivating = false;
        }
    }

    private void BeginActivation(string text)
    {
        HasActivationResult = false;
        ActivatingText = text;
        IsActivating = true;
        ProxyStatusMessage = text;
    }

    private void ShowActivationResult(bool ok, string text)
    {
        ActivationResultOk = ok;
        ActivationResultText = text;
        HasActivationResult = true;
        ProxyStatusMessage = text;
    }

    private async Task CommitActiveAsync(ProxyConfig p, string key, IEnumerable<ProxyConfig>? alsoSave = null)
    {
        var changed = _proxyService.MakeOnlyActive(p, key).ToList();
        if (alsoSave != null) changed.AddRange(alsoSave.Where(x => !changed.Contains(x)));
        await _proxyService.SaveManyAsync(changed);
        RefreshView(keepList: true);
        _shared.NotifyActiveProxyChanged();
        NotifyAllProvidersInBackground();
    }

    [RelayCommand]
    private async Task ProxyDeactivateAsync()
    {
        if (CurrentKey is not { } key) return;
        var active = Proxies.Where(p => p.EntryFor(key)?.IsActive == true).ToList();
        foreach (var p in active)
        {
            var e = p.EntryFor(key)!;
            e.IsActive = false;
            e.ManuallyDisabled = true;
        }
        await _proxyService.SaveManyAsync(active);
        RefreshView(keepList: true);
        _shared.NotifyActiveProxyChanged();
        NotifyAllProvidersInBackground();
        ShowActivationResult(true, active.Count > 0
            ? $"Proxy disattivato: {CurrentListTitle} ora usa la connessione diretta."
            : $"Nessun proxy attivo per {CurrentListTitle}.");
    }

    [RelayCommand]
    private async Task ProxyRotateNextAsync()
    {
        if (CurrentKey is not { } key) return;
        IsProxyBusy = true;
        try
        {
            var next = await _proxyService.RotateNextForAsync(key);
            RefreshView(keepList: true);
            if (next == null) { ShowActivationResult(false, $"Nessun altro proxy funzionante in {CurrentListTitle}: testa la lista."); return; }
            _shared.NotifyActiveProxyChanged();
            NotifyAllProvidersInBackground();
            ShowActivationResult(true, $"{CurrentListTitle} ora usa {next.Host}:{next.Port}.");
        }
        finally { IsProxyBusy = false; }
    }

    // ============================================================
    //  RIMOZIONE: escludi / ripristina / togli dalle liste / elimina
    // ============================================================

    [RelayCommand]
    private async Task ProxyExcludeSelectedAsync()
    {
        if (CurrentKey is not { } key) return;
        var n = await _proxyService.ExcludeAsync(Checked(), key);
        RefreshView();
        ProxyStatusMessage = n == 0 ? "Nessun proxy selezionato." : $"{n} proxy esclusi da {CurrentListTitle}: non verranno più testati.";
    }

    [RelayCommand]
    private async Task ProxyExcludeFailedAsync()
    {
        if (CurrentKey is not { } key) return;
        var failed = Proxies.Where(p => p.EntryFor(key)?.Status == ProxyEntryStatus.Failed).ToList();
        var n = await _proxyService.ExcludeAsync(failed, key);
        RefreshView();
        ProxyStatusMessage = n == 0 ? $"Nessun proxy fallito in {CurrentListTitle}." : $"{n} proxy falliti esclusi da {CurrentListTitle}.";
    }

    [RelayCommand]
    private async Task ProxyRestoreSelectedAsync()
    {
        if (CurrentKey is not { } key) return;
        var n = await _proxyService.RestoreAsync(Checked(), key);
        RefreshView();
        ProxyStatusMessage = n == 0 ? "Seleziona dei proxy esclusi (filtro \"Esclusi\")." : $"{n} proxy di nuovo in {CurrentListTitle}, da testare.";
    }

    [RelayCommand]
    private async Task ProxyRemoveFromAllListsAsync()
    {
        var n = await _proxyService.RemoveFromAllListsAsync(Checked());
        RefreshView();
        if (n > 0) _shared.NotifyActiveProxyChanged();
        ProxyStatusMessage = n == 0 ? "Nessun proxy selezionato." : $"{n} proxy tolti da tutte le liste: restano nell'archivio.";
    }

    /// <summary>Chiamato dopo la conferma: elimina definitivamente i proxy spuntati.</summary>
    public async Task DeleteCheckedAsync()
    {
        var targets = Checked();
        if (targets.Count == 0) { ProxyStatusMessage = "Nessun proxy selezionato."; return; }
        await DeleteAsync(targets);
        ProxyStatusMessage = $"{targets.Count} proxy eliminati definitivamente.";
    }

    /// <summary>Chiamato dopo la conferma: elimina i proxy falliti o esclusi in tutte le loro liste.</summary>
    public async Task CleanupArchiveAsync()
    {
        var useless = _proxyService.FindUseless();
        if (useless.Count == 0) { ProxyStatusMessage = "Archivio già pulito."; return; }
        await DeleteAsync(useless);
        ProxyStatusMessage = $"Archivio pulito: {useless.Count} proxy inutilizzabili eliminati.";
    }

    public int CheckedForDeletionCount => Checked().Count;
    public int UselessCount => _proxyService.FindUseless().Count;

    private async Task DeleteAsync(List<ProxyConfig> targets)
    {
        IsProxyBusy = true;
        try
        {
            bool hadActive = targets.Any(p => p.Lists.Values.Any(e => e.IsActive));
            await _proxyService.DeleteManyAsync(targets);
            var set = targets.ToHashSet();
            for (int i = Proxies.Count - 1; i >= 0; i--) if (set.Contains(Proxies[i])) Proxies.RemoveAt(i);
            if (SelectedProxy != null && set.Contains(SelectedProxy)) SelectedProxy = null;
            RefreshView();
            if (hadActive) { _shared.NotifyActiveProxyChanged(); NotifyAllProvidersInBackground(); }
        }
        finally { IsProxyBusy = false; }
    }

    // ============================================================
    //  HELPER
    // ============================================================

    /// <summary>Chiude i browser dei provider (ripartono col nuovo proxy). È lento: in background.</summary>
    private void NotifyAllProvidersInBackground() => _ = Task.Run(async () =>
    {
        try { await _animeWorldService.NotifyProxyChangedAsync(); } catch { }
        try { await _animeSaturnService.NotifyProxyChangedAsync(); } catch { }
        try { await _streamingCommunityService.NotifyProxyChangedAsync(); } catch { }
    });

    /// <summary>Liste per la finestra di importazione: (chiave, nome).</summary>
    public static IReadOnlyList<(string Key, string Title)> ImportTargets => ProviderLists.Select(l => (l.Key, l.Title)).ToList();
}
