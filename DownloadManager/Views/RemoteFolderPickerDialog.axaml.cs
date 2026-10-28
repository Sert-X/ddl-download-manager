using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DownloadManager.Helpers;
using DownloadManager.Models;
using DownloadManager.Services.Sftp;

namespace DownloadManager.Views;

public partial class RemoteFolderPickerDialog : Window
{
    private ISftpService? _sftp;
    /// <summary>Cartelle del PC invece che del server (stessa finestra, stessa navigazione).</summary>
    private bool _local;
    /// <summary>In locale, "" = elenco dei dischi.</summary>
    private const string DrivesView = "";
    private readonly ObservableCollection<SftpRemoteEntry> _folders = new();
    private string _currentPath = "/";

    public string? SelectedPath { get; private set; }

    public RemoteFolderPickerDialog()
    {
        InitializeComponent();
        FoldersList.ItemsSource = _folders;
    }

    public RemoteFolderPickerDialog(ISftpService sftp, string startPath) : this()
    {
        _sftp = sftp;
        _currentPath = string.IsNullOrWhiteSpace(startPath) ? "/" : startPath;
        Opened += async (_, _) => await RefreshAsync();
    }

    /// <summary>Scelta di una cartella del PC con la finestra dell'app (non quella di Windows).</summary>
    public static async Task<string?> ShowLocalAsync(Window owner, string? startPath, string? title = null)
    {
        var dialog = new RemoteFolderPickerDialog { _local = true };
        dialog._currentPath = !string.IsNullOrWhiteSpace(startPath) && Directory.Exists(startPath)
            ? startPath!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dialog.Title = title ?? "Seleziona cartella";
        dialog.HeaderText.Text = title ?? "Seleziona cartella";
        dialog.HeaderIcon.Data = dialog.FindResource("IconFolder") as Avalonia.Media.Geometry ?? dialog.HeaderIcon.Data;
        ToolTip.SetTip(dialog.HomeButton, "Cartella dell'utente");
        dialog.Opened += async (_, _) => await dialog.RefreshAsync();
        await dialog.ShowDialog(owner);
        return dialog.SelectedPath;
    }

    public static async Task<string?> ShowAsync(Window owner, ISftpService sftp, string startPath, string? title = null)
    {
        var dialog = new RemoteFolderPickerDialog(sftp, startPath);
        if (!string.IsNullOrWhiteSpace(title))
        {
            dialog.Title = title;
            dialog.HeaderText.Text = title;
        }
        await dialog.ShowDialog(owner);
        return dialog.SelectedPath;
    }

    private async Task RefreshAsync()
    {
        if (_local) { RefreshLocal(); return; }
        if (_sftp == null || !_sftp.IsConnected) return;

        PathBox.Text = _currentPath;
        ShowStatus("Caricamento...");
        try
        {
            var entries = await _sftp.ListDirectoryAsync(_currentPath);

            _folders.Clear();
            // Ordine naturale: 1, 2, 3 ... 10, 11 (prima era 1, 10, 11, 2, 3)
            foreach (var e in entries.Where(x => x.IsDirectory && x.Name is not ("." or ".."))
                                     .OrderBy(x => x.Name, new NaturalSortComparer()))
                _folders.Add(e);

            ShowStatus(_folders.Count == 0 ? "Nessuna sottocartella.\nPremi Seleziona per usare questa cartella." : null);
        }
        catch (Exception ex)
        {
            _folders.Clear();
            ShowStatus($"Impossibile leggere la cartella:\n{ex.Message}");
        }
    }

    private void RefreshLocal()
    {
        PathBox.Text = _currentPath == DrivesView ? "Questo PC" : _currentPath;
        _folders.Clear();
        try
        {
            if (_currentPath == DrivesView)
            {
                foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
                    _folders.Add(new SftpRemoteEntry { Name = string.IsNullOrEmpty(d.VolumeLabel) ? d.Name : $"{d.Name}  ({d.VolumeLabel})", FullPath = d.RootDirectory.FullName, IsDirectory = true });
            }
            else
            {
                foreach (var dir in new DirectoryInfo(_currentPath).EnumerateDirectories()
                             .Where(d => (d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                             .OrderBy(d => d.Name, new NaturalSortComparer()))
                    _folders.Add(new SftpRemoteEntry { Name = dir.Name, FullPath = dir.FullName, IsDirectory = true });
            }
            ShowStatus(_folders.Count == 0 ? "Nessuna sottocartella.\nPremi Seleziona per usare questa cartella." : null);
        }
        catch (Exception ex)
        {
            ShowStatus($"Impossibile leggere la cartella:\n{ex.Message}");
        }
    }

    private void ShowStatus(string? message)
    {
        StatusText.Text = message;
        StatusText.IsVisible = message != null;
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnHomeClick(object? sender, RoutedEventArgs e)
    {
        if (_local)
        {
            _currentPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            _ = RefreshAsync();
            return;
        }
        if (_sftp == null) return;
        _currentPath = _sftp.GetWorkingDirectory();
        _ = RefreshAsync();
    }

    private void OnGoUpClick(object? sender, RoutedEventArgs e)
    {
        if (_local)
        {
            if (_currentPath == DrivesView) return;
            // Dalla radice di un disco si sale all'elenco dei dischi
            var root = Path.GetPathRoot(_currentPath) ?? "";
            _currentPath = string.Equals(_currentPath.TrimEnd('\\', '/'), root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                ? DrivesView
                : Directory.GetParent(_currentPath.TrimEnd('\\', '/'))?.FullName ?? DrivesView;
            _ = RefreshAsync();
            return;
        }
        var path = _currentPath.TrimEnd('/');
        if (string.IsNullOrEmpty(path)) { _currentPath = "/"; _ = RefreshAsync(); return; }

        int lastSlash = path.LastIndexOf('/');
        _currentPath = lastSlash <= 0 ? "/" : path.Substring(0, lastSlash);
        _ = RefreshAsync();
    }

    private void OnFolderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FoldersList.SelectedItem is not SftpRemoteEntry entry) return;
        _currentPath = entry.FullPath;
        _ = RefreshAsync();
    }

    private void OnUseCurrentClick(object? sender, RoutedEventArgs e)
    {
        if (_local && _currentPath == DrivesView) return;
        SelectedPath = _currentPath;
        Close();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedItem is SftpRemoteEntry entry)
            SelectedPath = entry.FullPath;
        else if (_local && _currentPath == DrivesView)
            return;
        else
            SelectedPath = _currentPath;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        SelectedPath = null;
        Close();
    }
}