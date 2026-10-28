using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.FileOrganizer;
using DownloadManager.Services.Sftp;
using DownloadManager.Services.Download;

namespace DownloadManager.ViewModels;

/// <summary>
/// Sub-ViewModel per il tab "Organizza file". Contiene Organize / Merge / Split
/// nelle due modalità Locale / Remoto.
/// </summary>
public partial class OrganizerTabViewModel : ViewModelBase
{
    private readonly IFileOrganizerService _fileOrganizer;
    private readonly ISftpService _sftpService;
    private readonly SettingsService _settings;
    private readonly SharedState _shared;

    // --- Organize ---
    [ObservableProperty] private string _organizeFolder = string.Empty;
    [ObservableProperty] private string _organizePattern = "{Series} - {Episode:D3}";
    [ObservableProperty] private int _organizeStartingEpisode = 1;
    [ObservableProperty] private ObservableCollection<ProposedOperation> _previewOperations = new();
    [ObservableProperty] private ObservableCollection<FileOperation> _operationHistory = new();
    [ObservableProperty] private ProposedOperation? _selectedPreviewOperation;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CurrentStatusMessage))] private string _organizeStatusMessage = string.Empty;
    [ObservableProperty] private bool _isOrganizeBusy;

    // Sotto-tab attiva (0 = singola, 1 = unisci, 2 = dividi): decide quale stato mostrare sopra la guida.
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CurrentStatusMessage))] private int _selectedSubTabIndex;

    public string CurrentStatusMessage => SelectedSubTabIndex switch
    {
        1 => MergeStatusMessage,
        2 => SplitStatusMessage,
        _ => OrganizeStatusMessage
    };

    // --- Merge ---
    [ObservableProperty] private MergeSourceFolder? _selectedMergeFolder;
    [ObservableProperty] private string _mergeDestinationFolder = string.Empty;
    [ObservableProperty] private string _mergePattern = "{Series}/{Series} - {Episode:D3}";
    [ObservableProperty] private int _mergeStartingEpisode = 1;
    [ObservableProperty] private ObservableCollection<ProposedOperation> _mergePreviewOperations = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CurrentStatusMessage))] private string _mergeStatusMessage = string.Empty;
    [ObservableProperty] private bool _isMergeBusy;

    // --- Split ---
    [ObservableProperty] private string _splitSourceFolder = string.Empty;
    [ObservableProperty] private string _splitDestinationFolder = string.Empty;
    [ObservableProperty] private string _splitFilePattern = "{Series} - {Episode:D3}";
    [ObservableProperty] private string _splitFolderPattern = "Episodi {Start}-{End}";
    [ObservableProperty] private bool _splitNumberingContinue = false;
    [ObservableProperty] private bool _splitUniformMode = true;
    [ObservableProperty] private int _splitUniformEpisodes = 50;
    [ObservableProperty] private ObservableCollection<ProposedOperation> _splitPreviewOperations = new();
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CurrentStatusMessage))] private string _splitStatusMessage = string.Empty;
    [ObservableProperty] private bool _isSplitBusy;

    public ObservableCollection<MergeSourceFolder> MergeSourceFolders { get; } = new();
    public ObservableCollection<SplitSegment> SplitSegments { get; } = new();

    // Pattern proposti, diversi per modalità:
    // - Singola cartella: i file restano nella loro cartella, cambia solo il nome (niente "/").
    // - Unisci cartelle: si raccolgono in una destinazione, quindi ha senso anche la cartella della serie.
    // - Dividi cartella: la sottocartella la decide "Nome sottocartella", il file ha solo il nome.
    public string[] OrganizePatterns { get; } = new[]
    {
        "{Series} - {Episode:D3}", "{Series} - {Episode:D2}", "{Series}_Ep_{Episode:D3}",
        "{Series} - Episodio {Episode:D2}", "{Series} S01E{Episode:D2}", "{Series} - {Episode:D3} - {Title}"
    };

    public string[] MergePatterns { get; } = new[]
    {
        "{Series}/{Series} - {Episode:D3}", "{Series}/{Series}_Ep_{Episode:D3}", "{Series}/{Series} S01E{Episode:D2}",
        "{Series}/{Series} - {Episode:D3} - {Title}", "{Series} - {Episode:D3}", "{Series}_Ep_{Episode:D3}"
    };

    public string[] SplitFilePatterns { get; } = new[]
    {
        "{Series} - {Episode:D3}", "{Series} - {Episode:D2}", "{Series}_Ep_{Episode:D3}",
        "{Series} - Episodio {Episode:D2}", "{Series} - {Episode:D3} - {Title}", "{OriginalFileName}"
    };

    public string[] SplitFolderPatterns { get; } = new[]
    {
        "Episodi {Start}-{End}", "Episodi {Start:D3}-{End:D3}", "Parte {Number}", "Parte {Number} ({Start}-{End})",
        "Stagione {Number}", "{Series} - Parte {Number}", "{Series}/Parte {Number}", "{Series}/Stagione {Number:D2}",
        "{Number:D2}"
    };

    /// <summary>
    /// Anteprima dei nomi delle prime sottocartelle (es. "Episodi 1-50 · Episodi 51-100"), con le
    /// dimensioni scelte e la numerazione (reset o continua) come le farà "Anteprima split".
    /// </summary>
    public string SplitFolderPatternPreview
    {
        get
        {
            try
            {
                var sizes = SplitUniformMode
                    ? new List<int> { SplitUniformEpisodes, SplitUniformEpisodes }
                    : SplitSegments.Select(x => x.EpisodeCount).Where(x => x > 0).Take(2).ToList();
                if (sizes.Count == 0) sizes.Add(50);
                var pattern = string.IsNullOrWhiteSpace(SplitFolderPattern) ? "{Number}" : SplitFolderPattern;
                var names = new List<string>();
                int done = 0;
                for (int i = 0; i < sizes.Count; i++)
                {
                    int start = SplitNumberingContinue ? done + 1 : 1;
                    int end = SplitNumberingContinue ? done + sizes[i] : sizes[i];
                    done += sizes[i];
                    var name = Services.FileOrganizer.FileOrganizerService.BuildSplitFolderName(pattern, i + 1, start, end, "Frieren");
                    names.Add(name.Replace('/', System.IO.Path.DirectorySeparatorChar));
                }
                return string.Join("  ·  ", names) + "  ·  …";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    // ============================================================
    //  WRAPPER SharedState
    // ============================================================

    public bool OrganizeModeIsLocal
    {
        get => _shared.OrganizeModeIsLocal;
        set
        {
            if (_shared.OrganizeModeIsLocal == value) return;
            _shared.OrganizeModeIsLocal = value;
            OnOrganizeModeIsLocalChanged(value);
        }
    }

    /// <summary>
    /// Legacy: la casella "Crea sottocartella per serie" è stata rimossa
    /// dall'UI dell'Organizer. Il valore è mantenuto su SharedState perché
    /// ancora usato dal tab Download, ma qui non viene più mostrato.
    /// </summary>
    public bool CreateSeriesFolder
    {
        get => _shared.CreateSeriesFolder;
        set => _shared.CreateSeriesFolder = value;
    }

    // ============================================================
    //  SERVER REMOTO (uno dei server aperti nelle schede SFTP)
    // ============================================================

    /// <summary>Server aperti nella tab SFTP: si sceglie su quale lavorare, senza riconnettersi.</summary>
    public ObservableCollection<RemoteServerTab> SftpServers => _shared.SftpServers;

    [ObservableProperty] private RemoteServerTab? _organizerServer;

    /// <summary>Connessione su cui lavorano le operazioni remote.</summary>
    public ISftpService RemoteService => OrganizerServer?.Service ?? _sftpService;

    /// <summary>Cartella aperta su quel server nella tab SFTP.</summary>
    public string RemoteStartPath => OrganizerServer?.Path ?? _shared.SftpCurrentRemotePath;

    private string SharedSftpRemotePath => RemoteStartPath;
    private bool SharedIsSftpConnected => (RemoteService as ISftpService)?.IsConnected == true;
    /// <summary>C'è almeno un server aperto (abilita la modalità Remoto).</summary>
    public bool IsSftpConnected => SftpServers.Count > 0;
    public bool HasSeveralServers => SftpServers.Count > 1;

    partial void OnOrganizerServerChanged(RemoteServerTab? value)
    {
        _fileOrganizer.UseRemoteServer(value?.Service);
        OnPropertyChanged(nameof(RemoteService));
        OnPropertyChanged(nameof(RemoteStartPath));
        // Cambiando server, le cartelle remote ripartono da quella aperta su quel server
        if (value != null && !OrganizeModeIsLocal) FillRemoteFolders(value.Path);
        // In Locale: le cartelle remote ricordate erano dell'altro server, si riparte da quello nuovo
        if (OrganizeModeIsLocal) _remotePaths = null;
    }

    private void OnSftpServersChanged()
    {
        OnPropertyChanged(nameof(IsSftpConnected));
        OnPropertyChanged(nameof(HasSeveralServers));
        if (OrganizerServer == null || !SftpServers.Contains(OrganizerServer))
            OrganizerServer = SftpServers.FirstOrDefault();
        if (SftpServers.Count == 0) OrganizeModeIsLocal = true;
    }

    private void FillRemoteFolders(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        OrganizeFolder = path;
        MergeDestinationFolder = path;
        SplitSourceFolder = path;
        SplitDestinationFolder = path;
    }

    // ============================================================
    //  COMPUTED
    // ============================================================

    public string OrganizeFolderLabel => OrganizeModeIsLocal ? "Cartella:" : "Cartella remota:";
    public string MergeDestinationLabel => OrganizeModeIsLocal ? "Destinazione:" : "Destinazione remota:";
    public string SplitSourceLabel => OrganizeModeIsLocal ? "Cartella sorgente:" : "Cartella sorgente remota:";
    public string SplitDestinationLabel => OrganizeModeIsLocal ? "Destinazione:" : "Destinazione remota:";

    public string OrganizePatternPreview
    {
        get
        {
            try
            {
                var p = FileNameBuilder.ApplyPattern(
                    OrganizePattern, "Frieren", 1, "Episodio 1", "Frieren_Ep_001_SUB_ITA");
                return p.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".mp4";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string MergePatternPreview
    {
        get
        {
            try
            {
                var p = FileNameBuilder.ApplyPattern(
                    MergePattern, "Frieren", 1, "Episodio 1", "Frieren_Ep_001_SUB_ITA");
                return p.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".mp4";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string SplitFilePatternPreview
    {
        get
        {
            try
            {
                var p = FileNameBuilder.ApplyPattern(
                    SplitFilePattern, "Frieren", 1, "Episodio 1", "Frieren_Ep_001_SUB_ITA");
                return p.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".mp4";
            }
            catch { return "(pattern non valido)"; }
        }
    }

    public string SplitSegmentsSummary
    {
        get
        {
            if (SplitUniformMode)
                return $"Modalità uniforme: {SplitUniformEpisodes} episodi per sottocartella.";

            if (SplitSegments.Count == 0)
                return "Nessun segmento definito.";

            int total = SplitSegments.Sum(s => s.EpisodeCount);
            return $"{SplitSegments.Count} segmenti, {total} episodi programmati.";
        }
    }

    // ============================================================
    //  COSTRUTTORE
    // ============================================================

    public OrganizerTabViewModel(
        IFileOrganizerService fileOrganizer,
        ISftpService sftpService,
        SettingsService settings,
        SharedState shared)
    {
        _fileOrganizer = fileOrganizer;
        _sftpService = sftpService;
        _settings = settings;
        _shared = shared;

        OrganizeFolder = _settings.Current.BaseDownloadFolder;
        MergeDestinationFolder = _settings.Current.BaseDownloadFolder;
        SplitDestinationFolder = _settings.Current.BaseDownloadFolder;

        _shared.PropertyChanged += OnSharedPropertyChanged;
        _shared.SftpServers.CollectionChanged += (_, _) => OnSftpServersChanged();

        AddSplitSegment();
        _ = Task.Run(LoadHistoryAsync);
    }

    private void OnSharedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SharedState.OrganizeModeIsLocal):
                OnPropertyChanged(nameof(OrganizeModeIsLocal));
                OnPropertyChanged(nameof(OrganizeFolderLabel));
                OnPropertyChanged(nameof(MergeDestinationLabel));
                OnPropertyChanged(nameof(SplitSourceLabel));
                OnPropertyChanged(nameof(SplitDestinationLabel));
                break;
            case nameof(SharedState.CreateSeriesFolder):
                OnPropertyChanged(nameof(CreateSeriesFolder));
                break;
            case nameof(SharedState.IsSftpConnected):
                OnPropertyChanged(nameof(IsSftpConnected));
                break;
        }
    }

    /// <summary>Cartelle di una modalità (Locale o Remoto), ricordate separatamente.</summary>
    private sealed record ModePaths(string Organize, string MergeDest, string SplitSource, string SplitDest, List<MergeSourceFolder> MergeSources);
    private ModePaths? _localPaths, _remotePaths;

    private ModePaths CapturePaths() =>
        new(OrganizeFolder, MergeDestinationFolder, SplitSourceFolder, SplitDestinationFolder, MergeSourceFolders.ToList());

    private void RestorePaths(ModePaths p)
    {
        OrganizeFolder = p.Organize;
        MergeDestinationFolder = p.MergeDest;
        SplitSourceFolder = p.SplitSource;
        SplitDestinationFolder = p.SplitDest;
        MergeSourceFolders.Clear();
        foreach (var m in p.MergeSources) MergeSourceFolders.Add(m);
        RenumberPriorities();
    }

    private void OnOrganizeModeIsLocalChanged(bool value)
    {
        OnPropertyChanged(nameof(OrganizeFolderLabel));
        OnPropertyChanged(nameof(MergeDestinationLabel));
        OnPropertyChanged(nameof(SplitSourceLabel));
        OnPropertyChanged(nameof(SplitDestinationLabel));

        // Locale e Remoto hanno ciascuna le sue cartelle: prima, cambiando modalità, restava il
        // percorso dell'altra e si creavano cartelle dove non si voleva. Si ritrovano quelle usate
        // l'ultima volta in quella modalità (la prima volta: cartella base / cartella del server).
        var current = CapturePaths();
        if (value)
        {
            _remotePaths = current;
            var b = _settings.Current.BaseDownloadFolder;
            RestorePaths(_localPaths ?? new ModePaths(b, b, "", b, new List<MergeSourceFolder>()));
        }
        else
        {
            _localPaths = current;
            var r = SharedIsSftpConnected ? SharedSftpRemotePath : "";
            RestorePaths(_remotePaths ?? new ModePaths(r, r, r, r, new List<MergeSourceFolder>()));
        }

        // Le proposte calcolate nell'altra modalità non vanno applicate qui
        if (PreviewOperations.Count + MergePreviewOperations.Count + SplitPreviewOperations.Count > 0)
        {
            PreviewOperations = new();
            MergePreviewOperations = new();
            SplitPreviewOperations = new();
            OrganizeStatusMessage = MergeStatusMessage = SplitStatusMessage =
                $"Modalità {(value ? "Locale" : "Remoto")}: proposte svuotate, rifai l'anteprima.";
        }
    }

    partial void OnOrganizePatternChanged(string value) => OnPropertyChanged(nameof(OrganizePatternPreview));
    partial void OnMergePatternChanged(string value) => OnPropertyChanged(nameof(MergePatternPreview));
    partial void OnSplitFilePatternChanged(string value) => OnPropertyChanged(nameof(SplitFilePatternPreview));
    partial void OnSplitFolderPatternChanged(string value) => OnPropertyChanged(nameof(SplitFolderPatternPreview));
    partial void OnSplitNumberingContinueChanged(bool value) => OnPropertyChanged(nameof(SplitFolderPatternPreview));
    partial void OnSplitUniformModeChanged(bool value) { OnPropertyChanged(nameof(SplitSegmentsSummary)); OnPropertyChanged(nameof(SplitFolderPatternPreview)); }
    partial void OnSplitUniformEpisodesChanged(int value) { OnPropertyChanged(nameof(SplitSegmentsSummary)); OnPropertyChanged(nameof(SplitFolderPatternPreview)); }

    // ============================================================
    //  ORGANIZE
    // ============================================================

    [RelayCommand]
    private async Task PreviewOrganizeAsync()
    {
        if (OrganizeModeIsLocal) await PreviewOrganizeLocalInternalAsync();
        else await PreviewOrganizeRemoteInternalAsync();
    }

    [RelayCommand]
    private async Task ApplyOrganizeAsync()
    {
        if (OrganizeModeIsLocal) await ApplyOrganizeLocalInternalAsync();
        else await ApplyOrganizeRemoteInternalAsync();
    }

    [RelayCommand]
    private async Task UndoLastBatchAsync()
    {
        if (OrganizeModeIsLocal) await UndoLastLocalBatchInternalAsync();
        else await UndoLastRemoteBatchInternalAsync();
    }

    private async Task PreviewOrganizeLocalInternalAsync()
    {
        if (string.IsNullOrWhiteSpace(OrganizeFolder)) { OrganizeStatusMessage = "Seleziona una cartella."; return; }
        if (!Directory.Exists(OrganizeFolder)) { OrganizeStatusMessage = "La cartella non esiste."; return; }

        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Analisi in corso...";

            var startingEpisode = OrganizeStartingEpisode > 0 ? OrganizeStartingEpisode : 1;

            // createSubfolders sempre false: la casella è stata rimossa dall'UI
            var ops = await _fileOrganizer.PreviewAsync(
                OrganizeFolder, OrganizePattern, false, startingEpisode);

            PreviewOperations = new(ops);   // un solo aggiornamento della tabella, non uno per riga

            var conflicts = ops.Count(o => o.HasConflict);
            OrganizeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni (episodi da {startingEpisode})."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    private async Task ApplyOrganizeLocalInternalAsync()
    {
        if (PreviewOperations.Count == 0) { OrganizeStatusMessage = "Nessuna operazione."; return; }
        var conflicts = PreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { OrganizeStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Applicazione...";
            var count = await _fileOrganizer.ApplyAsync(PreviewOperations.ToList());
            OrganizeStatusMessage = $"Applicate {count} operazioni.";
            PreviewOperations.Clear();
            await LoadHistoryAsync();
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    private async Task UndoLastLocalBatchInternalAsync()
    {
        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Undo...";
            var count = await _fileOrganizer.UndoLastBatchAsync();
            OrganizeStatusMessage = count == 0 ? "Niente da annullare." : $"Annullate {count} operazioni.";
            await LoadHistoryAsync();
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    private async Task PreviewOrganizeRemoteInternalAsync()
    {
        if (!SharedIsSftpConnected) { OrganizeStatusMessage = "Non connesso."; return; }
        if (string.IsNullOrWhiteSpace(OrganizeFolder)) { OrganizeStatusMessage = "Seleziona una cartella remota."; return; }

        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Analisi remota in corso...";

            var startingEpisode = OrganizeStartingEpisode > 0 ? OrganizeStartingEpisode : 1;

            // createSubfolders sempre false: la casella è stata rimossa dall'UI
            var ops = await _fileOrganizer.PreviewRemoteFolderAsync(
                OrganizeFolder, OrganizePattern, false, startingEpisode);

            PreviewOperations = new(ops);   // un solo aggiornamento della tabella, non uno per riga

            var conflicts = ops.Count(o => o.HasConflict);
            OrganizeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni (remoto, episodi da {startingEpisode})."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    private async Task ApplyOrganizeRemoteInternalAsync()
    {
        if (PreviewOperations.Count == 0) { OrganizeStatusMessage = "Nessuna operazione."; return; }
        var conflicts = PreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { OrganizeStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Applicazione remota...";

            var count = await _fileOrganizer.ApplyRemoteAsync(PreviewOperations.ToList());
            OrganizeStatusMessage = $"Applicate {count} operazioni.";

            PreviewOperations.Clear();
            _shared.RequestSftpRefresh?.Invoke();
            await LoadHistoryAsync();
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    private async Task UndoLastRemoteBatchInternalAsync()
    {
        if (!SharedIsSftpConnected) { OrganizeStatusMessage = "Non connesso."; return; }

        try
        {
            IsOrganizeBusy = true;
            OrganizeStatusMessage = "Undo remoto...";

            var count = await _fileOrganizer.UndoLastRemoteBatchAsync();

            OrganizeStatusMessage = count == 0
                ? "Niente da annullare (remoto)."
                : $"Annullate {count} operazioni.";

            _shared.RequestSftpRefresh?.Invoke();
            await LoadHistoryAsync();
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsOrganizeBusy = false; }
    }

    [RelayCommand]
    private void ClearPreviewOperations() { PreviewOperations = new(); OrganizeStatusMessage = "Proposte svuotate."; }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try
        {
            await Task.Run(() => _fileOrganizer.ClearHistoryAsync());   // database fuori dal thread dell'interfaccia
            OperationHistory = new();
            OrganizeStatusMessage = "Cronologia svuotata.";
        }
        catch (Exception ex) { OrganizeStatusMessage = $"Errore: {ex.Message}"; }
    }

    public async Task LoadHistoryAsync()
    {
        try
        {
            var history = await _fileOrganizer.GetHistoryAsync(200);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                OperationHistory = new(history);   // un solo aggiornamento della tabella, non uno per riga
            });
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($">>> Errore cronologia: {ex.Message}"); }
    }

    public void SetOrganizeFolder(string folder) => OrganizeFolder = folder;

    // ============================================================
    //  MERGE
    // ============================================================

    [RelayCommand]
    private async Task PreviewMergeAsync()
    {
        if (OrganizeModeIsLocal) await PreviewMergeLocalInternalAsync();
        else await PreviewMergeRemoteInternalAsync();
    }

    [RelayCommand]
    private async Task ApplyMergeAsync()
    {
        if (OrganizeModeIsLocal) await ApplyMergeLocalInternalAsync();
        else await ApplyMergeRemoteInternalAsync();
    }

    public void AddMergeFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return;
        if (MergeSourceFolders.Any(f => string.Equals(f.Path, folder, StringComparison.OrdinalIgnoreCase))) return;

        var item = new MergeSourceFolder { Path = folder, Priority = MergeSourceFolders.Count };
        MergeSourceFolders.Add(item);
        RenumberPriorities();
        _ = CountMergeFilesAsync(item);
    }

    /// <summary>Conta i video della cartella (gli stessi file che l'unione prende in considerazione).</summary>
    private async Task CountMergeFilesAsync(MergeSourceFolder folder)
    {
        try
        {
            // Percorso locale (C:\..., \server\...) oppure cartella sul server SFTP
            bool isLocal = folder.Path.Contains(':') || folder.Path.StartsWith(@"\\");
            int count;
            if (isLocal)
            {
                count = await Task.Run(() => Directory.Exists(folder.Path)
                    ? Directory.EnumerateFiles(folder.Path).Count(FileOrganizerService.IsVideoFile)
                    : 0);
            }
            else
            {
                if (!SharedIsSftpConnected) return;
                var entries = await RemoteService.ListDirectoryAsync(folder.Path.TrimEnd('/'), CancellationToken.None);
                count = entries.Count(e => !e.IsDirectory && FileOrganizerService.IsVideoFile(e.Name));
            }
            folder.FileCount = count;
        }
        catch { /* cartella non leggibile: resta il valore precedente */ }
    }

    /// <summary>Dopo un'unione i file si sono spostati: si ricontano tutte le cartelle.</summary>
    private void RecountMergeFolders()
    {
        foreach (var folder in MergeSourceFolders.ToList()) _ = CountMergeFilesAsync(folder);
    }

    public void RemoveMergeFolder(MergeSourceFolder folder) { MergeSourceFolders.Remove(folder); RenumberPriorities(); }

    public void MoveMergeFolderUp(MergeSourceFolder folder)
    {
        var idx = MergeSourceFolders.IndexOf(folder);
        if (idx <= 0) return;
        MergeSourceFolders.Move(idx, idx - 1);
        RenumberPriorities();
    }

    public void MoveMergeFolderDown(MergeSourceFolder folder)
    {
        var idx = MergeSourceFolders.IndexOf(folder);
        if (idx < 0 || idx >= MergeSourceFolders.Count - 1) return;
        MergeSourceFolders.Move(idx, idx + 1);
        RenumberPriorities();
    }

    public void ClearMergeFolders()
    {
        MergeSourceFolders.Clear();
        MergePreviewOperations.Clear();
        MergeStatusMessage = string.Empty;
    }

    private void RenumberPriorities()
    {
        for (int i = 0; i < MergeSourceFolders.Count; i++)
            MergeSourceFolders[i].Priority = i;
    }

    public void SetMergeDestinationFolder(string folder) => MergeDestinationFolder = folder;

    private async Task PreviewMergeLocalInternalAsync()
    {
        if (MergeSourceFolders.Count == 0) { MergeStatusMessage = "Aggiungi almeno una cartella."; return; }
        if (string.IsNullOrWhiteSpace(MergeDestinationFolder)) { MergeStatusMessage = "Seleziona una destinazione."; return; }

        try
        {
            IsMergeBusy = true;
            MergeStatusMessage = "Analisi...";

            var startingEpisode = MergeStartingEpisode > 0 ? MergeStartingEpisode : 1;

            // createSubfolders sempre false: la casella è stata rimossa dall'UI
            var ops = await _fileOrganizer.PreviewMergeAsync(
                MergeSourceFolders.ToList(), MergeDestinationFolder, MergePattern, false, startingEpisode);

            MergePreviewOperations = new(ops);   // un solo aggiornamento della tabella, non uno per riga

            var conflicts = ops.Count(o => o.HasConflict);
            MergeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni (episodi da {startingEpisode})."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { MergeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsMergeBusy = false; }
    }

    private async Task ApplyMergeLocalInternalAsync()
    {
        if (MergePreviewOperations.Count == 0) { MergeStatusMessage = "Nessuna operazione."; return; }
        var conflicts = MergePreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { MergeStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsMergeBusy = true;
            MergeStatusMessage = "Applicazione...";
            var count = await _fileOrganizer.ApplyAsync(MergePreviewOperations.ToList());
            MergeStatusMessage = $"Applicate {count} operazioni.";
            MergePreviewOperations.Clear();
            RecountMergeFolders();
            await LoadHistoryAsync();
        }
        catch (Exception ex) { MergeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsMergeBusy = false; }
    }

    private async Task PreviewMergeRemoteInternalAsync()
    {
        if (!SharedIsSftpConnected) { MergeStatusMessage = "Non connesso."; return; }
        if (MergeSourceFolders.Count == 0) { MergeStatusMessage = "Aggiungi almeno una cartella."; return; }
        if (string.IsNullOrWhiteSpace(MergeDestinationFolder)) { MergeStatusMessage = "Seleziona una destinazione remota."; return; }

        try
        {
            IsMergeBusy = true;
            MergeStatusMessage = "Analisi remota...";

            var startingEpisode = MergeStartingEpisode > 0 ? MergeStartingEpisode : 1;

            // createSubfolders sempre false: la casella è stata rimossa dall'UI
            var ops = await _fileOrganizer.PreviewRemoteMergeAsync(
                MergeSourceFolders.ToList(), MergeDestinationFolder, MergePattern, false, startingEpisode);

            MergePreviewOperations = new(ops);   // un solo aggiornamento della tabella, non uno per riga

            var conflicts = ops.Count(o => o.HasConflict);
            MergeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni (remoto, episodi da {startingEpisode})."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { MergeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsMergeBusy = false; }
    }

    private async Task ApplyMergeRemoteInternalAsync()
    {
        if (MergePreviewOperations.Count == 0) { MergeStatusMessage = "Nessuna operazione."; return; }
        var conflicts = MergePreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { MergeStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsMergeBusy = true;
            MergeStatusMessage = "Applicazione remota...";
            var count = await _fileOrganizer.ApplyRemoteAsync(MergePreviewOperations.ToList());
            MergeStatusMessage = $"Applicate {count} operazioni.";
            MergePreviewOperations.Clear();
            _shared.RequestSftpRefresh?.Invoke();
            RecountMergeFolders();
            await LoadHistoryAsync();
        }
        catch (Exception ex) { MergeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsMergeBusy = false; }
    }

    [RelayCommand]
    private void ClearMergePreviewOperations() { MergePreviewOperations = new(); MergeStatusMessage = "Proposte svuotate."; }

    // ============================================================
    //  SPLIT
    // ============================================================

    [RelayCommand]
    private async Task PreviewSplitAsync()
    {
        if (OrganizeModeIsLocal) await PreviewSplitLocalInternalAsync();
        else await PreviewSplitRemoteInternalAsync();
    }

    [RelayCommand]
    private async Task ApplySplitAsync()
    {
        if (OrganizeModeIsLocal) await ApplySplitLocalInternalAsync();
        else await ApplySplitRemoteInternalAsync();
    }

    public void SetSplitSourceFolder(string folder) => SplitSourceFolder = folder;
    public void SetSplitDestinationFolder(string folder) => SplitDestinationFolder = folder;

    [RelayCommand]
    private void AddSplitSegment()
    {
        var seg = new SplitSegment { EpisodeCount = 50 };
        seg.PropertyChanged += OnSplitSegmentPropertyChanged;
        SplitSegments.Add(seg);
        RenumberSegments();
        OnPropertyChanged(nameof(SplitSegmentsSummary)); OnPropertyChanged(nameof(SplitFolderPatternPreview));
    }

    [RelayCommand]
    private void RemoveSplitSegment(SplitSegment? segment)
    {
        if (segment == null) return;
        segment.PropertyChanged -= OnSplitSegmentPropertyChanged;
        SplitSegments.Remove(segment);
        RenumberSegments();
        OnPropertyChanged(nameof(SplitSegmentsSummary)); OnPropertyChanged(nameof(SplitFolderPatternPreview));
    }

    [RelayCommand]
    private void ClearSplitSegments()
    {
        foreach (var seg in SplitSegments) seg.PropertyChanged -= OnSplitSegmentPropertyChanged;
        SplitSegments.Clear();
        OnPropertyChanged(nameof(SplitSegmentsSummary)); OnPropertyChanged(nameof(SplitFolderPatternPreview));
    }

    private void OnSplitSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SplitSegment.EpisodeCount))
            OnPropertyChanged(nameof(SplitSegmentsSummary)); OnPropertyChanged(nameof(SplitFolderPatternPreview));
    }

    private void RenumberSegments()
    {
        for (int i = 0; i < SplitSegments.Count; i++) SplitSegments[i].Index = i + 1;
    }

    private List<int>? BuildSegmentSizesLocal()
    {
        if (SplitUniformMode)
        {
            if (SplitUniformEpisodes <= 0) { SplitStatusMessage = "Numero episodi non valido."; return null; }

            var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".m4v" };

            int fileCount = Directory.GetFiles(SplitSourceFolder, "*", SearchOption.TopDirectoryOnly)
                .Count(f => videoExts.Contains(Path.GetExtension(f)));

            if (fileCount == 0) { SplitStatusMessage = "Nessun file video."; return null; }

            int segmentCount = (int)Math.Ceiling((double)fileCount / SplitUniformEpisodes);
            var sizes = new List<int>();
            for (int i = 0; i < segmentCount; i++) sizes.Add(SplitUniformEpisodes);
            return sizes;
        }
        else
        {
            if (SplitSegments.Count == 0) { SplitStatusMessage = "Aggiungi un segmento."; return null; }
            var sizes = SplitSegments.Where(s => s.EpisodeCount > 0).Select(s => s.EpisodeCount).ToList();
            if (sizes.Count == 0) { SplitStatusMessage = "Tutti segmenti a 0."; return null; }
            return sizes;
        }
    }

    private async Task PreviewSplitLocalInternalAsync()
    {
        if (string.IsNullOrWhiteSpace(SplitSourceFolder)) { SplitStatusMessage = "Seleziona una cartella."; return; }
        if (!Directory.Exists(SplitSourceFolder)) { SplitStatusMessage = "Cartella non esiste."; return; }
        if (string.IsNullOrWhiteSpace(SplitDestinationFolder)) SplitDestinationFolder = SplitSourceFolder;

        var segmentSizes = BuildSegmentSizesLocal();
        if (segmentSizes == null) return;

        try
        {
            IsSplitBusy = true;
            SplitStatusMessage = "Analisi...";
            var mode = SplitNumberingContinue ? SplitNumberingMode.Continue : SplitNumberingMode.Reset;

            var ops = await _fileOrganizer.PreviewSplitAsync(
                SplitSourceFolder, SplitDestinationFolder, segmentSizes,
                SplitFilePattern, SplitFolderPattern, mode);

            SplitPreviewOperations = new(ops);   // un solo aggiornamento della tabella, non uno per riga

            var conflicts = ops.Count(o => o.HasConflict);
            var folderCount = ops.Select(o => Path.GetDirectoryName(o.NewPath) ?? "").Distinct().Count();

            SplitStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni in {folderCount} sottocartelle."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { SplitStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsSplitBusy = false; }
    }

    private async Task ApplySplitLocalInternalAsync()
    {
        if (SplitPreviewOperations.Count == 0) { SplitStatusMessage = "Nessuna operazione."; return; }
        var conflicts = SplitPreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { SplitStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsSplitBusy = true;
            SplitStatusMessage = "Applicazione...";
            var count = await _fileOrganizer.ApplyAsync(SplitPreviewOperations.ToList());
            SplitStatusMessage = $"Applicate {count} operazioni.";
            SplitPreviewOperations.Clear();
            await LoadHistoryAsync();
        }
        catch (Exception ex) { SplitStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsSplitBusy = false; }
    }

    private async Task PreviewSplitRemoteInternalAsync()
    {
        if (!SharedIsSftpConnected) { SplitStatusMessage = "Non connesso."; return; }
        if (string.IsNullOrWhiteSpace(SplitSourceFolder)) { SplitStatusMessage = "Seleziona una cartella remota."; return; }
        if (string.IsNullOrWhiteSpace(SplitDestinationFolder)) SplitDestinationFolder = SplitSourceFolder;

        List<int>? segmentSizes;

        if (SplitUniformMode)
        {
            if (SplitUniformEpisodes <= 0) { SplitStatusMessage = "Numero episodi non valido."; return; }

            var videoExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".m4v" };

            List<SftpRemoteEntry> entries;
            try
            {
                entries = await RemoteService.ListDirectoryAsync(SplitSourceFolder.TrimEnd('/'), CancellationToken.None);
            }
            catch (Exception ex)
            {
                SplitStatusMessage = $"Errore lettura remota: {ex.Message}";
                return;
            }

            int fileCount = entries.Count(e => !e.IsDirectory && videoExts.Contains(Path.GetExtension(e.Name)));
            if (fileCount == 0) { SplitStatusMessage = "Nessun file video."; return; }

            int segmentCount = (int)Math.Ceiling((double)fileCount / SplitUniformEpisodes);
            segmentSizes = new List<int>();
            for (int i = 0; i < segmentCount; i++) segmentSizes.Add(SplitUniformEpisodes);
        }
        else
        {
            if (SplitSegments.Count == 0) { SplitStatusMessage = "Aggiungi un segmento."; return; }
            segmentSizes = SplitSegments.Where(s => s.EpisodeCount > 0).Select(s => s.EpisodeCount).ToList();
            if (segmentSizes.Count == 0) { SplitStatusMessage = "Tutti segmenti a 0."; return; }
        }

        try
        {
            IsSplitBusy = true;
            SplitStatusMessage = "Analisi remota...";
            var mode = SplitNumberingContinue ? SplitNumberingMode.Continue : SplitNumberingMode.Reset;

            var ops = await _fileOrganizer.PreviewRemoteSplitAsync(
                SplitSourceFolder, SplitDestinationFolder, segmentSizes,
                SplitFilePattern, SplitFolderPattern, mode);

            SplitPreviewOperations = new(ops);   // un solo aggiornamento della tabella, non uno per riga

            var conflicts = ops.Count(o => o.HasConflict);
            var folderCount = ops
                .Select(o => { var i = o.NewPath.LastIndexOf('/'); return i > 0 ? o.NewPath.Substring(0, i) : ""; })
                .Distinct().Count();

            SplitStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni in {folderCount} sottocartelle (remoto)."
                : $"Trovate {ops.Count} operazioni ({conflicts} conflitti).";
        }
        catch (Exception ex) { SplitStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsSplitBusy = false; }
    }

    private async Task ApplySplitRemoteInternalAsync()
    {
        if (SplitPreviewOperations.Count == 0) { SplitStatusMessage = "Nessuna operazione."; return; }
        var conflicts = SplitPreviewOperations.Count(o => o.HasConflict);
        if (conflicts > 0) { SplitStatusMessage = $"Ci sono {conflicts} conflitti."; return; }

        try
        {
            IsSplitBusy = true;
            SplitStatusMessage = "Applicazione remota...";
            var count = await _fileOrganizer.ApplyRemoteAsync(SplitPreviewOperations.ToList());
            SplitStatusMessage = $"Applicate {count} operazioni.";
            SplitPreviewOperations.Clear();
            _shared.RequestSftpRefresh?.Invoke();
            await LoadHistoryAsync();
        }
        catch (Exception ex) { SplitStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsSplitBusy = false; }
    }

    [RelayCommand]
    private void ClearSplitPreviewOperations() { SplitPreviewOperations = new(); SplitStatusMessage = "Proposte svuotate."; }
}