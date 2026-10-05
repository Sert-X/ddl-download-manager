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
    [ObservableProperty] private string _organizePattern = "{Series}/{Series} - {Episode:D3}";
    [ObservableProperty] private ObservableCollection<ProposedOperation> _previewOperations = new();
    [ObservableProperty] private ObservableCollection<FileOperation> _operationHistory = new();
    [ObservableProperty] private ProposedOperation? _selectedPreviewOperation;
    [ObservableProperty] private string _organizeStatusMessage = string.Empty;
    [ObservableProperty] private bool _isOrganizeBusy;

    // --- Merge ---
    [ObservableProperty] private MergeSourceFolder? _selectedMergeFolder;
    [ObservableProperty] private string _mergeDestinationFolder = string.Empty;
    [ObservableProperty] private string _mergePattern = "{Series}/{Series} - {Episode:D3}";
    [ObservableProperty] private ObservableCollection<ProposedOperation> _mergePreviewOperations = new();
    [ObservableProperty] private string _mergeStatusMessage = string.Empty;
    [ObservableProperty] private bool _isMergeBusy;

    // --- Split ---
    [ObservableProperty] private string _splitSourceFolder = string.Empty;
    [ObservableProperty] private string _splitDestinationFolder = string.Empty;
    [ObservableProperty] private string _splitFilePattern = "{Series} - {Episode:D3}";
    [ObservableProperty] private string _splitFolderPattern = "Puntate {Number}";
    [ObservableProperty] private bool _splitNumberingContinue = false;
    [ObservableProperty] private bool _splitUniformMode = true;
    [ObservableProperty] private int _splitUniformEpisodes = 50;
    [ObservableProperty] private ObservableCollection<ProposedOperation> _splitPreviewOperations = new();
    [ObservableProperty] private string _splitStatusMessage = string.Empty;
    [ObservableProperty] private bool _isSplitBusy;

    public ObservableCollection<MergeSourceFolder> MergeSourceFolders { get; } = new();
    public ObservableCollection<SplitSegment> SplitSegments { get; } = new();

    public string[] OrganizePatterns { get; } = new[]
    {
        "{Series} - {Episode:D3}", "{Series}/{Series} - {Episode:D3}",
        "{Series}/S01/{Series} - {Episode:D3}", "{Series}/{Episode}",
        "{Series}/{Episode:D2}", "{Series}/{Episode:D3}",
        "{Series}/{Series} - {Episode:D3} - {Title}", "{OriginalFileName}"
    };

    public string[] SplitFolderPatterns { get; } = new[]
    {
        "{Number}", "{Number:D2}", "{Number:D3}", "Puntate {Number}", "Parte {Number}",
        "Puntate {Number} ({Start}-{End})", "Episodi {Start}-{End}", "{Number} - Episodi {Start}-{End}"
    };

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

    public bool CreateSeriesFolder
    {
        get => _shared.CreateSeriesFolder;
        set => _shared.CreateSeriesFolder = value;
    }

    private string SharedSftpRemotePath => _shared.SftpCurrentRemotePath;
    private bool SharedIsSftpConnected => _shared.IsSftpConnected;
    public bool IsSftpConnected => _shared.IsSftpConnected;

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
                    OrganizePattern, "Naruto Shippuden", 1, "Episodio 1", "NarutoShippuden_Ep_001_SUB_ITA");
                return p + ".mp4";
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
                    MergePattern, "Naruto Shippuden", 1, "Episodio 1", "NarutoShippuden_Ep_001_SUB_ITA");
                return p + ".mp4";
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
                    SplitFilePattern, "Naruto Shippuden", 1, "Episodio 1", "NarutoShippuden_Ep_001_SUB_ITA");
                return p + ".mp4";
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
                if (!_shared.IsSftpConnected) OrganizeModeIsLocal = true;
                break;
        }
    }

    private void OnOrganizeModeIsLocalChanged(bool value)
    {
        OnPropertyChanged(nameof(OrganizeFolderLabel));
        OnPropertyChanged(nameof(MergeDestinationLabel));
        OnPropertyChanged(nameof(SplitSourceLabel));
        OnPropertyChanged(nameof(SplitDestinationLabel));

        if (!value && SharedIsSftpConnected && !string.IsNullOrWhiteSpace(SharedSftpRemotePath))
        {
            OrganizeFolder = SharedSftpRemotePath;
            MergeDestinationFolder = SharedSftpRemotePath;
            SplitSourceFolder = SharedSftpRemotePath;
            SplitDestinationFolder = SharedSftpRemotePath;
        }
    }

    partial void OnOrganizePatternChanged(string value) => OnPropertyChanged(nameof(OrganizePatternPreview));
    partial void OnMergePatternChanged(string value) => OnPropertyChanged(nameof(MergePatternPreview));
    partial void OnSplitFilePatternChanged(string value) => OnPropertyChanged(nameof(SplitFilePatternPreview));
    partial void OnSplitUniformModeChanged(bool value) => OnPropertyChanged(nameof(SplitSegmentsSummary));
    partial void OnSplitUniformEpisodesChanged(int value) => OnPropertyChanged(nameof(SplitSegmentsSummary));

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
            var ops = await _fileOrganizer.PreviewAsync(OrganizeFolder, OrganizePattern, false);

            PreviewOperations.Clear();
            foreach (var op in ops) PreviewOperations.Add(op);

            var conflicts = ops.Count(o => o.HasConflict);
            OrganizeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni proposte."
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

            var ops = await _fileOrganizer.PreviewRemoteFolderAsync(
                OrganizeFolder, OrganizePattern, CreateSeriesFolder);

            PreviewOperations.Clear();
            foreach (var op in ops) PreviewOperations.Add(op);

            var conflicts = ops.Count(o => o.HasConflict);
            OrganizeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni (remoto)."
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
    private void ClearPreviewOperations() { PreviewOperations.Clear(); OrganizeStatusMessage = "Proposte svuotate."; }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        try
        {
            await _fileOrganizer.ClearHistoryAsync();
            OperationHistory.Clear();
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
                OperationHistory.Clear();
                foreach (var op in history) OperationHistory.Add(op);
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
            var ops = await _fileOrganizer.PreviewMergeAsync(
                MergeSourceFolders.ToList(), MergeDestinationFolder, MergePattern, false);

            MergePreviewOperations.Clear();
            foreach (var op in ops) MergePreviewOperations.Add(op);

            var conflicts = ops.Count(o => o.HasConflict);
            MergeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni."
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
            var ops = await _fileOrganizer.PreviewRemoteMergeAsync(
                MergeSourceFolders.ToList(), MergeDestinationFolder, MergePattern, CreateSeriesFolder);

            MergePreviewOperations.Clear();
            foreach (var op in ops) MergePreviewOperations.Add(op);

            var conflicts = ops.Count(o => o.HasConflict);
            MergeStatusMessage = conflicts == 0
                ? $"Trovate {ops.Count} operazioni (remoto)."
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
            await LoadHistoryAsync();
        }
        catch (Exception ex) { MergeStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsMergeBusy = false; }
    }

    [RelayCommand]
    private void ClearMergePreviewOperations() { MergePreviewOperations.Clear(); MergeStatusMessage = "Proposte svuotate."; }

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
        OnPropertyChanged(nameof(SplitSegmentsSummary));
    }

    [RelayCommand]
    private void RemoveSplitSegment(SplitSegment? segment)
    {
        if (segment == null) return;
        segment.PropertyChanged -= OnSplitSegmentPropertyChanged;
        SplitSegments.Remove(segment);
        RenumberSegments();
        OnPropertyChanged(nameof(SplitSegmentsSummary));
    }

    [RelayCommand]
    private void ClearSplitSegments()
    {
        foreach (var seg in SplitSegments) seg.PropertyChanged -= OnSplitSegmentPropertyChanged;
        SplitSegments.Clear();
        OnPropertyChanged(nameof(SplitSegmentsSummary));
    }

    private void OnSplitSegmentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SplitSegment.EpisodeCount))
            OnPropertyChanged(nameof(SplitSegmentsSummary));
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

            SplitPreviewOperations.Clear();
            foreach (var op in ops) SplitPreviewOperations.Add(op);

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
                entries = await _sftpService.ListDirectoryAsync(SplitSourceFolder.TrimEnd('/'), CancellationToken.None);
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

            SplitPreviewOperations.Clear();
            foreach (var op in ops) SplitPreviewOperations.Add(op);

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
    private void ClearSplitPreviewOperations() { SplitPreviewOperations.Clear(); SplitStatusMessage = "Proposte svuotate."; }
}