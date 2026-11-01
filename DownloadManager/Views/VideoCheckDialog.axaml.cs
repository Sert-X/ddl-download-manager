using Avalonia.Controls;
using Avalonia.Interactivity;
using DownloadManager.Persistence;

namespace DownloadManager.Views;

/// <summary>Prima della verifica dei video: quali controlli fare (codec web, lingua, durata).</summary>
public partial class VideoCheckDialog : Window
{
    /// <summary>Controlli scelti.</summary>
    public sealed record Selection(bool Web, bool Language, bool Duration);

    public Selection? Result { get; private set; }

    public VideoCheckDialog()
    {
        InitializeComponent();
        WebCheck.IsCheckedChanged += (_, _) => UpdateOk();
        LangCheck.IsCheckedChanged += (_, _) => UpdateOk();
        DurationCheck.IsCheckedChanged += (_, _) => UpdateOk();
    }

    private void UpdateOk() =>
        OkButton.IsEnabled = WebCheck.IsChecked == true || LangCheck.IsChecked == true || DurationCheck.IsChecked == true;

    /// <param name="recheckAll">Ricontrolla anche i video già controllati (cambia solo il testo).</param>
    /// <param name="prompt">Testo su cosa si controlla (per una selezione).</param>
    /// <param name="minDurationMinutes">Soglia attuale dei video corti (per il testo della voce Durata).</param>
    /// <returns>I controlli scelti, null se annullato.</returns>
    public static async Task<Selection?> ShowAsync(Window owner, bool recheckAll = false, string? prompt = null, double minDurationMinutes = 0)
    {
        var dialog = new VideoCheckDialog();
        if (prompt != null)
            dialog.PromptText.Text = prompt;
        else if (recheckAll)
            dialog.PromptText.Text = "Tutti i video della cartella aperta e delle sottocartelle, anche quelli già controllati.";
        if (minDurationMinutes > 0)
            dialog.DurationHint.Text = $"Durata di ogni video; segnala i più corti di {Services.Sftp.WebStreamCheckerService.FormatMinutes(minDurationMinutes * 60)} (colonna Durata)";

        // Ultima scelta
        var settings = App.Services?.GetService(typeof(SettingsService)) as SettingsService;
        if (settings != null)
        {
            dialog.WebCheck.IsChecked = settings.Current.VideoCheckWeb;
            dialog.LangCheck.IsChecked = settings.Current.VideoCheckLanguage;
            dialog.DurationCheck.IsChecked = settings.Current.VideoCheckDuration;
        }
        dialog.UpdateOk();
        dialog.Opened += (_, _) => dialog.OkButton.Focus();
        await dialog.ShowDialog(owner);

        if (dialog.Result != null && settings != null)
        {
            settings.Current.VideoCheckWeb = dialog.Result.Web;
            settings.Current.VideoCheckLanguage = dialog.Result.Language;
            settings.Current.VideoCheckDuration = dialog.Result.Duration;
            try { settings.Save(); } catch { }
        }
        return dialog.Result;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Result = new Selection(WebCheck.IsChecked == true, LangCheck.IsChecked == true, DurationCheck.IsChecked == true);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) { Result = null; Close(); }
}
