using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DownloadManager.Models;
using DownloadManager.Persistence;

namespace DownloadManager.Views;

/// <summary>Cronologia dei download completati, con ricerca e filtro per tipo.</summary>
public partial class HistoryWindow : Window
{
    private readonly DownloadRepository? _repo;
    private int _loadSeq;
    private DispatcherTimer? _debounce;

    public HistoryWindow() { InitializeComponent(); }

    /// <param name="category">"anime", "streaming" o null (tutti): filtro iniziale.</param>
    public HistoryWindow(DownloadRepository repo, string? category) : this()
    {
        _repo = repo;
        CategoryBox.SelectedIndex = category switch { "anime" => 1, "streaming" => 2, _ => 0 };
        Opened += async (_, _) => { await LoadAsync(); SearchBox.Focus(); };
    }

    private string? Category => CategoryBox.SelectedIndex switch { 1 => "anime", 2 => "streaming", _ => null };

    private async Task LoadAsync()
    {
        if (_repo == null) return;
        int seq = ++_loadSeq;
        var search = SearchBox.Text;
        var category = Category;
        List<DownloadHistoryEntry> rows;
        try { rows = await Task.Run(() => _repo.GetHistoryAsync(category, search)); }
        catch (Exception ex) { CountText.Text = "Errore: " + ex.Message; return; }
        if (seq != _loadSeq) return;

        Grid.ItemsSource = rows;
        EmptyText.IsVisible = rows.Count == 0;
        EmptyText.Text = string.IsNullOrWhiteSpace(search) ? "Nessun download nella cronologia." : "Nessun risultato per questa ricerca.";
        long total = rows.Sum(r => r.SizeBytes);
        CountText.Text = rows.Count == 0 ? "" :
            $"{rows.Count} {(rows.Count == 1 ? "file" : "file")} · {(total >= 1L << 30 ? $"{total / (double)(1L << 30):F1} GB" : $"{total / (double)(1L << 20):F0} MB")}" +
            (rows.Count >= 2000 ? " (ultimi 2000)" : "");
    }

    private void OnSearchChanged(object? sender, TextChangedEventArgs e) => ScheduleReload();
    private void OnCategoryChanged(object? sender, SelectionChangedEventArgs e) => ScheduleReload();

    private void ScheduleReload()
    {
        if (_repo == null) return;
        // Si aspetta che finisca di scrivere
        _debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, async (_, _) =>
        {
            _debounce!.Stop();
            await LoadAsync();
        });
        _debounce.Stop();
        _debounce.Start();
    }

    private DownloadHistoryEntry? Selected => Grid.SelectedItem as DownloadHistoryEntry;

    private static void OpenFolderOf(DownloadHistoryEntry entry)
    {
        try
        {
            if (File.Exists(entry.FilePath))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.FilePath}\"") { UseShellExecute = true });
            else if (Directory.Exists(entry.Folder))
                Process.Start(new ProcessStartInfo(entry.Folder) { UseShellExecute = true });
        }
        catch { }
    }

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e) { if (Selected is { } s) OpenFolderOf(s); }
    private void OnRowDoubleTapped(object? sender, TappedEventArgs e) { if (Selected is { } s) OpenFolderOf(s); }

    private async void OnCopyPathClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } s) await DownloadManager.Helpers.ClipboardHelper.SetTextAsync(s.FilePath);
    }

    private async void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (_repo == null) return;
        var ok = await ConfirmDialog.ShowAsync(this,
            "Cancellare tutta la cronologia dei download? I file scaricati non vengono toccati, ma l'app non potrà più avvisarti se riscarichi qualcosa che hai già.",
            "Svuota cronologia", safeDefault: true);
        if (!ok) return;
        await Task.Run(() => _repo.ClearHistoryAsync());
        await LoadAsync();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
