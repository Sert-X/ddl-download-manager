using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Services.Sftp;
using Microsoft.Extensions.Logging;

namespace DownloadManager.ViewModels;

/// <summary>
/// Una scheda del pannello "File remoti": un server SFTP aperto, con la sua connessione e la
/// sua cartella. La prima scheda aperta usa la connessione principale dell'app (quella che
/// usano anche Organizza file e Videobox); le altre hanno ciascuna una connessione propria.
/// </summary>
public partial class RemoteServerTab : ObservableObject
{
    public RemoteServerTab(SftpConfig config, ISftpService service, bool isMain)
    {
        Config = config;
        Service = service;
        IsMain = isMain;
    }

    public SftpConfig Config { get; }
    public ISftpService Service { get; }

    /// <summary>Usa la connessione principale dell'app.</summary>
    public bool IsMain { get; }

    public string Name => Config.Name;
    public string Host => Config.Host;
    public string TabTooltip => $"{Config.Name} ({Config.Host})";

    /// <summary>
    /// Controllo dei file a 0 byte di questo server: ogni scheda ha il suo, con risultati
    /// separati (una stessa cartella può essere rossa su un server e non sull'altro).
    /// </summary>
    public ZeroByteCheckerService? Checker { get; set; }

    /// <summary>Cartella aperta (per la scheda principale vale quella condivisa con le altre tab).</summary>
    public string Path { get; set; } = "/";

    [ObservableProperty] private bool _isActive;
}

public partial class SftpTabViewModel
{
    private ILoggerFactory _loggerFactory = null!;

    /// <summary>Server aperti nel pannello di destra, uno per scheda.</summary>
    public ObservableCollection<RemoteServerTab> RemoteTabs { get; } = new();

    [ObservableProperty] private RemoteServerTab? _activeRemoteTab;
    /// <summary>Pannello "connetti un server" aperto con il pulsante "+".</summary>
    [ObservableProperty] private bool _isAddingRemoteTab;
    [ObservableProperty] private SftpConfig? _remoteConnectProfile;
    [ObservableProperty] private bool _isRemoteConnecting;

    public bool HasRemoteTabs => RemoteTabs.Count > 0;
    public bool ShowRemoteConnect => ActiveRemoteTab == null || IsAddingRemoteTab;
    public bool ShowRemoteBrowser => !ShowRemoteConnect;
    /// <summary>Il check dei file a 0 byte lavora sulla connessione principale.</summary>
    public bool IsActiveTabMain => ActiveRemoteTab?.IsMain == true;
    public bool CanCancelAddTab => IsAddingRemoteTab && ActiveRemoteTab != null;

    /// <summary>Connessione della scheda aperta (per tutte le operazioni del pannello di destra).</summary>
    private ISftpService Remote => ActiveRemoteTab?.Service ?? _sftpService;

    /// <summary>Server della scheda aperta.</summary>
    private SftpConfig? RemoteConfig => ActiveRemoteTab?.Config;

    public bool IsRemoteConnected => ActiveRemoteTab != null && ActiveRemoteTab.Service.IsConnected;

    /// <summary>Connessione della scheda aperta (per "Sposta in..." nel pannello di destra).</summary>
    public ISftpService ActiveRemoteService => Remote;

    /// <summary>Cartella aperta sulla connessione principale (anche se a destra è aperta un'altra scheda).</summary>
    public string MainRemotePath => _shared.SftpCurrentRemotePath;

