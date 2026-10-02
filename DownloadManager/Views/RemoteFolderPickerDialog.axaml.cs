using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DownloadManager.Models;
using DownloadManager.Services.Sftp;

namespace DownloadManager.Views;

public partial class RemoteFolderPickerDialog : Window
{
    private ISftpService? _sftp;
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

    public static async Task<string?> ShowAsync(Window owner, ISftpService sftp, string startPath)
    {
        var dialog = new RemoteFolderPickerDialog(sftp, startPath);
        await dialog.ShowDialog(owner);
        return dialog.SelectedPath;
    }

    private async Task RefreshAsync()
    {
        if (_sftp == null || !_sftp.IsConnected) return;

        try
        {
            PathBox.Text = _currentPath;

            var entries = await _sftp.ListDirectoryAsync(_currentPath);

            _folders.Clear();
            foreach (var e in entries.Where(x => x.IsDirectory))
                _folders.Add(e);
        }
        catch (Exception ex)
        {
            PathBox.Text = $"{_currentPath}  —  Errore: {ex.Message}";
        }
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => _ = RefreshAsync();

    private void OnHomeClick(object? sender, RoutedEventArgs e)
    {
        if (_sftp == null) return;
        _currentPath = _sftp.GetWorkingDirectory();
        _ = RefreshAsync();
    }

    private void OnGoUpClick(object? sender, RoutedEventArgs e)
    {
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
        SelectedPath = _currentPath;
        Close();
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedItem is SftpRemoteEntry entry)
            SelectedPath = entry.FullPath;
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