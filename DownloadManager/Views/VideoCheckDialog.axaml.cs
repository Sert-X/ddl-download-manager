using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DownloadManager.Views;

/// <summary>Prima della verifica dei video: solo codec, solo lingua dell'audio, oppure entrambi.</summary>
public partial class VideoCheckDialog : Window
{
    public enum Choice { Cancel, CodecOnly, LanguageOnly, CodecAndLanguage }

    public Choice Result { get; private set; } = Choice.Cancel;

    public VideoCheckDialog()
    {
        InitializeComponent();
    }

    /// <param name="recheckAll">Ricontrolla anche i video già controllati (cambia solo il testo).</param>
    /// <param name="prompt">Testo su cosa si controlla (per una selezione).</param>
    public static async Task<Choice> ShowAsync(Window owner, bool recheckAll = false, string? prompt = null)
    {
        var dialog = new VideoCheckDialog();
        if (prompt != null)
            dialog.PromptText.Text = prompt;
        else if (recheckAll)
            dialog.PromptText.Text = "Tutti i video della cartella aperta e delle sottocartelle, anche quelli già controllati.";
        dialog.Opened += (_, _) => dialog.CodecOnlyButton.Focus();
        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    private void OnCodecOnlyClick(object? sender, RoutedEventArgs e) { Result = Choice.CodecOnly; Close(); }
    private void OnLanguageOnlyClick(object? sender, RoutedEventArgs e) { Result = Choice.LanguageOnly; Close(); }
    private void OnBothClick(object? sender, RoutedEventArgs e) { Result = Choice.CodecAndLanguage; Close(); }
    private void OnCancelClick(object? sender, RoutedEventArgs e) { Result = Choice.Cancel; Close(); }
}
