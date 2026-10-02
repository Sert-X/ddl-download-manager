using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    public MainWindow(MainWindowViewModel viewModel, ISftpService sftpService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _sftpService = sftpService;

        // DataGridRow gestisce internamente PointerPressed: registriamo con
        // handledEventsToo=true, altrimenti i nostri handler non partono.
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
    }

    public MainWindow()
    {
        InitializeComponent();
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    // ============================================================
    //  HELPER
    // ============================================================

    private static T? ResolveItem<T>(object? sender) where T : class
    {
        if (sender is not MenuItem mi) return null;
        if (mi.DataContext is T direct) return direct;

        if (mi.Parent is ContextMenu cm)
        {
            if (cm.DataContext is T cmItem) return cmItem;
            if (cm.PlacementTarget?.DataContext is T targetItem) return targetItem;
        }
        return null;
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

    private async Task<string?> PickRemoteFolderAsync(string title)
    {
        if (_sftpService == null) return null;
        if (!_sftpService.IsConnected) return null;

        var start = Vm?.SftpCurrentRemotePath ?? "/";
        return await RemoteFolderPickerDialog.ShowAsync(this, _sftpService, start);
    }

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
        if (string.IsNullOrEmpty(text)) { Vm.StatusMessage = "Inserisci un termine."; return; }

        _ = Vm.SearchWithTermAsync(text);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnSearchButtonClick(sender, new RoutedEventArgs()); e.Handled = true; }
    }

    private void OnClearSearchHistoryClick(object? sender, RoutedEventArgs e) => Vm?.ClearSearchHistoryCommand.Execute(null);

    // ============================================================
    //  SELEZIONE EPISODI
    // ============================================================

    private void OnEpisodesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm == null) return;
        if (sender is not DataGrid grid) return;

        var selected = new HashSet<Episode>();
        if (grid.SelectedItems != null)
            foreach (var item in grid.SelectedItems)
                if (item is Episode ep) selected.Add(ep);

        foreach (var ep in Vm.Episodes)
        {
            bool shouldBeSelected = selected.Contains(ep);
            if (ep.IsSelected != shouldBeSelected) ep.IsSelected = shouldBeSelected;
        }
    }

    private void OnSelectAllEpisodesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.SelectAllEpisodesCommand.Execute(null);
        if (this.FindControl<DataGrid>("EpisodesGrid") is DataGrid grid) grid.SelectAll();
    }

    private void OnDeselectAllEpisodesClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.DeselectAllEpisodesCommand.Execute(null);
        if (this.FindControl<DataGrid>("EpisodesGrid") is DataGrid grid) grid.SelectedItems?.Clear();
    }

    private void OnInvertEpisodeSelectionClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        Vm.InvertEpisodeSelectionCommand.Execute(null);

        if (this.FindControl<DataGrid>("EpisodesGrid") is not DataGrid grid) return;
        grid.SelectedItems?.Clear();
        foreach (var ep in Vm.Episodes)
            if (ep.IsSelected) grid.SelectedItems?.Add(ep);
    }

    // ============================================================
    //  FOLDER PICKER LOCALE
    // ============================================================

    private async void OnBrowseBaseFolderClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella base download");
        if (!string.IsNullOrEmpty(folder)) Vm?.SetBaseDownloadFolder(folder);
    }

    private async void OnBrowseOrganizeFolderClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella da organizzare");
        if (!string.IsNullOrEmpty(folder)) Vm?.SetOrganizeFolder(folder);
    }

    private async void OnBrowseMergeDestClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella di destinazione");
        if (!string.IsNullOrEmpty(folder)) Vm?.SetMergeDestinationFolder(folder);
    }

    private async void OnBrowseSplitSourceClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella da dividere");
        if (!string.IsNullOrEmpty(folder)) Vm?.SetSplitSourceFolder(folder);
    }

    private async void OnBrowseSplitDestClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella di destinazione");
        if (!string.IsNullOrEmpty(folder)) Vm?.SetSplitDestinationFolder(folder);
    }

    // ============================================================
    //  FOLDER PICKER REMOTO
    // ============================================================

    private async void OnBrowseOrganizeFolderRemoteClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickRemoteFolderAsync("Seleziona cartella remota da organizzare");
        if (!string.IsNullOrEmpty(folder) && Vm != null) Vm.OrganizeFolder = folder;
    }

    private async void OnBrowseMergeDestRemoteClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickRemoteFolderAsync("Seleziona destinazione remota per il merge");
        if (!string.IsNullOrEmpty(folder) && Vm != null) Vm.MergeDestinationFolder = folder;
    }

    private async void OnBrowseSplitSourceRemoteClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickRemoteFolderAsync("Seleziona cartella remota da dividere");
        if (!string.IsNullOrEmpty(folder) && Vm != null) Vm.SplitSourceFolder = folder;
    }

    private async void OnBrowseSplitDestRemoteClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickRemoteFolderAsync("Seleziona destinazione remota per lo split");
        if (!string.IsNullOrEmpty(folder) && Vm != null) Vm.SplitDestinationFolder = folder;
    }

    private async void OnBrowseMergeAddRemoteFolderClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickRemoteFolderAsync("Aggiungi cartella sorgente remota");
        if (!string.IsNullOrEmpty(folder)) Vm?.AddMergeFolder(folder);
    }

    // ============================================================
    //  MERGE
    // ============================================================

    private async void OnMergeAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        if (Vm.OrganizeModeIsLocal)
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
                if (!string.IsNullOrEmpty(path)) Vm.AddMergeFolder(path);
            }
        }
        else
        {
            await PickRemoteFolderAsync("Aggiungi cartella sorgente remota");
        }
    }

    private void OnMergeRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedMergeFolder == null) return;
        Vm.RemoveMergeFolder(Vm.SelectedMergeFolder);
    }

    private void OnMergeMoveUpClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedMergeFolder == null) return;
        Vm.MoveMergeFolderUp(Vm.SelectedMergeFolder);
    }

    private void OnMergeMoveDownClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.SelectedMergeFolder == null) return;
        Vm.MoveMergeFolderDown(Vm.SelectedMergeFolder);
    }

    private void OnMergeClearClick(object? sender, RoutedEventArgs e) => Vm?.ClearMergeFolders();

    // ============================================================
    //  SPLIT
    // ============================================================

    private void OnSplitRemoveSegmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        if (btn.DataContext is not SplitSegment segment) return;
        Vm?.RemoveSplitSegmentCommand.Execute(segment);
    }

    // ============================================================
    //  SFTP CONFIG
    // ============================================================

    private void OnSftpNewProfileClick(object? sender, RoutedEventArgs e) => Vm?.SftpNewProfileCommand.Execute(null);

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
        if (!string.IsNullOrEmpty(path) && Vm != null) Vm.SftpPrivateKeyPath = path;
    }

    private void OnSftpRefreshClick(object? sender, RoutedEventArgs e) => Vm?.SftpRefreshCommand.Execute(null);

    private async void OnSftpEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not SftpRemoteEntry entry) return;
        if (Vm == null) return;

        if (entry.IsDirectory)
        {
            Vm.SftpCurrentRemotePath = entry.FullPath;
            await Vm.SftpRefreshCommand.ExecuteAsync(null);
        }
    }

    // ============================================================
    //  SFTP BROWSER LOCALE
    // ============================================================

    private void OnSftpLocalRefreshClick(object? sender, RoutedEventArgs e) => Vm?.SftpRefreshLocalCommand.Execute(null);

    private async void OnSftpLocalEntryDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not LocalFileEntry entry) return;
        if (Vm == null) return;

        if (entry.IsDirectory)
        {
            Vm.SftpCurrentLocalPath = entry.FullPath;
            await Vm.SftpRefreshLocalCommand.ExecuteAsync(null);
        }
    }

    private async void OnBrowseSftpUploadLocalClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella locale da caricare");
        if (!string.IsNullOrEmpty(folder)) Vm?.SetSftpUploadLocalFolder(folder);
    }

    private async void OnBrowseSftpDownloadLocalClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Seleziona cartella locale di destinazione");
        if (!string.IsNullOrEmpty(folder)) Vm?.SetSftpDownloadLocalFolder(folder);
    }

    // ============================================================
    //  SFTP SELEZIONE MULTIPLA
    // ============================================================

    private void OnLocalFilesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm == null) return;
        if (sender is not DataGrid grid) return;

        Vm.SelectedLocalEntries.Clear();
        if (grid.SelectedItems != null)
            foreach (var item in grid.SelectedItems)
                if (item is LocalFileEntry entry) Vm.SelectedLocalEntries.Add(entry);
    }

    private void OnRemoteFilesSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm == null) return;
        if (sender is not DataGrid grid) return;

        Vm.SelectedRemoteEntries.Clear();
        if (grid.SelectedItems != null)
            foreach (var item in grid.SelectedItems)
                if (item is SftpRemoteEntry entry) Vm.SelectedRemoteEntries.Add(entry);
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
        if (Vm.SelectedLocalEntries.Count > 0) entries.AddRange(Vm.SelectedLocalEntries);
        else if (Vm.SftpSelectedLocalEntry != null) entries.Add(Vm.SftpSelectedLocalEntry);

        if (entries.Count == 0) return;

        var msg = entries.Count == 1
            ? $"Eliminare '{entries[0].Name}'?"
            : $"Eliminare {entries.Count} elementi?";
        if (entries.Any(x => x.IsDirectory))
            msg += "\n\nLe cartelle verranno eliminate con tutto il contenuto.";
        msg += "\n\nQuesta azione non può essere annullata.";

        var ok = await ConfirmDialog.ShowAsync(this, msg, "Conferma eliminazione");
        if (sender is DataGrid g) g.Focus();
        if (!ok) return;

        await Vm.SftpLocalDeleteManyAsync(entries);
    }

    private async void OnRemoteGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        if (Vm == null) return;

        var entries = new List<SftpRemoteEntry>();
        if (Vm.SelectedRemoteEntries.Count > 0) entries.AddRange(Vm.SelectedRemoteEntries);
        else if (Vm.SftpSelectedRemoteEntry != null) entries.Add(Vm.SftpSelectedRemoteEntry);

        if (entries.Count == 0) return;

        var msg = entries.Count == 1
            ? $"Eliminare '{entries[0].Name}' dal server?"
            : $"Eliminare {entries.Count} elementi dal server?";
        if (entries.Any(x => x.IsDirectory))
            msg += "\n\nLe cartelle verranno eliminate con tutto il contenuto.";
        msg += "\n\nQuesta azione non può essere annullata.";

        var ok = await ConfirmDialog.ShowAsync(this, msg, "Conferma eliminazione");
        if (sender is DataGrid g) g.Focus();
        if (!ok) return;

        await Vm.SftpRemoteDeleteManyAsync(entries);
    }

    // ============================================================
    //  SFTP CRUD LOCALE
    // ============================================================

    private async void OnLocalNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var name = await InputDialog.ShowAsync(this, "Nome nuova cartella:", "Nuova cartella", "Nuova cartella");
        if (!string.IsNullOrEmpty(name)) await Vm.SftpLocalCreateFolderAsync(name);
    }

    private async void OnLocalOpenClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<LocalFileEntry>(sender);
        if (entry == null || Vm == null) return;
        if (!entry.IsDirectory) return;

        Vm.SftpCurrentLocalPath = entry.FullPath;
        await Vm.SftpRefreshLocalCommand.ExecuteAsync(null);
    }

    private async void OnLocalRenameClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<LocalFileEntry>(sender);
        if (entry == null || Vm == null) return;

        var newName = await InputDialog.ShowAsync(this, $"Rinomina '{entry.Name}' in:", entry.Name, "Rinomina");
        if (string.IsNullOrEmpty(newName) || newName == entry.Name) return;

        await Vm.SftpLocalRenameAsync(entry, newName);
    }

    private async void OnLocalDeleteClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<LocalFileEntry>(sender);
        if (entry == null || Vm == null) return;

        var tipo = entry.IsDirectory ? "cartella" : "file";
        var extra = entry.IsDirectory ? " e tutto il suo contenuto" : "";
        var msg = $"Eliminare la {tipo} '{entry.Name}'{extra}?\n\nQuesta azione non può essere annullata.";

        var ok = await ConfirmDialog.ShowAsync(this, msg, "Conferma eliminazione");
        if (!ok) return;

        await Vm.SftpLocalDeleteManyAsync(new[] { entry });
    }

    // ============================================================
    //  SFTP CRUD REMOTO
    // ============================================================

    private async void OnRemoteNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;
        var name = await InputDialog.ShowAsync(this, "Nome nuova cartella remota:", "Nuova cartella", "Nuova cartella");
        if (!string.IsNullOrEmpty(name)) await Vm.SftpRemoteCreateFolderAsync(name);
    }

    private async void OnRemoteOpenClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<SftpRemoteEntry>(sender);
        if (entry == null || Vm == null) return;
        if (!entry.IsDirectory) return;

        Vm.SftpCurrentRemotePath = entry.FullPath;
        await Vm.SftpRefreshCommand.ExecuteAsync(null);
    }

    private async void OnRemoteRenameClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<SftpRemoteEntry>(sender);
        if (entry == null || Vm == null) return;

        var newName = await InputDialog.ShowAsync(this, $"Rinomina '{entry.Name}' in:", entry.Name, "Rinomina");
        if (string.IsNullOrEmpty(newName) || newName == entry.Name) return;

        await Vm.SftpRemoteRenameAsync(entry, newName);
    }

    private async void OnRemoteDeleteClick(object? sender, RoutedEventArgs e)
    {
        var entry = ResolveItem<SftpRemoteEntry>(sender);
        if (entry == null || Vm == null) return;

        var tipo = entry.IsDirectory ? "cartella" : "file";
        var extra = entry.IsDirectory ? " e tutto il suo contenuto sul server" : "";
        var msg = $"Eliminare la {tipo} remota '{entry.Name}'{extra}?\n\nQuesta azione non può essere annullata.";

        var ok = await ConfirmDialog.ShowAsync(this, msg, "Conferma eliminazione");
        if (!ok) return;

        await Vm.SftpRemoteDeleteManyAsync(new[] { entry });
    }

    private async void OnProxyImportFileClick(object? sender, RoutedEventArgs e)
    {
        if (Vm == null) return;

        var storage = StorageProvider;
        if (storage == null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importa lista proxy",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("File proxy")
                {
                    Patterns = new[] { "*.txt", "*.csv" }
                },
                new FilePickerFileType("Tutti i file")
                {
                    Patterns = new[] { "*" }
                }
            }
        });

        if (files == null || files.Count == 0) return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try
        {
            var content = await File.ReadAllTextAsync(path);
            Vm.ProxyImportFromString(content);
        }
        catch (Exception ex)
        {
            Vm.ProxyStatusMessage = $"Errore lettura file: {ex.Message}";
        }
    }

    private bool _syncingProxySelection;

    private void OnProxyListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm == null || _syncingProxySelection) return;
        if (sender is not ListBox lb) return;

        // Sincronizza: gli item aggiunti alla selezione ListBox → IsSelected = true
        // quelli rimossi → IsSelected = false
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

    private void OnSftpJobResumeClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpUploadJob>(sender); if (j != null) Vm?.ResumeSftpJobPublic(j); }
    private void OnSftpJobPauseClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpUploadJob>(sender); if (j != null) Vm?.PauseSftpJobPublic(j); }
    private void OnSftpJobCancelClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpUploadJob>(sender); if (j != null) Vm?.CancelSftpJobPublic(j); }
    private void OnSftpJobRemoveClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpUploadJob>(sender); if (j != null) Vm?.RemoveSftpJobPublic(j); }

    private void OnSftpDownloadJobResumeClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpDownloadJob>(sender); if (j != null) Vm?.ResumeSftpDownloadJobPublic(j); }
    private void OnSftpDownloadJobPauseClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpDownloadJob>(sender); if (j != null) Vm?.PauseSftpDownloadJobPublic(j); }
    private void OnSftpDownloadJobCancelClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpDownloadJob>(sender); if (j != null) Vm?.CancelSftpDownloadJobPublic(j); }
    private void OnSftpDownloadJobRemoveClick(object? sender, RoutedEventArgs e) { var j = ResolveItem<SftpDownloadJob>(sender); if (j != null) Vm?.RemoveSftpDownloadJobPublic(j); }

    // ============================================================
    //  DOPPIO CLICK DOWNLOAD TAB
    // ============================================================

    private async void OnSearchResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not AnimeSearchResult anime) return;
        if (Vm == null) return;
        await Vm.LoadEpisodesCommand.ExecuteAsync(anime);
    }

    private async void OnEpisodeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (grid.SelectedItem is not Episode episode) return;
        if (Vm == null) return;
        await Vm.DownloadEpisodeCommand.ExecuteAsync(episode);
    }

    // ============================================================
    //  CONTEXT MENU DOWNLOAD
    // ============================================================

    private void OnResumeDownloadClick(object? sender, RoutedEventArgs e) { var i = ResolveItem<DownloadItem>(sender); if (i != null) Vm?.ResumeItemPublic(i); }
    private void OnForceDownloadClick(object? sender, RoutedEventArgs e) { var i = ResolveItem<DownloadItem>(sender); if (i != null) Vm?.ForceItemPublic(i); }
    private void OnUnforceDownloadClick(object? sender, RoutedEventArgs e) { var i = ResolveItem<DownloadItem>(sender); if (i != null) Vm?.UnforceItemPublic(i); }
    private void OnPauseDownloadClick(object? sender, RoutedEventArgs e) { var i = ResolveItem<DownloadItem>(sender); if (i != null) Vm?.PauseItemPublic(i); }
    private void OnCancelDownloadClick(object? sender, RoutedEventArgs e) { var i = ResolveItem<DownloadItem>(sender); if (i != null) Vm?.CancelItemPublic(i); }

    private async void OnRemoveDownloadClick(object? sender, RoutedEventArgs e)
    {
        var i = ResolveItem<DownloadItem>(sender);
        if (i != null && Vm != null) await Vm.RemoveItemPublicAsync(i);
    }

    // ============================================================
    //  PULSANTI GRUPPO
    // ============================================================

    private void OnResumeGroupClick(object? sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is SeriesGroup g) Vm?.ResumeGroupPublic(g); }
    private void OnForceGroupClick(object? sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is SeriesGroup g) Vm?.ForceGroupPublic(g); }
    private void OnUnforceGroupClick(object? sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is SeriesGroup g) Vm?.UnforceGroupPublic(g); }
    private void OnPauseGroupClick(object? sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is SeriesGroup g) Vm?.PauseGroupPublic(g); }
    private void OnCancelGroupClick(object? sender, RoutedEventArgs e) { if (sender is Button b && b.DataContext is SeriesGroup g) Vm?.CancelGroupPublic(g); }

    private async void OnRemoveGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        if (b.DataContext is not SeriesGroup g) return;
        if (Vm == null) return;
        await Vm.RemoveGroupPublicAsync(g);
    }

    // ============================================================
    //  DRAG & DROP — RILEVAMENTO (PointerMoved, non PointerPressed)
    // ============================================================
    //
    // Il drag parte da PointerMoved con soglia 5px, così la DataGrid ha già
    // applicato la selezione multipla (Ctrl/Shift) prima che leggiamo SelectedItems.

    private void OnLocalGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
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

        System.Diagnostics.Debug.WriteLine($"[DRAG-START-LOCAL] {items.Count} elemento/i");

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

        System.Diagnostics.Debug.WriteLine($"[DRAG-START-REMOTE] {items.Count} elemento/i");

        _draggedRemoteEntries = items;
        _draggedLocalEntries = null;

        try
        {
            // Solo testo: il drag interno (Remote → Local) usa i path remoti.
            // Il drag outbound verso Explorer è rimosso perché troppo costoso
            // (avrebbe dovuto pre-scaricare tutto in temp prima di poter partire).
            var dataTransfer = new DataTransfer();
            foreach (var it in items)
                dataTransfer.Add(DataTransferItem.CreateText(it.FullPath));

            await DragDrop.DoDragDropAsync(pressed, dataTransfer, DragDropEffects.Copy);
        }
        catch (Exception ex) { Console.WriteLine($">>> Errore drag remoto: {ex.Message}"); }
        finally { _draggedRemoteEntries = null; }
    }

    // ============================================================
    //  DRAG & DROP — TARGET (DragOver / Drop)
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

        // Caso A: drag interno LocalGrid → RemoteGrid
        if (_draggedLocalEntries is { Count: > 0 })
        {
            var entries = _draggedLocalEntries;
            _draggedLocalEntries = null;

            Vm.SelectedLocalEntries.Clear();
            foreach (var it in entries)
                Vm.SelectedLocalEntries.Add(it);

            await Vm.SftpUploadSelectedCommand.ExecuteAsync(null);
            return;
        }

        // Caso B: drag dal Sistema Operativo (Esplora File)
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

        System.Diagnostics.Debug.WriteLine($"[DRAG-OS-DROP] {paths.Count} elemento/i");
        await Vm.EnqueueUploadsFromPathsAsync(paths, Vm.SftpCurrentRemotePath);
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

        Vm.SelectedRemoteEntries.Clear();
        foreach (var it in entries)
            Vm.SelectedRemoteEntries.Add(it);

        await Vm.SftpDownloadSelectedCommand.ExecuteAsync(null);
    }
}