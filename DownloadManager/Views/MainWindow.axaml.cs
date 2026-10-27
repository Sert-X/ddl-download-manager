using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using DownloadManager.Helpers;
using DownloadManager.Models;
using DownloadManager.Services.Sftp;
using DownloadManager.ViewModels;

namespace DownloadManager.Views;

public partial class MainWindow : Window
{
    private readonly ISftpService? _sftpService;

    // Snapshot per il drag interno
    private List<LocalFileEntry>? _draggedLocalEntries;
    private List<SftpRemoteEntry>? _draggedRemoteEntries;

    // Stato del puntatore per distinguere click da drag
    private Point _localDragStart;
    private Point _remoteDragStart;
    private bool _localPointerDown;
    private bool _remotePointerDown;

    // Args di PointerPressed, necessari per DoDragDropAsync in Avalonia 12
    private PointerPressedEventArgs? _localPressedArgs;
    private PointerPressedEventArgs? _remotePressedArgs;

    private bool _logCollapsed = true;   // all'avvio il log è chiuso
    private double _logHeightBeforeCollapse = 150;
    private bool _syncingEpisodeSelection;

    private void OnToggleLogClick(object? sender, RoutedEventArgs e)
    {
        _logCollapsed = ToggleLogButton.IsChecked != true;
        if (_logCollapsed)
        {
            _logHeightBeforeCollapse = LogPanel.Height;
            LogPanel.MaxHeight = 43;
            LogPanel.Height = 43;
            LogScrollViewer.IsVisible = false;
        }
        else
        {
            LogPanel.MaxHeight = 400;
            LogPanel.Height = _logHeightBeforeCollapse;
            LogScrollViewer.IsVisible = true;
        }
    }

    public MainWindow(MainWindowViewModel viewModel, ISftpService sftpService)
    {
        InitializeComponent();
        HookMappingGrid();

        Opened += (s, e) =>
        {
            try
            {
                var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
                if (screen == null) return;

                var workingArea = screen.WorkingArea;
                double screenWidthDip = workingArea.Width / screen.Scaling;
                double screenHeightDip = workingArea.Height / screen.Scaling;

                if (screenWidthDip < 1500 || screenHeightDip < 950)
                {
                    WindowState = WindowState.Maximized;
                }
                else
                {
                    Width = Math.Min(1500, screenWidthDip - 40);
                    Height = Math.Min(950, screenHeightDip - 40);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AUTOSIZE] {ex.Message}");
            }
        };

        DataContext = viewModel;
        _sftpService = sftpService;

        // Clic destro su una riga di qualunque tabella: la seleziona prima che si apra
        // il menu della tabella (i menu sono uno per tabella, non uno per riga).
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            var source = e.Source as Visual;
            if (source?.FindAncestorOfType<DataGrid>(includeSelf: true) is { } grid)
            {
                SelectRowUnderPointer(grid, e);
                return;
            }

            // Liste e albero: si ricorda l'elemento cliccato col destro ma NON lo si
            // seleziona (in Videobox selezionare un post ricaricherebbe la mappatura).
            if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
            var item = (Control?)source?.FindAncestorOfType<ListBoxItem>(includeSelf: true)
                       ?? source?.FindAncestorOfType<TreeViewItem>(includeSelf: true);
            if (item != null)
            {
                _contextGrid = null;
                _contextItem = item.DataContext;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Log: segue le voci nuove solo se l'utente è già in fondo (non lo strappa
        // via mentre sta leggendo righe più vecchie).
        // Una sola richiesta di scorrimento per gruppo di righe (prima una per riga) e solo a log aperto.
        bool scrollPending = false;
        viewModel.FilteredLogs.CollectionChanged += (_, e) =>
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
            var sv = LogScrollViewer;
            if (scrollPending || !sv.IsVisible) return;
            bool atBottom = sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 40;
            if (!atBottom) return;
            scrollPending = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { scrollPending = false; sv.ScrollToEnd(); }, Avalonia.Threading.DispatcherPriority.Background);
        };

        LocalFilesGrid.AddHandler(
            PointerPressedEvent, OnLocalGridPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        LocalFilesGrid.AddHandler(
            PointerMovedEvent, OnLocalGridPointerMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        LocalFilesGrid.AddHandler(
            PointerReleasedEvent, OnLocalGridPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        RemoteFilesGrid.AddHandler(
            PointerPressedEvent, OnRemoteGridPointerPressed,
            RoutingStrategies.Bubble, handledEventsToo: true);
        RemoteFilesGrid.AddHandler(
            PointerMovedEvent, OnRemoteGridPointerMoved,
            RoutingStrategies.Bubble, handledEventsToo: true);
        RemoteFilesGrid.AddHandler(
            PointerReleasedEvent, OnRemoteGridPointerReleased,
            RoutingStrategies.Bubble, handledEventsToo: true);

        // Episodi: caselle di spunta allineate alla selezione (Ctrl/Shift come in Windows)
        GridCheckboxSelection.Attach(EpisodesGrid, v => _syncingEpisodeSelection = v);
        GridCheckboxSelection.Attach(StreamingEpisodesGrid, v => _syncingStreamingEpisodeSelection = v);

        // SFTP: una lettera porta al primo file/cartella che inizia così
        static string? EntryName(object o) => o switch
        {
            LocalFileEntry l => l.Name,
            SftpRemoteEntry r => r.Name,
            _ => null
        };
        GridTypeAhead.Attach(LocalFilesGrid, EntryName);
        GridTypeAhead.Attach(RemoteFilesGrid, EntryName);
        GridTypeAhead.Attach(Server2FilesGrid, EntryName);
    }

    public MainWindow()
    {
        InitializeComponent();
        HookMappingGrid();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    // ============================================================
    //  HELPER
    // ============================================================

    // Tabella e riga dell'ultimo clic destro: i menu sono uno per tabella, quindi
    // l'elemento su cui agire si ricava da qui (non dal menu, che ha il DataContext
    // del ViewModel e un PlacementTarget non affidabile).
    private DataGrid? _contextGrid;
    private object? _contextItem;

    private T? ResolveItem<T>(object? sender) where T : class
    {
        if (sender is MenuItem { DataContext: T direct }) return direct;
        if (sender is Button { DataContext: T row }) return row;   // pulsanti nella riga
        if (_contextItem is T clicked) return clicked;
        return _contextGrid?.SelectedItem as T;
    }

    /// <summary>
    /// Elementi su cui agisce il menu: tutta la selezione se la riga cliccata ne fa parte
    /// (es. più file selezionati), altrimenti solo la riga cliccata.
    /// </summary>
    private List<T> ResolveItems<T>(object? sender) where T : class
    {
        if (_contextGrid != null && _contextItem != null &&
            _contextGrid.SelectedItems.Count > 1 && _contextGrid.SelectedItems.Contains(_contextItem))
            return _contextGrid.SelectedItems.OfType<T>().ToList();

        var one = ResolveItem<T>(sender);
        return one != null ? new List<T> { one } : new List<T>();
    }

    /// <summary>
    /// Clic destro su una riga: la ricorda per il menu e, se non fa già parte della
    /// selezione, la seleziona da sola.
    /// </summary>
    private void SelectRowUnderPointer(DataGrid grid, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;

        var row = (e.Source as Avalonia.Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);
        _contextGrid = grid;
        _contextItem = row?.DataContext;

        if (_contextItem != null && !grid.SelectedItems.Contains(_contextItem))
            grid.SelectedItem = _contextItem;
    }

    // ============================================================
    //  VIDEOBOX: SELEZIONE MULTIPLA NELLA TABELLA DELL'ANTEPRIMA
    // ============================================================

    private void OnMappingSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Il ViewModel è quello della tabella stessa (la tab Videobox)
        if (sender is not DataGrid { DataContext: VideoboxTabViewModel vb } grid) return;
        vb.SelectedMappingRows.Clear();
        if (grid.SelectedItems != null)
            foreach (var item in grid.SelectedItems)
                if (item is EpisodeMappingItem row) vb.SelectedMappingRows.Add(row);
    }

    /// <summary>Spazio: spunta (o toglie la spunta a) tutte le righe selezionate insieme.</summary>
    private void OnMappingGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || sender is not DataGrid { DataContext: VideoboxTabViewModel vb }) return;
        if (vb.SelectedMappingRows.Count == 0 || e.Source is TextBox) return;
        e.Handled = true;
        vb.ToggleSelectedRows();
    }

