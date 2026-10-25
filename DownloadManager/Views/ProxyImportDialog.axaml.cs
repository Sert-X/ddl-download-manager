using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace DownloadManager.Views;

/// <summary>Importazione proxy: testo incollato o da file, verso tutte le liste o solo alcune.</summary>
public partial class ProxyImportDialog : Window
{
    public record ImportRequest(string Content, IReadOnlyCollection<string> Providers);

    private ImportRequest? _result;
    private readonly List<(string Key, CheckBox Box)> _providerBoxes = new();

    public ProxyImportDialog()
    {
        InitializeComponent();
    }

    /// <param name="preselected">Lista della scheda aperta: preselezionata come unica destinazione (null = tutte).</param>
    public static async Task<ImportRequest?> ShowAsync(Window owner, IReadOnlyList<(string Key, string Title)> lists, string? preselected)
    {
        var dlg = new ProxyImportDialog();
        foreach (var (key, title) in lists)
        {
            var box = new CheckBox { Content = title, IsChecked = preselected == null || preselected == key };
            dlg._providerBoxes.Add((key, box));
            dlg.ProvidersPanel.Children.Add(box);
        }
        if (preselected != null) dlg.SomeListsRadio.IsChecked = true;

        dlg.Opened += (_, _) => dlg.ContentBox.Focus();
        await dlg.ShowDialog(owner);
        return dlg._result;
    }

    private void OnDestinationChanged(object? sender, RoutedEventArgs e)
        => ProvidersPanel.IsEnabled = SomeListsRadio.IsChecked == true;

    private void OnContentChanged(object? sender, TextChangedEventArgs e)
    {
        var lines = (ContentBox.Text ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(l => l.Length > 0 && !l.StartsWith('#'));
        LinesText.Text = lines == 0 ? "Nessuna riga" : lines == 1 ? "1 riga" : $"{lines} righe";
    }

    private async void OnFromFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Importa lista proxy",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("File proxy") { Patterns = new[] { "*.txt", "*.csv" } },
                new FilePickerFileType("Tutti i file") { Patterns = new[] { "*" } }
            }
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        try { ContentBox.Text = await File.ReadAllTextAsync(path); }
        catch (Exception ex) { ShowError($"Impossibile leggere il file: {ex.Message}"); }
    }

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        var content = ContentBox.Text ?? "";
        if (string.IsNullOrWhiteSpace(content)) { ShowError("Incolla almeno un proxy o carica un file."); return; }

        var providers = AllListsRadio.IsChecked == true
            ? _providerBoxes.Select(b => b.Key).ToList()
            : _providerBoxes.Where(b => b.Box.IsChecked == true).Select(b => b.Key).ToList();
        if (providers.Count == 0) { ShowError("Scegli almeno una lista."); return; }

        _result = new ImportRequest(content, providers);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