    private void InitTabs(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        SftpProfiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSftpProfiles));
        LeftTabs.CollectionChanged += (_, _) =>
        {
            SyncSharedServers();
            NotifyConnectionBadges();
            OnPropertyChanged(nameof(HasLeftTabs));
        };
        RemoteTabs.CollectionChanged += (_, _) =>
        {
            SyncSharedServers();
            OnPropertyChanged(nameof(HasRemoteTabs));
            NotifyConnectionBadges();
        };
    }

    partial void OnActiveRemoteTabChanged(RemoteServerTab? oldValue, RemoteServerTab? newValue)
    {
        // La cartella della scheda che si lascia resta salvata nella scheda
        if (oldValue != null) { oldValue.IsActive = false; if (!oldValue.IsMain) oldValue.Path = _remoteTabPath; }
        if (newValue != null)
        {
            newValue.IsActive = true;
            if (!newValue.IsMain) _remoteTabPath = newValue.Path;
        }

        IsAddingRemoteTab = false;
        _lastListedRemotePath = null;
        SftpRemoteEntries.Clear();
        SelectedRemoteEntries.Clear();
        RemoteBrowserStats = "";
        OnPropertyChanged(nameof(SftpCurrentRemotePath));
        NotifyRemoteTabLayout();
        SyncZeroByteState();   // stato del controllo 0 byte della scheda aperta
        if (newValue != null) _ = SftpRefreshInternalAsync(silent: false);
    }

    partial void OnIsAddingRemoteTabChanged(bool value)
    {
        // Durante "+ Connetti server" nessuna scheda risulta aperta
        if (ActiveRemoteTab != null) ActiveRemoteTab.IsActive = !value;
        if (value && RemoteConnectProfile == null)
            RemoteConnectProfile = SftpProfiles.FirstOrDefault(p => RemoteTabs.All(t => t.Config.ServerKey != p.ServerKey))
                                   ?? SftpProfiles.FirstOrDefault();
        NotifyRemoteTabLayout();
    }

    private void NotifyRemoteTabLayout()
    {
        OnPropertyChanged(nameof(ShowRemoteConnect));
        OnPropertyChanged(nameof(ShowRemoteBrowser));
        OnPropertyChanged(nameof(IsActiveTabMain));
        OnPropertyChanged(nameof(IsRemoteConnected));
        OnPropertyChanged(nameof(CanCancelAddTab));
        OnPropertyChanged(nameof(ActiveRemoteService));
    }

    private void NotifyConnectionBadges()
    {
        OnPropertyChanged(nameof(ShowSftpGray));
        OnPropertyChanged(nameof(ShowSftpRed));
        OnPropertyChanged(nameof(ShowSftpYellow));
        OnPropertyChanged(nameof(ShowSftpGreen));
    }

    /// <summary>Cartella della scheda aperta, se non è quella principale (che usa il percorso condiviso).</summary>
    private string _remoteTabPath = "/";

    [RelayCommand] private void AddRemoteTab() => IsAddingRemoteTab = true;
    [RelayCommand] private void CancelAddRemoteTab() => IsAddingRemoteTab = false;

    [RelayCommand]
    private void SelectRemoteTab(RemoteServerTab? tab)
    {
        if (tab == null) return;
        if (ReferenceEquals(tab, ActiveRemoteTab)) { IsAddingRemoteTab = false; return; }
        ActiveRemoteTab = tab;
    }

    /// <summary>Apre una scheda col profilo scelto (o passa a quella già aperta sullo stesso server).</summary>
    [RelayCommand]
    private async Task ConnectRemoteTabAsync()
    {
        var profile = RemoteConnectProfile;
        if (profile == null) { SftpStatusMessage = "Scegli il profilo del server da aprire."; return; }

        var existing = RemoteTabs.FirstOrDefault(t => string.Equals(t.Config.ServerKey, profile.ServerKey, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            ActiveRemoteTab = existing;
            IsAddingRemoteTab = false;
            SftpStatusMessage = $"'{profile.Name}' è già aperto: passato alla sua scheda.";
            return;
        }

        try
        {
            IsRemoteConnecting = true;
            SftpStatusMessage = $"Connessione a '{profile.Name}'...";
            RemoteServerTab tab;

            if (!_sftpService.IsConnected)
            {
                // Prima scheda: connessione principale dell'app (Organizza file, Videobox...)
                await _sftpService.ConnectAsync(profile);
                _mainConfig = profile;
                IsSftpConnected = true;

                _sftpUploadQueue.Configure(profile);
                _sftpUploadQueue.MaxConcurrency = SftpMaxUploadConcurrency;
                _sftpDownloadQueue.Configure(profile);
                _sftpDownloadQueue.MaxConcurrency = SftpMaxDownloadConcurrency;

                tab = new RemoteServerTab(profile, _sftpService, isMain: true) { Path = _sftpService.CurrentRemotePath, Checker = GetChecker(profile, _sftpService) };
                RemoteTabs.Insert(0, tab);
                _shared.SftpCurrentRemotePath = _sftpService.CurrentRemotePath;
            }
            else
            {
                // Connessione propria: non toglie posti né blocca quella principale
                var service = new SftpWinScpService(_loggerFactory.CreateLogger<SftpWinScpService>(), new SftpSessionLimiter(4));
                await service.ConnectAsync(profile);
                var start = string.IsNullOrWhiteSpace(profile.RemoteBasePath) || profile.RemoteBasePath == "/"
                    ? service.GetWorkingDirectory()
                    : profile.RemoteBasePath;
                var checker = GetChecker(profile, service);
                tab = new RemoteServerTab(profile, service, isMain: false) { Path = string.IsNullOrWhiteSpace(start) ? "/" : start, Checker = checker };
                RemoteTabs.Add(tab);
            }

            _logger.LogInformation("[SFTP] Aperta la scheda '{Name}' ({Host})", profile.Name, profile.Host);
            ActiveRemoteTab = tab;
            IsAddingRemoteTab = false;
            SftpStatusMessage = $"Connesso a '{profile.Name}'.";
        }
        catch (Exception ex)
        {
            SftpStatusMessage = $"Connessione a '{profile.Name}' fallita: {ex.Message}";
            _logger.LogWarning(ex, "[SFTP] Connessione a {Name} fallita", profile.Name);
        }
        finally { IsRemoteConnecting = false; }
    }

    /// <summary>
    /// Chiude la scheda e la sua connessione. Upload e download già in coda verso quel server
    /// continuano: usano connessioni proprie.
    /// </summary>
    [RelayCommand]
    private async Task CloseRemoteTabAsync(RemoteServerTab? tab)
    {
        if (tab == null) return;
        int index = RemoteTabs.IndexOf(tab);
        if (index < 0) return;

        RemoteServerTab? next = null;
        if (ReferenceEquals(tab, ActiveRemoteTab))
            next = index + 1 < RemoteTabs.Count ? RemoteTabs[index + 1] : index > 0 ? RemoteTabs[index - 1] : null;

        RemoteTabs.Remove(tab);
        // Il controllo 0 byte di quel server si ferma se nessun altro pannello lo tiene aperto
        if (!IsServerOpen(tab.Config.ServerKey)) tab.Checker?.Stop();
        if (ReferenceEquals(tab, ActiveRemoteTab)) ActiveRemoteTab = next;

        try { await tab.Service.DisconnectAsync(); } catch { }
        if (tab.IsMain)
        {
            IsSftpConnected = false;
            _mainConfig = null;
        }
        else if (tab.Service is IDisposable d)
        {
            try { d.Dispose(); } catch { }
        }

        _logger.LogInformation("[SFTP] Chiusa la scheda '{Name}'", tab.Name);
        SftpStatusMessage = $"Disconnesso da '{tab.Name}'.";
    }

    // ============================================================
    //  CONTROLLO DEI FILE A 0 BYTE (uno per scheda)
    // ============================================================

    /// <summary>Controllo della scheda aperta (quello principale se non c'è nessuna scheda).</summary>
    private ZeroByteCheckerService ActiveChecker => ActiveRemoteTab?.Checker ?? _zeroByteChecker;

    /// <summary>
    /// Un controllo per SERVER (non per pannello): lo stesso server aperto a destra e nel
    /// Server 2 a sinistra condivide risultati e avanzamento.
    /// </summary>
    private readonly Dictionary<string, ZeroByteCheckerService> _checkers = new(StringComparer.OrdinalIgnoreCase);

    private ZeroByteCheckerService GetChecker(SftpConfig server, ISftpService service)
    {
        if (_checkers.TryGetValue(server.ServerKey, out var checker)) return checker;
        checker = new ZeroByteCheckerService(service, () => service.IsConnected);
        AttachChecker(checker);
        _checkers[server.ServerKey] = checker;
        return checker;
    }

    /// <summary>Controllo del server di un trasferimento (null = connessione principale).</summary>
    private ZeroByteCheckerService? CheckerFor(SftpConfig? server)
    {
        var key = (server ?? _mainConfig)?.ServerKey;
        return key != null && _checkers.TryGetValue(key, out var c) ? c : null;
    }

    /// <summary>
    /// Server aperti in tutta la tab SFTP (schede di destra + Server 2 a sinistra), per
    /// Organizza file. Lo stesso server aperto in entrambi i pannelli compare una volta sola.
    /// </summary>
    private void SyncSharedServers()
    {
        var wanted = RemoteTabs.ToList();
        foreach (var left in LeftTabs)
            if (wanted.All(t => !string.Equals(t.Config.ServerKey, left.Config.ServerKey, StringComparison.OrdinalIgnoreCase)))
                wanted.Add(left);
        var list = _shared.SftpServers;
        for (int i = list.Count - 1; i >= 0; i--)
            if (!wanted.Contains(list[i])) list.RemoveAt(i);
        foreach (var t in wanted)
            if (!list.Contains(t)) list.Add(t);
    }

    /// <summary>Il server è aperto in una scheda o nel Server 2.</summary>
    private bool IsServerOpen(string serverKey) =>
        RemoteTabs.Any(t => string.Equals(t.Config.ServerKey, serverKey, StringComparison.OrdinalIgnoreCase)) ||
        LeftTabs.Any(t => string.Equals(t.Config.ServerKey, serverKey, StringComparison.OrdinalIgnoreCase));

    private void AttachChecker(ZeroByteCheckerService checker)
    {
        // Solo i messaggi importanti (avvio, fine, timeout, errori), non ogni cartella
        checker.LogSink = msg => _logger.LogInformation("{Msg}", msg);
        checker.StateChanged += OnZeroByteCheckerStateChanged;
        checker.Completed += OnZeroByteCheckerCompleted;
        checker.FolderScanned += OnZeroByteCheckerFolderScanned;
    }

    /// <summary>Mostra lo stato del controllo della scheda aperta (gli altri continuano in sottofondo).</summary>
    private void SyncZeroByteState()
    {
        var checker = ActiveChecker;
        IsCheckingZeroByte = checker.IsChecking;
        ZeroByteCheckStatus = checker.IsChecking ? checker.ProgressStatus : string.Empty;
    }

    /// <summary>Quando è stato tentato il controllo automatico di una cartella (server|percorso).</summary>
    private readonly Dictionary<string, DateTime> _autoCheckTried = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Dopo un controllo in questa cartella, le sottocartelle senza esito (create, rinominate,
    /// arrivate con un upload) si controllano da sole: sono poche e così "Contenuto" e il rosso
    /// restano sempre aggiornati senza rifare il controllo a mano.
    /// </summary>
    private void AutoCheckNewFolders(ZeroByteCheckerService? checker, ISftpService service, SftpConfig? server,
                                     string path, IEnumerable<SftpRemoteEntry> entries)
    {
        if (checker == null || server == null || checker.IsChecking || !checker.WasCheckedIn(path)) return;
        var now = DateTime.UtcNow;
        var missing = entries
            .Where(e => e.IsDirectory && !checker.TryGetFolderContents(e.FullPath.TrimEnd('/'), out _))
            .Where(e =>
            {
                // Una cartella che non dà esito non si ritenta a ogni lettura (al massimo ogni 30 s)
                var key = server.ServerKey + "|" + e.FullPath;
                if (_autoCheckTried.TryGetValue(key, out var at) && now - at < TimeSpan.FromSeconds(30)) return false;
                _autoCheckTried[key] = now;
                return true;
            })
            .ToList();
        if (missing.Count == 0) return;
        _logger.LogInformation("[ZERO-BYTE] Controllo automatico di {Count} cartelle nuove o rinominate in {Path}", missing.Count, path);
        checker.UseConnection(service, () => service.IsConnected);
        _ = checker.RunCheckAsync(missing);
    }

    /// <summary>Pulsante "disconnetti" nella barra del pannello di destra.</summary>
    [RelayCommand]
    private Task DisconnectActiveRemoteTabAsync() => CloseRemoteTabAsync(ActiveRemoteTab);

    /// <summary>Nome del server di un controllo (per i messaggi).</summary>
    private string? ServerNameOfChecker(object? checker)
    {
        var key = _checkers.FirstOrDefault(kv => ReferenceEquals(kv.Value, checker)).Key;
        if (key == null) return null;
        return RemoteTabs.FirstOrDefault(t => string.Equals(t.Config.ServerKey, key, StringComparison.OrdinalIgnoreCase))?.Name
               ?? LeftTabs.FirstOrDefault(t => string.Equals(t.Config.ServerKey, key, StringComparison.OrdinalIgnoreCase))?.Name;
    }

    /// <summary>Clic su un profilo nel riquadro di connessione: apre subito quel server.</summary>
    [RelayCommand]
    private async Task ConnectProfileAsync(SftpConfig? profile)
    {
        if (profile == null || IsRemoteConnecting) return;
        RemoteConnectProfile = profile;
        await ConnectRemoteTabAsync();
    }

    /// <summary>Chiude tutte le schede (all'uscita dall'app).</summary>
    private void DisposeTabs()
    {
        foreach (var checker in _checkers.Values) { try { checker.Stop(); } catch { } }
        foreach (var tab in RemoteTabs.Where(t => !t.IsMain).ToList())
        {
            try { tab.Service.DisconnectAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
            try { (tab.Service as IDisposable)?.Dispose(); } catch { }
        }
    }

    /// <summary>Il file è arrivato sul server della scheda aperta, in una cartella che si vede?</summary>
    private bool IsShownInActiveTab(SftpConfig? server, string folder)
    {
        var config = server ?? _mainConfig;
        return RemoteConfig != null && config != null &&
               string.Equals(config.ServerKey, RemoteConfig.ServerKey, StringComparison.OrdinalIgnoreCase) &&
               IsInOrUnder(folder, SftpCurrentRemotePath);
    }
}