    private void HookMappingGrid()
    {
        // In "tunnel": la tabella non deve usare lo Spazio per conto suo (modifica della cella)
        this.FindControl<DataGrid>("MappingGrid")?.AddHandler(KeyDownEvent, OnMappingGridKeyDown, RoutingStrategies.Tunnel);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var storage = StorageProvider;
        if (storage == null) return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        if (folders == null || folders.Count == 0) return null;
        return folders[0].TryGetLocalPath();
    }

    /// <param name="service">Server in cui scegliere (null = connessione principale).</param>
    private async Task<string?> PickRemoteFolderAsync(string title, ISftpService? service = null, string? start = null)
    {
        service ??= _sftpService;
        if (service == null || !service.IsConnected) return null;

        // Si parte dalla cartella aperta su quel server
        start ??= (ReferenceEquals(service, _sftpService) ? Vm?.Sftp.MainRemotePath : Vm?.Sftp.SftpCurrentRemotePath) ?? "/";
        return await RemoteFolderPickerDialog.ShowAsync(this, service, start, title);
    }

    /// <summary>Cartelle del server scelto in Organizza file.</summary>
    private Task<string?> PickOrganizerRemoteFolderAsync(string title, string? current) =>
        PickRemoteFolderAsync(title, Vm?.Organizer.RemoteService,
            string.IsNullOrWhiteSpace(current) || !current.StartsWith('/') ? Vm?.Organizer.RemoteStartPath : current);

    // ============================================================
    //  RICERCA
    // ============================================================

    private void OnSearchButtonClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var combo = this.FindControl<ComboBox>("SearchComboBox");
        if (combo == null) return;

        var text = combo.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text) && combo.SelectedItem is string selected) text = selected.Trim();
        if (string.IsNullOrEmpty(text)) { Vm.AnimeDownload.StatusMessage = "Inserisci un termine."; return; }

        _ = Vm.AnimeDownload.SearchWithTermAsync(text);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnSearchButtonClick(sender, new RoutedEventArgs()); e.Handled = true; }
    }

    private void OnClearSearchHistoryClick(object? sender, RoutedEventArgs e) => Vm?.AnimeDownload.ClearSearchHistoryCommand.Execute(null);

    // ============================================================
    //  SELEZIONE EPISODI
    // ============================================================

    private void OnEpisodesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm == null) return;
        if (_syncingEpisodeSelection) return;
        if (sender is not DataGrid grid) return;

        var selected = new HashSet<Episode>();
        if (grid.SelectedItems != null)
            foreach (var item in grid.SelectedItems)
                if (item is Episode ep) selected.Add(ep);

        foreach (var ep in Vm.AnimeDownload.Episodes)
        {
            bool shouldBeSelected = selected.Contains(ep);
            if (ep.IsSelected != shouldBeSelected) ep.IsSelected = shouldBeSelected;
        }
    }

    private void OnSelectAllEpisodesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.AnimeDownload.SelectAllEpisodesCommand.Execute(null);

        if (this.FindControl<DataGrid>("EpisodesGrid") is not DataGrid grid) return;

        _syncingEpisodeSelection = true;
        try { grid.SelectAll(); }
        finally { _syncingEpisodeSelection = false; }
    }

    private void OnDeselectAllEpisodesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.AnimeDownload.DeselectAllEpisodesCommand.Execute(null);

        if (this.FindControl<DataGrid>("EpisodesGrid") is not DataGrid grid) return;

        _syncingEpisodeSelection = true;
        try { grid.SelectedItems?.Clear(); }
        finally { _syncingEpisodeSelection = false; }
    }

    private void OnInvertEpisodeSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        Vm.AnimeDownload.InvertEpisodeSelectionCommand.Execute(null);

        if (this.FindControl<DataGrid>("EpisodesGrid") is not DataGrid grid) return;

        _syncingEpisodeSelection = true;
        try
        {
            grid.SelectedItems?.Clear();
            foreach (var ep in Vm.AnimeDownload.Episodes)
                if (ep.IsSelected) grid.SelectedItems?.Add(ep);
        }
        finally { _syncingEpisodeSelection = false; }
    }

    // ============================================================
    //  FOLDER PICKER LOCALE
    // ============================================================

    private async void OnBrowseBaseFolderClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella base download");
        if (!string.IsNullOrEmpty(folder)) Vm?.AnimeDownload.SetBaseDownloadFolder(folder);
    }

    private async void OnBrowseOrganizeFolderClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella da organizzare");
        if (!string.IsNullOrEmpty(folder)) Vm?.Organizer.SetOrganizeFolder(folder);
    }

    private async void OnBrowseMergeDestClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella di destinazione");
        if (!string.IsNullOrEmpty(folder)) Vm?.Organizer.SetMergeDestinationFolder(folder);
    }

    private async void OnBrowseSplitSourceClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella da dividere");
        if (!string.IsNullOrEmpty(folder)) Vm?.Organizer.SetSplitSourceFolder(folder);
    }

    private async void OnBrowseSplitDestClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella di destinazione");
        if (!string.IsNullOrEmpty(folder)) Vm?.Organizer.SetSplitDestinationFolder(folder);
    }

    // ============================================================
    //  FOLDER PICKER REMOTO
    // ============================================================

    private async void OnBrowseOrganizeFolderRemoteClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickOrganizerRemoteFolderAsync("Seleziona cartella remota da organizzare", Vm?.Organizer.OrganizeFolder);
        if (!string.IsNullOrEmpty(folder) && Vm != null) Vm.Organizer.OrganizeFolder = folder;
    }

    private async void OnBrowseMergeDestRemoteClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickOrganizerRemoteFolderAsync("Seleziona destinazione remota per il merge", Vm?.Organizer.MergeDestinationFolder);
        if (!string.IsNullOrEmpty(folder) && Vm != null) Vm.Organizer.MergeDestinationFolder = folder;
    }

    private async void OnBrowseSplitSourceRemoteClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickOrganizerRemoteFolderAsync("Seleziona cartella remota da dividere", Vm?.Organizer.SplitSourceFolder);
        if (!string.IsNullOrEmpty(folder) && Vm != null) Vm.Organizer.SplitSourceFolder = folder;
    }

    private async void OnBrowseSplitDestRemoteClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickOrganizerRemoteFolderAsync("Seleziona destinazione remota per lo split", Vm?.Organizer.SplitDestinationFolder);
        if (!string.IsNullOrEmpty(folder) && Vm != null) Vm.Organizer.SplitDestinationFolder = folder;
    }

    private async void OnBrowseMergeAddRemoteFolderClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickOrganizerRemoteFolderAsync("Aggiungi cartella sorgente remota", null);
        if (!string.IsNullOrEmpty(folder)) Vm?.Organizer.AddMergeFolder(folder);
    }

    // ============================================================
    //  MERGE
    // ============================================================

    private async void OnMergeAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        if (Vm.Organizer.OrganizeModeIsLocal)
        {
            var storage = StorageProvider;
            if (storage == null) return;

            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Aggiungi cartella sorgente",
                AllowMultiple = true
            });

            if (folders == null) return;

            foreach (var f in folders)
            {
                var path = f.TryGetLocalPath();
                if (!string.IsNullOrEmpty(path)) Vm.Organizer.AddMergeFolder(path);
            }
        }
        else
        {
            // Prima la cartella scelta veniva ignorata
            var folder = await PickOrganizerRemoteFolderAsync("Aggiungi cartella sorgente remota", null);
            if (!string.IsNullOrEmpty(folder)) Vm.Organizer.AddMergeFolder(folder);
        }
    }

    private void OnMergeRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Organizer.SelectedMergeFolder == null) return;
        Vm.Organizer.RemoveMergeFolder(Vm.Organizer.SelectedMergeFolder);
    }

    private void OnMergeMoveUpClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Organizer.SelectedMergeFolder == null) return;
        Vm.Organizer.MoveMergeFolderUp(Vm.Organizer.SelectedMergeFolder);
    }

    private void OnMergeMoveDownClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Organizer.SelectedMergeFolder == null) return;
        Vm.Organizer.MoveMergeFolderDown(Vm.Organizer.SelectedMergeFolder);
    }

    private void OnMergeClearClick(object? sender, RoutedEventArgs e) => Vm?.Organizer.ClearMergeFolders();

    // ============================================================
    //  SPLIT
    // ============================================================

    private void OnSplitRemoveSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not SplitSegment segment) return;
        Vm?.Organizer.RemoveSplitSegmentCommand.Execute(segment);
    }

    // ============================================================
    //  SFTP CONFIG
    // ============================================================

    private void OnSftpNewProfileClick(object? sender, RoutedEventArgs e) => Vm?.Sftp.SftpNewProfileCommand.Execute(null);

    private async void OnBrowseSftpKeyClick(object? sender, RoutedEventArgs e)
    {
        var storage = StorageProvider;
        if (storage == null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Seleziona chiave privata SSH",
            AllowMultiple = false
        });

        if (files == null || files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path) && Vm != null) Vm.Sftp.SftpPrivateKeyPath = path;
    }

    private void OnSftpRefreshClick(object? sender, RoutedEventArgs e) => Vm?.Sftp.SftpRefreshCommand.Execute(null);

    private async void OnSftpEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not SftpRemoteEntry entry) return;
        if (Vm == null) return;

        if (entry.IsDirectory)
        {
            Vm.Sftp.SftpCurrentRemotePath = entry.FullPath;
            await Vm.Sftp.SftpRefreshCommand.ExecuteAsync(null);
        }
    }

    // ============================================================
    //  SFTP BROWSER LOCALE
    // ============================================================

    private void OnSftpLocalRefreshClick(object? sender, RoutedEventArgs e) => Vm?.Sftp.SftpRefreshLocalCommand.Execute(null);

    private async void OnSftpLocalEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not LocalFileEntry entry) return;
        if (Vm == null) return;

        if (entry.IsDirectory)
        {
            Vm.Sftp.SftpCurrentLocalPath = entry.FullPath;
            await Vm.Sftp.SftpRefreshLocalCommand.ExecuteAsync(null);
        }
    }

    private async void OnBrowseSftpUploadLocalClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella locale da caricare");
        if (!string.IsNullOrEmpty(folder)) Vm?.Sftp.SetSftpUploadLocalFolder(folder);
    }

    private async void OnBrowseSftpDownloadLocalClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella locale di destinazione");
        if (!string.IsNullOrEmpty(folder)) Vm?.Sftp.SetSftpDownloadLocalFolder(folder);
    }

    // ============================================================
    //  SFTP SELEZIONE MULTIPLA
    // ============================================================

    private void OnLocalFilesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm == null) return;
        if (sender is not DataGrid grid) return;

        Vm.Sftp.SelectedLocalEntries.Clear();
        if (grid.SelectedItems != null)
            foreach (var item in grid.SelectedItems)
                if (item is LocalFileEntry entry) Vm.Sftp.SelectedLocalEntries.Add(entry);
    }

    private void OnRemoteFilesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm == null) return;
        if (sender is not DataGrid grid) return;

        Vm.Sftp.SelectedRemoteEntries.Clear();
        if (grid.SelectedItems != null)
            foreach (var item in grid.SelectedItems)
                if (item is SftpRemoteEntry entry) Vm.Sftp.SelectedRemoteEntries.Add(entry);
    }

    // ============================================================
    //  DELETE CON TASTO CANC
    // ============================================================

    private async void OnLocalGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        if (Vm == null) return;

        var entries = new List<LocalFileEntry>();
        if (Vm.Sftp.SelectedLocalEntries.Count > 0) entries.AddRange(Vm.Sftp.SelectedLocalEntries);
        else if (Vm.Sftp.SftpSelectedLocalEntry != null) entries.Add(Vm.Sftp.SftpSelectedLocalEntry);

        if (entries.Count == 0) return;

        var msg = entries.Count == 1
            ? $"Eliminare '{entries[0].Name}'?"
            : $"Eliminare {entries.Count} elementi?";
        if (entries.Any(x => x.IsDirectory))
            msg += "\n\nLe cartelle verranno eliminate con tutto il contenuto.";
        msg += "\n\nQuesta azione non pu\u00F2 essere annullata.";

        var ok = await ConfirmDialog.ShowAsync(this, msg, "Conferma eliminazione");
        if (sender is DataGrid g) g.Focus();
        if (!ok) return;

        await Vm.Sftp.SftpLocalDeleteManyAsync(entries);
    }

    // ============================================================
    //  SECONDO SERVER (pannello di sinistra) E COPIE TRA SERVER
    // ============================================================

    private void OnServer2SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm == null || sender is not DataGrid grid) return;
        Vm.Sftp.SelectedServer2Entries.Clear();
        foreach (var item in grid.SelectedItems)
            if (item is SftpRemoteEntry entry) Vm.Sftp.SelectedServer2Entries.Add(entry);
    }

    private async void OnServer2DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is DataGrid { SelectedItem: SftpRemoteEntry entry } && Vm != null)
            await Vm.Sftp.Server2OpenAsync(entry);
    }

    private async void OnServer2OpenClick(object? sender, RoutedEventArgs e)
    {
        if (Vm != null) await Vm.Sftp.Server2OpenAsync(ResolveItem<SftpRemoteEntry>(sender));
    }

    private void OnServer2PathKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Vm?.Sftp.Server2RefreshCommand.Execute(null);
    }

    private async void OnServer2NewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var name = await InputDialog.ShowAsync(this, $"Nome della nuova cartella su '{Vm.Sftp.Server2Name}':", "Nuova cartella", "Nuova cartella");
        if (!string.IsNullOrEmpty(name)) await Vm.Sftp.Server2CreateFolderAsync(name);
    }

    private void OnServer2CopyToMainClick(object? sender, RoutedEventArgs e) =>
        Vm?.Sftp.CopyServer2ToMainCommand.Execute(null);

    private void OnRemoteCopyToServer2Click(object? sender, RoutedEventArgs e) =>
        Vm?.Sftp.CopyMainToServer2Command.Execute(null);

    private async void OnServer2DeleteClick(object? sender, RoutedEventArgs e) =>
        await ConfirmServer2DeleteAsync(ResolveItems<SftpRemoteEntry>(sender), sender);

    private async void OnServer2GridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Vm == null) return;
        e.Handled = true;
        var entries = Vm.Sftp.SelectedServer2Entries.ToList();
        if (entries.Count == 0 && Vm.Sftp.SelectedServer2Entry != null) entries.Add(Vm.Sftp.SelectedServer2Entry);
        await ConfirmServer2DeleteAsync(entries, sender);
    }

    private async Task ConfirmServer2DeleteAsync(List<SftpRemoteEntry> entries, object? sender)
    {
        if (entries.Count == 0 || Vm == null) return;

        var server = Vm.Sftp.Server2Name;
        var msg = entries.Count == 1
            ? $"Eliminare '{entries[0].Name}' da '{server}'?"
            : $"Eliminare {entries.Count} elementi da '{server}'?";
        if (entries.Any(x => x.IsDirectory))
            msg += "\n\nLe cartelle verranno eliminate con tutto il contenuto.";
        msg += "\n\nQuesta azione non può essere annullata.";

        var ok = await ConfirmDialog.ShowAsync(this, msg, "Conferma eliminazione");
        if (sender is DataGrid g) g.Focus();
        if (ok) await Vm.Sftp.Server2DeleteManyAsync(entries);
    }

    private void OnRelayJobResumeClick(object? sender, RoutedEventArgs e) { if (ResolveItem<SftpRelayJob>(sender) is { } j) Vm?.Sftp.ResumeRelayJob(j); }
    private void OnRelayJobPauseClick(object? sender, RoutedEventArgs e) { if (ResolveItem<SftpRelayJob>(sender) is { } j) Vm?.Sftp.PauseRelayJob(j); }
    private void OnRelayJobCancelClick(object? sender, RoutedEventArgs e) { if (ResolveItem<SftpRelayJob>(sender) is { } j) Vm?.Sftp.CancelRelayJob(j); }
    private void OnRelayJobRemoveClick(object? sender, RoutedEventArgs e) { if (ResolveItem<SftpRelayJob>(sender) is { } j) Vm?.Sftp.RemoveRelayJob(j); }

    private async void OnRemoteGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        if (Vm == null) return;

        var entries = new List<SftpRemoteEntry>();
        if (Vm.Sftp.SelectedRemoteEntries.Count > 0) entries.AddRange(Vm.Sftp.SelectedRemoteEntries);
        else if (Vm.Sftp.SftpSelectedRemoteEntry != null) entries.Add(Vm.Sftp.SftpSelectedRemoteEntry);

        if (entries.Count == 0) return;

        var msg = entries.Count == 1
            ? $"Eliminare '{entries[0].Name}' dal server?"
            : $"Eliminare {entries.Count} elementi dal server?";
        if (entries.Any(x => x.IsDirectory))
            msg += "\n\nLe cartelle verranno eliminate con tutto il contenuto.";
        msg += "\n\nQuesta azione non pu\u00F2 essere annullata.";

        var ok = await ConfirmDialog.ShowAsync(this, msg, "Conferma eliminazione");
        if (sender is DataGrid g) g.Focus();
        if (!ok) return;

        await Vm.Sftp.SftpRemoteDeleteManyAsync(entries);
    }

    // ============================================================
    //  SFTP CRUD LOCALE
    // ============================================================

    private async void OnLocalNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var name = await InputDialog.ShowAsync(this, "Nome nuova cartella:", "Nuova cartella", "Nuova cartella");
        if (!string.IsNullOrEmpty(name)) await Vm.Sftp.SftpLocalCreateFolderAsync(name);
    }

    private async void OnLocalOpenClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<LocalFileEntry>(sender);
        if (entry == null || Vm == null) return;
        if (!entry.IsDirectory) return;

        Vm.Sftp.SftpCurrentLocalPath = entry.FullPath;
        await Vm.Sftp.SftpRefreshLocalCommand.ExecuteAsync(null);
    }

    private async void OnLocalRenameClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<LocalFileEntry>(sender);
        if (entry == null || Vm == null) return;

        var newName = await InputDialog.ShowAsync(this, $"Rinomina '{entry.Name}' in:", entry.Name, "Rinomina", selectNameOnly: !entry.IsDirectory);
        if (string.IsNullOrEmpty(newName) || newName == entry.Name) return;

        await Vm.Sftp.SftpLocalRenameAsync(entry, newName);
    }

    private async void OnLocalDeleteClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<LocalFileEntry>(sender);
        if (entry == null || Vm == null) return;

        var tipo = entry.IsDirectory ? "cartella" : "file";
        var extra = entry.IsDirectory ? " e tutto il suo contenuto" : "";
        var msg = $"Eliminare la {tipo} '{entry.Name}'{extra}?\n\nQuesta azione non pu\u00F2 essere annullata.";

        var ok = await ConfirmDialog.ShowAsync(this, msg, "Conferma eliminazione");
        if (!ok) return;

        await Vm.Sftp.SftpLocalDeleteManyAsync(new[] { entry });
    }

    // ============================================================
    //  SFTP CRUD REMOTO
    // ============================================================

    // ============================================================
    //  VIDEOBOX — copia negli appunti
    // ============================================================

    private async Task CopyAsync(string? text, string what)
    {
        if (Vm == null) return;
        if (string.IsNullOrEmpty(text))
        {
            Vm.Videobox.StatusMessage = $"Niente da copiare ({what} vuoto).";
            return;
        }
        try
        {
            await ClipboardHelper.SetTextAsync(text);
            var lines = text.Split('\n').Length;
            Vm.Videobox.StatusMessage = lines > 1 ? $"Copiati negli appunti: {lines} {what}." : $"Copiato negli appunti: {what}.";
        }
        catch (Exception ex) { Vm.Videobox.StatusMessage = $"Copia non riuscita: {ex.Message}"; }
    }

    private static string JoinLines(IEnumerable<string?> values) =>
        string.Join(Environment.NewLine, values.Where(v => !string.IsNullOrWhiteSpace(v)));

    private async void OnVbCopyPostTitleClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(ResolveItem<VideoboxPostItem>(sender)?.Title, "titolo");
    private async void OnVbCopyPostUrlClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(ResolveItem<VideoboxPostItem>(sender)?.Url, "indirizzo del post");
    private async void OnVbCopyPostLinksClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(JoinLines(ResolveItem<VideoboxPostItem>(sender)?.Links ?? new List<string>()), "link");
    private void OnVbOpenPostClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveItem<VideoboxPostItem>(sender) is { } post) Vm?.Videobox.OpenPostUrl(post);
    }

    private async void OnVbCopyNodeNameClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(ResolveItem<SftpNode>(sender)?.Name, "nome");
    private async void OnVbCopyNodePathClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(ResolveItem<SftpNode>(sender)?.FullPath, "percorso");
    private async void OnVbCopyNodeUrlClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveItem<SftpNode>(sender) is { } node && Vm != null)
            await CopyAsync(Vm.Videobox.GetPublicUrl(node), "link pubblico");
    }
    /// <summary>"Togli dall'albero" solo sulle radici delle sorgenti.</summary>
    private void OnVbTreeMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        bool isRoot = _contextItem is SftpNode { IsSourceRoot: true };
        foreach (var c in menu.Items.OfType<Control>())
            if (c.Tag as string == "sourceRoot") c.IsVisible = isRoot;
    }

    private void OnVbDiscardSourceClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveItem<SftpNode>(sender) is { IsSourceRoot: true } root) Vm?.Videobox.DiscardSourceRoot(root);
    }

    private async void OnVbCopyNodeAllUrlsClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveItem<SftpNode>(sender) is { } node && Vm != null)
            await CopyAsync(JoinLines(Vm.Videobox.GetPublicUrlsUnder(node)), "link");
    }

    private async void OnVbCopyOldUrlClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(ResolveItem<EpisodeMappingItem>(sender)?.OldUrl, "vecchio URL");
    private async void OnVbCopyNewUrlClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(ResolveItem<EpisodeMappingItem>(sender)?.NewUrl, "nuovo URL");
    private async void OnVbCopyFileNameClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(ResolveItem<EpisodeMappingItem>(sender)?.SftpFileName, "nome file");
    private async void OnVbCopyAllOldUrlsClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(JoinLines(Vm?.Videobox.MappingItems.Select(m => m.OldUrl) ?? Enumerable.Empty<string>()), "vecchi URL");
    private async void OnVbCopyAllNewUrlsClick(object? sender, RoutedEventArgs e) =>
        await CopyAsync(JoinLines(Vm?.Videobox.MappingItems.Select(m => m.NewUrl) ?? Enumerable.Empty<string>()), "nuovi URL");

    private async void OnLocalMoveClick(object? sender, RoutedEventArgs e)
    {
        var entries = ResolveItems<LocalFileEntry>(sender);
        if (entries.Count == 0 || Vm == null) return;

        var title = entries.Count == 1 ? $"Sposta '{entries[0].Name}' in..." : $"Sposta {entries.Count} elementi in...";
        var dest = await PickFolderAsync(title);
        if (string.IsNullOrEmpty(dest)) return;

        await Vm.Sftp.SftpLocalMoveManyAsync(entries, dest);
    }

    private async void OnRemoteMoveClick(object? sender, RoutedEventArgs e)
    {
        var entries = ResolveItems<SftpRemoteEntry>(sender);
        if (entries.Count == 0 || Vm == null) return;

        var title = entries.Count == 1 ? $"Sposta '{entries[0].Name}' in..." : $"Sposta {entries.Count} elementi in...";
        // Cartelle del server della scheda aperta nel pannello di destra
        var dest = await PickRemoteFolderAsync(title, Vm.Sftp.ActiveRemoteService);
        if (string.IsNullOrEmpty(dest)) return;

        await Vm.Sftp.SftpRemoteMoveManyAsync(entries, dest);
    }

    private async void OnRemoteNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var name = await InputDialog.ShowAsync(this, "Nome nuova cartella remota:", "Nuova cartella", "Nuova cartella");
        if (!string.IsNullOrEmpty(name)) await Vm.Sftp.SftpRemoteCreateFolderAsync(name);
    }

    private async void OnRemoteOpenClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<SftpRemoteEntry>(sender);
        if (entry == null || Vm == null) return;
        if (!entry.IsDirectory) return;

        Vm.Sftp.SftpCurrentRemotePath = entry.FullPath;
        await Vm.Sftp.SftpRefreshCommand.ExecuteAsync(null);
    }

    private async void OnRemoteRenameClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<SftpRemoteEntry>(sender);
        if (entry == null || Vm == null) return;

        var newName = await InputDialog.ShowAsync(this, $"Rinomina '{entry.Name}' in:", entry.Name, "Rinomina", selectNameOnly: !entry.IsDirectory);
        if (string.IsNullOrEmpty(newName) || newName == entry.Name) return;

        await Vm.Sftp.SftpRemoteRenameAsync(entry, newName);
    }

    // ============================================================
    //  DUPLICA (chiede il nome della copia)
    // ============================================================

    private async Task<string?> AskCopyNameAsync(string name, bool isDirectory, IEnumerable<string> existing)
    {
        var suggested = ViewModels.SftpTabViewModel.SuggestCopyName(name, isDirectory, existing);
        var tipo = isDirectory ? "cartella" : "file";
        var newName = await InputDialog.ShowAsync(this, $"Nome della copia del {tipo} '{name}':", suggested, "Duplica", selectNameOnly: !isDirectory);
        return string.IsNullOrWhiteSpace(newName) || newName.Trim() == name ? null : newName.Trim();
    }

    private async void OnLocalDuplicateClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveItem<LocalFileEntry>(sender) is not { } entry || Vm == null) return;
        var name = await AskCopyNameAsync(entry.Name, entry.IsDirectory, Vm.Sftp.SftpLocalEntries.Select(x => x.Name));
        if (name != null) await Vm.Sftp.SftpLocalDuplicateAsync(entry, name);
    }

    private async void OnRemoteDuplicateClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveItem<SftpRemoteEntry>(sender) is not { } entry || Vm == null) return;
        var name = await AskCopyNameAsync(entry.Name, entry.IsDirectory, Vm.Sftp.SftpRemoteEntries.Select(x => x.Name));
        if (name != null) await Vm.Sftp.SftpRemoteDuplicateAsync(entry, name);
    }

    private async void OnServer2DuplicateClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveItem<SftpRemoteEntry>(sender) is not { } entry || Vm == null) return;
        var name = await AskCopyNameAsync(entry.Name, entry.IsDirectory, Vm.Sftp.Server2Entries.Select(x => x.Name));
        if (name != null) await Vm.Sftp.Server2DuplicateAsync(entry, name);
    }

    private async void OnRemoteDeleteClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<SftpRemoteEntry>(sender);
        if (entry == null || Vm == null) return;

        var tipo = entry.IsDirectory ? "cartella" : "file";
        var extra = entry.IsDirectory ? " e tutto il suo contenuto sul server" : "";
        var msg = $"Eliminare la {tipo} remota '{entry.Name}'{extra}?\n\nQuesta azione non pu\u00F2 essere annullata.";

        var ok = await ConfirmDialog.ShowAsync(this, msg, "Conferma eliminazione");
        if (!ok) return;

        await Vm.Sftp.SftpRemoteDeleteManyAsync(new[] { entry });
    }

    private async void OnProxyImportFileClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var request = await ProxyImportDialog.ShowAsync(this, ProxyTabViewModel.ImportTargets, Vm.Proxy.SelectedTab?.Key);
        if (request != null) await Vm.Proxy.ImportAsync(request.Content, request.Providers);
    }

    /// <summary>
    /// "Elimina tutto…" del menu "Pulisci lista" di ogni coda: chiede conferma (il fuoco parte da
    /// "Annulla") e solo allora esegue il comando legato in Tag. Il testo della domanda è nel
    /// CommandParameter.
    /// </summary>
    private async void OnConfirmRemoveAllClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: System.Windows.Input.ICommand command } control) return;
        var param = (control as MenuItem)?.CommandParameter ?? (control as Button)?.CommandParameter;
        var prompt = param as string ?? "Togliere tutti gli elementi dalla lista?";
        var title = control is MenuItem ? "Elimina tutto" : "Conferma";
        var ok = await ConfirmDialog.ShowAsync(this, prompt, title, safeDefault: true);
        if (ok && command.CanExecute(null)) command.Execute(null);
    }

    private async void OnProxyDeleteCheckedClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var n = Vm.Proxy.CheckedForDeletionCount;
        if (n == 0) { Vm.Proxy.ProxyStatusMessage = "Nessun proxy selezionato."; return; }
        var ok = await ConfirmDialog.ShowAsync(this,
            $"Eliminare definitivamente {n} proxy dall'archivio e da tutte le liste?", "Conferma eliminazione");
        if (ok) await Vm.Proxy.DeleteCheckedAsync();
    }

    private async void OnProxyCleanupArchiveClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var n = Vm.Proxy.UselessCount;
        if (n == 0) { Vm.Proxy.ProxyStatusMessage = "Archivio già pulito: nessun proxy fallito o escluso ovunque."; return; }
        var ok = await ConfirmDialog.ShowAsync(this,
            $"Eliminare definitivamente {n} proxy che risultano falliti o esclusi in tutte le loro liste?", "Pulisci archivio");
        if (ok) await Vm.Proxy.CleanupArchiveAsync();
    }

    private bool _syncingProxySelection;

    private void OnProxyListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm == null || _syncingProxySelection) return;
        if (sender is not ListBox) return;

        _syncingProxySelection = true;
        try
        {
            if (e.AddedItems != null)
                foreach (var item in e.AddedItems)
                    if (item is ProxyConfig p) p.IsSelected = true;

            if (e.RemovedItems != null)
                foreach (var item in e.RemovedItems)
                    if (item is ProxyConfig p) p.IsSelected = false;
        }
        finally
        {
            _syncingProxySelection = false;
        }
    }

    // ============================================================
    //  SFTP UPLOAD/DOWNLOAD JOB
    // ============================================================

    private void OnSftpJobResumeClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpUploadJob>(sender); if (j != null) Vm?.Sftp.ResumeSftpJobPublic(j); }
    private void OnSftpJobPauseClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpUploadJob>(sender); if (j != null) Vm?.Sftp.PauseSftpJobPublic(j); }
    private void OnSftpJobCancelClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpUploadJob>(sender); if (j != null) Vm?.Sftp.CancelSftpJobPublic(j); }
    private void OnSftpJobRemoveClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpUploadJob>(sender); if (j != null) Vm?.Sftp.RemoveSftpJobPublic(j); }

    private void OnSftpDownloadJobResumeClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpDownloadJob>(sender); if (j != null) Vm?.Sftp.ResumeSftpDownloadJobPublic(j); }
    private void OnSftpDownloadJobPauseClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpDownloadJob>(sender); if (j != null) Vm?.Sftp.PauseSftpDownloadJobPublic(j); }
    private void OnSftpDownloadJobCancelClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpDownloadJob>(sender); if (j != null) Vm?.Sftp.CancelSftpDownloadJobPublic(j); }
    private void OnSftpDownloadJobRemoveClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpDownloadJob>(sender); if (j != null) Vm?.Sftp.RemoveSftpDownloadJobPublic(j); }

    // ============================================================
    //  DOPPIO CLICK DOWNLOAD TAB
    // ============================================================

    private async void OnSearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        var selected = sender switch
        {
            DataGrid g => g.SelectedItem,
            ListBox l => l.SelectedItem,
            _ => null
        };
        if (selected is not AnimeSearchResult anime) return;
        if (Vm == null) return;
        await Vm.AnimeDownload.LoadEpisodesCommand.ExecuteAsync(anime);
    }

    /// <summary>Pulsante "Analizza" sulla locandina: come il doppio clic.</summary>
    private async void OnAnalyzeAnimeClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Vm == null || (sender as Control)?.DataContext is not AnimeSearchResult anime) return;
        await Vm.AnimeDownload.LoadEpisodesCommand.ExecuteAsync(anime);
    }

    private async void OnAnalyzeStreamingClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Vm == null || (sender as Control)?.DataContext is not MovieSearchResult result) return;
        Vm.Streaming.SelectedResult = result;
        await Vm.Streaming.LoadSeasonsCommand.ExecuteAsync(result);
    }

    private async void OnEpisodeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not Episode episode) return;
        if (Vm == null) return;
        await Vm.AnimeDownload.DownloadEpisodeCommand.ExecuteAsync(episode);
    }

    // ============================================================
    //  CONTEXT MENU DOWNLOAD (ANIME)
    // ============================================================

    private void OnResumeDownloadClick(object? sender, RoutedEventArgs e) { var i = ResolveItem<DownloadItem>(sender); if (i != null) Vm?.AnimeDownload.ResumeItemPublic(i); }
    private void OnForceDownloadClick(object? sender, RoutedEventArgs e) { var i = ResolveItem<DownloadItem>(sender); if (i != null) Vm?.AnimeDownload.ForceItemPublic(i); }
    private void OnUnforceDownloadClick(object? sender, RoutedEventArgs e) { var i = ResolveItem<DownloadItem>(sender); if (i != null) Vm?.AnimeDownload.UnforceItemPublic(i); }
    private void OnPauseDownloadClick(object? sender, RoutedEventArgs e) { var i = ResolveItem<DownloadItem>(sender); if (i != null) Vm?.AnimeDownload.PauseItemPublic(i); }
    private void OnCancelDownloadClick(object? sender, RoutedEventArgs e) { var i = ResolveItem<DownloadItem>(sender); if (i != null) Vm?.AnimeDownload.CancelItemPublic(i); }

    // ============================================================
    //  PULSANTI ITEM (ANIME)
    // ============================================================

    private void OnDownloadItemResumeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DownloadItem item)
            Vm?.AnimeDownload.ResumeItemPublic(item);
    }

    private void OnDownloadItemForceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DownloadItem item)
            Vm?.AnimeDownload.ForceItemPublic(item);
    }

    private void OnDownloadItemPauseClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DownloadItem item)
            Vm?.AnimeDownload.PauseItemPublic(item);
    }

    private void OnDownloadItemCancelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DownloadItem item)
            Vm?.AnimeDownload.CancelItemPublic(item);
    }

    private async void OnDownloadItemRetryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DownloadItem item && Vm != null)
            await Vm.AnimeDownload.RetryItemPublicAsync(item);
    }

    private async void OnDownloadItemRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DownloadItem item && Vm != null)
            await Vm.AnimeDownload.RemoveItemPublicAsync(item);
    }

    // ============================================================
    //  PULSANTI SERIE (ANIME)
    // ============================================================

    private async void OnRetryFailedGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is SeriesGroup g && Vm != null)
            await Vm.AnimeDownload.RetryFailedGroupPublicAsync(g);
    }

    private async void OnRetryCancelledGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is SeriesGroup g && Vm != null)
            await Vm.AnimeDownload.RetryCancelledGroupPublicAsync(g);
    }

    private async void OnRemoveDownloadClick(object? sender, RoutedEventArgs e)
    {
        var i = ResolveItem<DownloadItem>(sender);
        if (i != null && Vm != null) await Vm.AnimeDownload.RemoveItemPublicAsync(i);
    }

    // ============================================================
    //  PULSANTI GRUPPO (ANIME)
    // ============================================================

    private void OnResumeGroupClick(object? sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is SeriesGroup g) Vm?.AnimeDownload.ResumeGroupPublic(g); }
    private void OnForceGroupClick(object? sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is SeriesGroup g) Vm?.AnimeDownload.ForceGroupPublic(g); }
    private void OnUnforceGroupClick(object? sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is SeriesGroup g) Vm?.AnimeDownload.UnforceGroupPublic(g); }
    private void OnPauseGroupClick(object? sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is SeriesGroup g) Vm?.AnimeDownload.PauseGroupPublic(g); }
    private void OnCancelGroupClick(object? sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is SeriesGroup g) Vm?.AnimeDownload.CancelGroupPublic(g); }

    private async void OnRemoveGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        if (b.DataContext is not SeriesGroup g) return;
        if (Vm == null) return;
        await Vm.AnimeDownload.RemoveGroupPublicAsync(g);
    }

    // ============================================================
    //  DRAG & DROP — RILEVAMENTO
    // ============================================================

    private void OnLocalGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
        if (!DragGuard.IsOnRow(e.Source)) { _localPointerDown = false; _localPressedArgs = null; return; }
        _localDragStart = e.GetPosition(grid);
        _localPointerDown = true;
        _localPressedArgs = e;
    }

    private void OnLocalGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _localPointerDown = false;
        _localPressedArgs = null;
    }

    private async void OnLocalGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_localPointerDown) return;
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed)
        {
            _localPointerDown = false;
            return;
        }

        var pos = e.GetPosition(grid);
        if (Math.Abs(pos.X - _localDragStart.X) < 5 &&
            Math.Abs(pos.Y - _localDragStart.Y) < 5) return;

        _localPointerDown = false;
        var pressed = _localPressedArgs;
        _localPressedArgs = null;
        if (pressed == null) return;

        var items = grid.SelectedItems.OfType<LocalFileEntry>().ToList();
        if (items.Count == 0) return;

        _draggedLocalEntries = items;
        _draggedRemoteEntries = null;

        try
        {
            var dataTransfer = new DataTransfer();
            foreach (var it in items)
                dataTransfer.Add(DataTransferItem.CreateText(it.FullPath));

            await DragDrop.DoDragDropAsync(pressed, dataTransfer, DragDropEffects.Copy);
        }
        catch (Exception ex) { Console.WriteLine($">>> Errore drag locale: {ex.Message}"); }
        finally { _draggedLocalEntries = null; }
    }

    private void OnRemoteGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
        if (!DragGuard.IsOnRow(e.Source)) { _remotePointerDown = false; _remotePressedArgs = null; return; }
        _remoteDragStart = e.GetPosition(grid);
        _remotePointerDown = true;
        _remotePressedArgs = e;
    }

    private void OnRemoteGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _remotePointerDown = false;
        _remotePressedArgs = null;
    }

    private async void OnRemoteGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_remotePointerDown) return;
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed)
        {
            _remotePointerDown = false;
            return;
        }

        var pos = e.GetPosition(grid);
        if (Math.Abs(pos.X - _remoteDragStart.X) < 5 &&
            Math.Abs(pos.Y - _remoteDragStart.Y) < 5) return;

        _remotePointerDown = false;
        var pressed = _remotePressedArgs;
        _remotePressedArgs = null;
        if (pressed == null) return;

        var items = grid.SelectedItems.OfType<SftpRemoteEntry>().ToList();
        if (items.Count == 0) return;

        _draggedRemoteEntries = items;
        _draggedLocalEntries = null;

        try
        {
            var dataTransfer = new DataTransfer();
            foreach (var it in items)
                dataTransfer.Add(DataTransferItem.CreateText(it.FullPath));

            await DragDrop.DoDragDropAsync(pressed, dataTransfer, DragDropEffects.Copy);
        }
        catch (Exception ex) { Console.WriteLine($">>> Errore drag remoto: {ex.Message}"); }
        finally { _draggedRemoteEntries = null; }
    }

    // ============================================================
    //  DRAG & DROP — TARGET
    // ============================================================

    private void OnRemoteGridDragOver(object? sender, DragEventArgs e)
    {
        if (_draggedLocalEntries is { Count: > 0 })
        {
            e.DragEffects = DragDropEffects.Copy;
            return;
        }
        if (e.DataTransfer.Contains(DataFormat.File))
        {
            e.DragEffects = DragDropEffects.Copy;
            return;
        }
        e.DragEffects = DragDropEffects.None;
    }

    private async void OnRemoteGridDrop(object? sender, DragEventArgs e)
    {
        if (Vm == null) return;

        if (_draggedLocalEntries is { Count: > 0 })
        {
            var entries = _draggedLocalEntries;
            _draggedLocalEntries = null;

            Vm.Sftp.SelectedLocalEntries.Clear();
            foreach (var it in entries)
                Vm.Sftp.SelectedLocalEntries.Add(it);

            await Vm.Sftp.SftpUploadSelectedCommand.ExecuteAsync(null);
            return;
        }

        if (!e.DataTransfer.Contains(DataFormat.File)) return;

        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;

        var paths = new List<string>();
        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (!string.IsNullOrEmpty(p)) paths.Add(p);
        }

        if (paths.Count == 0) return;

        await Vm.Sftp.EnqueueUploadsFromPathsAsync(paths, Vm.Sftp.SftpCurrentRemotePath);
    }

    private void OnLocalGridDragOver(object? sender, DragEventArgs e)
    {
        if (_draggedRemoteEntries is { Count: > 0 })
        {
            e.DragEffects = DragDropEffects.Copy;
            return;
        }
        e.DragEffects = DragDropEffects.None;
    }

    private async void OnLocalGridDrop(object? sender, DragEventArgs e)
    {
        if (Vm == null) return;
        if (_draggedRemoteEntries is not { Count: > 0 }) return;

        var entries = _draggedRemoteEntries;
        _draggedRemoteEntries = null;

        Vm.Sftp.SelectedRemoteEntries.Clear();
        foreach (var it in entries)
            Vm.Sftp.SelectedRemoteEntries.Add(it);

        await Vm.Sftp.SftpDownloadSelectedCommand.ExecuteAsync(null);
    }

    // ============================================================
    //  TAB FILM/SERIE — RICERCA
    // ============================================================

    private void OnStreamingSearchButtonClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var combo = this.FindControl<ComboBox>("StreamingSearchComboBox");
        if (combo == null) return;

        var text = combo.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(text) && combo.SelectedItem is string selected) text = selected.Trim();
        if (string.IsNullOrEmpty(text)) { Vm.Streaming.StatusMessage = "Inserisci un termine."; return; }

        _ = Vm.Streaming.SearchWithTermAsync(text);
    }

    private void OnStreamingSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnStreamingSearchButtonClick(sender, new RoutedEventArgs()); e.Handled = true; }
    }

    private void OnStreamingClearSearchHistoryClick(object? sender, RoutedEventArgs e)
        => Vm?.Streaming.ClearSearchHistoryCommand.Execute(null);

    // ============================================================
    //  RISULTATI DI RICERCA — APRI / COPIA LINK DEL POST
    // ============================================================

    private static string? ResultLink(object? sender) => (sender as Control)?.DataContext switch
    {
        AnimeSearchResult a => a.Link,
        MovieSearchResult m => m.Link,
        _ => null
    };

    private void OnOpenResultInBrowserClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;   // il clic sull'icona non deve aprire anche la serie
        var link = ResultLink(sender);
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Console.WriteLine($">>> Apertura browser fallita: {ex.Message}"); }
    }

    private async void OnShowChangelogClick(object? sender, RoutedEventArgs e)
    {
        await ChangelogDialog.ShowAsync(this, Vm?.AppVersionText ?? "");
    }

    private void OnOpenSelectedResultInBrowserClick(object? sender, RoutedEventArgs e)
    {
        var link = Vm?.Streaming.SelectedResult?.Link;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Console.WriteLine($">>> Apertura browser fallita: {ex.Message}"); }
    }

    private async void OnCopyResultLinkClick(object? sender, RoutedEventArgs e)
    {
        var link = ResultLink(sender);
        if (!string.IsNullOrEmpty(link)) await ClipboardHelper.SetTextAsync(link);
    }

    // ============================================================
    //  TAB FILM/SERIE — DOPPIO CLICK RISULTATO
    // ============================================================

    private async void OnStreamingSearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        var selected = sender switch
        {
            DataGrid g => g.SelectedItem,
            ListBox l => l.SelectedItem,
            _ => null
        };
        if (selected is not MovieSearchResult result) return;
        if (Vm == null) return;

        await Vm.Streaming.LoadSeasonsCommand.ExecuteAsync(result);
    }

    // ============================================================
    //  TAB FILM/SERIE — SELEZIONE EPISODI
    // ============================================================

    private bool _syncingStreamingEpisodeSelection;

    private void OnStreamingEpisodesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm == null) return;
        if (_syncingStreamingEpisodeSelection) return;
        if (sender is not DataGrid grid) return;

        var selected = new HashSet<StreamingEpisode>();
        if (grid.SelectedItems != null)
            foreach (var item in grid.SelectedItems)
                if (item is StreamingEpisode ep) selected.Add(ep);

        foreach (var ep in Vm.Streaming.Episodes)
        {
            bool shouldBeSelected = selected.Contains(ep);
            if (ep.IsSelected != shouldBeSelected) ep.IsSelected = shouldBeSelected;
        }
    }

    private void OnStreamingSelectAllEpisodesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.Streaming.SelectAllEpisodesCommand.Execute(null);

        if (this.FindControl<DataGrid>("StreamingEpisodesGrid") is not DataGrid grid) return;
        _syncingStreamingEpisodeSelection = true;
        try { grid.SelectAll(); }
        finally { _syncingStreamingEpisodeSelection = false; }
    }

    private void OnStreamingDeselectAllEpisodesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.Streaming.DeselectAllEpisodesCommand.Execute(null);

        if (this.FindControl<DataGrid>("StreamingEpisodesGrid") is not DataGrid grid) return;
        _syncingStreamingEpisodeSelection = true;
        try { grid.SelectedItems?.Clear(); }
        finally { _syncingStreamingEpisodeSelection = false; }
    }

    private void OnStreamingInvertEpisodeSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        Vm.Streaming.InvertEpisodeSelectionCommand.Execute(null);

        if (this.FindControl<DataGrid>("StreamingEpisodesGrid") is not DataGrid grid) return;
        _syncingStreamingEpisodeSelection = true;
        try
        {
            grid.SelectedItems?.Clear();
            foreach (var ep in Vm.Streaming.Episodes)
                if (ep.IsSelected) grid.SelectedItems?.Add(ep);
        }
        finally { _syncingStreamingEpisodeSelection = false; }
    }

    // ============================================================
    //  TAB FILM/SERIE — BROWSER CARTELLA
    // ============================================================

    private async void OnStreamingBrowseBaseFolderClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella base download streaming");
        if (!string.IsNullOrEmpty(folder)) Vm?.Streaming.SetBaseDownloadFolder(folder);
    }

    // ============================================================
    //  FILM/SERIE — PULSANTI CODA
    // ============================================================

    private void OnStreamingResumeGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is SeriesGroup g)
            Vm?.Streaming.ResumeGroupPublic(g);
    }

    private void OnStreamingForceGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is SeriesGroup g)
            Vm?.Streaming.ForceGroupPublic(g);
    }

    private void OnStreamingUnforceGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is SeriesGroup g)
            Vm?.Streaming.UnforceGroupPublic(g);
    }

    private void OnStreamingCancelGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is SeriesGroup g)
            Vm?.Streaming.CancelGroupPublic(g);
    }

    private async void OnStreamingRemoveGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is SeriesGroup g && Vm != null)
            await Vm.Streaming.RemoveGroupPublicAsync(g);
    }

    private async void OnStreamingRetryFailedGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is SeriesGroup g && Vm != null)
            await Vm.Streaming.RetryFailedGroupPublicAsync(g);
    }

    private void OnStreamingDownloadItemResumeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DownloadItem it)
            Vm?.Streaming.ResumeItemPublic(it);
    }

    private void OnStreamingDownloadItemForceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DownloadItem it)
            Vm?.Streaming.ForceItemPublic(it);
    }

    private void OnStreamingDownloadItemCancelClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DownloadItem it)
            Vm?.Streaming.CancelItemPublic(it);
    }

    private async void OnStreamingDownloadItemRetryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DownloadItem it && Vm != null)
            await Vm.Streaming.RetryItemPublicAsync(it);
    }

    private async void OnStreamingDownloadItemRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.DataContext is DownloadItem it && Vm != null)
            await Vm.Streaming.RemoveItemPublicAsync(it);
    }

    // ============================================================
    //  MENU DEI FILE: voci secondo l'elemento cliccato
    // ============================================================

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".flv", ".wmv", ".m4v", ".ts", ".m2ts"
    };

    public static bool IsVideoFile(string name) => VideoExtensions.Contains(Path.GetExtension(name));

    /// <summary>
    /// Prima di aprire il menu di una tabella di file: "Apri" (Tag="dir") solo su una cartella,
    /// "Anteprima video" (Tag="video") solo su un file video; i separatori rimasti all'inizio,
    /// alla fine o doppi vengono nascosti.
    /// </summary>
    private void OnFileMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu) return;

        bool multiple = _contextGrid != null && _contextItem != null &&
                        _contextGrid.SelectedItems.Count > 1 && _contextGrid.SelectedItems.Contains(_contextItem);
        (bool isDir, string name)? item = _contextItem switch
        {
            LocalFileEntry l => (l.IsDirectory, l.Name),
            SftpRemoteEntry r => (r.IsDirectory, r.Name),
            _ => null
        };

        foreach (var mi in menu.Items.OfType<MenuItem>())
        {
            switch (mi.Tag as string)
            {
                case "dir": mi.IsVisible = !multiple && item is { isDir: true }; break;
                case "video": mi.IsVisible = !multiple && item is { isDir: false } it && IsVideoFile(it.name); break;
            }
        }

        // Separatori: nascosti se non hanno voci visibili prima e dopo
        var controls = menu.Items.OfType<Control>().ToList();
        bool seenItem = false;
        Separator? pending = null;
        foreach (var c in controls)
        {
            if (c is Separator sep)
            {
                sep.IsVisible = false;
                if (seenItem && pending == null) pending = sep;
            }
            else if (c.IsVisible)
            {
                if (pending != null) { pending.IsVisible = true; pending = null; }
                seenItem = true;
            }
        }
    }

    private void OnLocalPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveItem<LocalFileEntry>(sender) is { IsDirectory: false } entry) Vm?.Sftp.PlayLocalFile(entry);
    }

    private void OnServer2PreviewClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveItem<SftpRemoteEntry>(sender) is { IsDirectory: false } entry) Vm?.Sftp.PlayServer2File(entry);
    }

    private async void OnServer2RenameClick(object? sender, RoutedEventArgs e)
    {
        if (ResolveItem<SftpRemoteEntry>(sender) is not { } entry || Vm == null) return;
        var newName = await InputDialog.ShowAsync(this, $"Rinomina '{entry.Name}' in:", entry.Name, "Rinomina", selectNameOnly: !entry.IsDirectory);
        if (string.IsNullOrEmpty(newName) || newName == entry.Name) return;
        await Vm.Sftp.Server2RenameAsync(entry, newName);
    }

    private async void OnServer2MoveClick(object? sender, RoutedEventArgs e)
    {
        var entries = ResolveItems<SftpRemoteEntry>(sender);
        if (entries.Count == 0 || Vm == null || !Vm.Sftp.IsServer2Connected) return;

        var title = entries.Count == 1 ? $"Sposta '{entries[0].Name}' in..." : $"Sposta {entries.Count} elementi in...";
        var dest = await RemoteFolderPickerDialog.ShowAsync(this, Vm.Sftp.Server2Service, Vm.Sftp.Server2Path, title);
        if (string.IsNullOrEmpty(dest)) return;
        await Vm.Sftp.Server2MoveManyAsync(entries, dest);
    }

    private void OnRemotePreviewClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<SftpRemoteEntry>(sender);
        if (entry == null || Vm == null) return;
        if (entry.IsDirectory)
        {
            Vm.Sftp.SftpStatusMessage = "Le cartelle non si possono riprodurre.";
            return;
        }
        Vm.Sftp.PlayRemoteFile(entry);
    }
}