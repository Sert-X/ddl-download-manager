using Avalonia.Controls;
using Avalonia.Interactivity;
using DownloadManager.Persistence;

namespace DownloadManager.Views;

/// <summary>
/// Finestra unica dei controlli della tab SFTP: cartelle (file vuoti, contenuto) e video
/// (codec web, lingua, durata). Senza sottocartelle le voci delle cartelle sono spente.
/// </summary>
public partial class CheckDialog : Window
{
    /// <summary>Controlli scelti.</summary>
    public sealed record Selection(bool Zero, bool Contents, bool Web, bool Language, bool Duration)
    {
        public bool AnyFolder => Zero || Contents;
        public bool AnyVideo => Web || Language || Duration;
    }

    public Selection? Result { get; private set; }
    private bool _syncingAll;

    public CheckDialog()
    {
        InitializeComponent();
        foreach (var box in Boxes) box.IsCheckedChanged += (_, _) => OnBoxChanged();
        AllCheck.IsCheckedChanged += (_, _) =>
        {
            if (_syncingAll) return;
            bool on = AllCheck.IsChecked == true;
            _syncingAll = true;
            // Solo le voci attive: senza sottocartelle quelle delle cartelle restano come sono
            foreach (var box in Boxes.Where(b => b.IsEnabled)) box.IsChecked = on;
            _syncingAll = false;
            UpdateOk();
        };
    }

    private CheckBox[] Boxes => new[] { ZeroCheck, ContentsCheck, WebCheck, LangCheck, DurationCheck };
    private IEnumerable<CheckBox> Active => Boxes.Where(b => b.IsEnabled);

    private void OnBoxChanged()
    {
        if (_syncingAll) return;
        _syncingAll = true;
        AllCheck.IsChecked = Active.All(b => b.IsChecked == true);
        _syncingAll = false;
        UpdateOk();
    }

    private void UpdateOk() => OkButton.IsEnabled = Active.Any(b => b.IsChecked == true);

    /// <param name="hasFolders">Ci sono sottocartelle da analizzare (altrimenti File vuoti e Contenuto sono spenti).</param>
    /// <param name="prompt">Cosa si controlla (cartella aperta, selezione, PC).</param>
    /// <param name="recheckAll">Ricontrolla anche i video già controllati (cambia solo il testo).</param>
    /// <param name="minDurationMinutes">Soglia dei video corti (per il testo della voce Durata).</param>
    /// <returns>I controlli scelti, null se annullato.</returns>
    public static async Task<Selection?> ShowAsync(Window owner, bool hasFolders, string? prompt = null,
        bool recheckAll = false, double minDurationMinutes = 0)
    {
        var dialog = new CheckDialog();
        if (prompt != null) dialog.PromptText.Text = prompt;
        else if (recheckAll) dialog.PromptText.Text = "La cartella aperta e tutte le sottocartelle; i video si rileggono anche se già controllati.";
        if (minDurationMinutes > 0)
            dialog.DurationHint.Text = $"Durata di ogni video; segnala i più corti di {Services.Sftp.WebStreamCheckerService.FormatMinutes(minDurationMinutes * 60)} (colonna Durata)";

        // Ultima scelta
        var settings = App.Services?.GetService(typeof(SettingsService)) as SettingsService;
        var s = settings?.Current;
        dialog.ZeroCheck.IsChecked = s?.FolderCheckZero ?? true;
        dialog.ContentsCheck.IsChecked = s?.FolderCheckContents ?? true;
        dialog.WebCheck.IsChecked = s?.VideoCheckWeb ?? true;
        dialog.LangCheck.IsChecked = s?.VideoCheckLanguage ?? true;
        dialog.DurationCheck.IsChecked = s?.VideoCheckDuration ?? true;

        if (!hasFolders)
        {
            dialog.ZeroCheck.IsEnabled = false;
            dialog.ContentsCheck.IsEnabled = false;
            dialog.NoFoldersNote.IsVisible = true;
            dialog.SpeedNote.Text = "Ogni video si legge una volta sola, qualunque voce scegli; quelli già controllati e non cambiati si saltano.";
        }
        dialog.OnBoxChanged();
        dialog.Opened += (_, _) => dialog.OkButton.Focus();
        await dialog.ShowDialog(owner);

        if (dialog.Result != null && settings != null)
        {
            // Le voci spente non cambiano la scelta salvata
            if (hasFolders)
            {
                settings.Current.FolderCheckZero = dialog.Result.Zero;
                settings.Current.FolderCheckContents = dialog.Result.Contents;
            }
            settings.Current.VideoCheckWeb = dialog.Result.Web;
            settings.Current.VideoCheckLanguage = dialog.Result.Language;
            settings.Current.VideoCheckDuration = dialog.Result.Duration;
            try { settings.Save(); } catch { }
        }
        return dialog.Result;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        bool On(CheckBox b) => b.IsEnabled && b.IsChecked == true;
        Result = new Selection(On(ZeroCheck), On(ContentsCheck), On(WebCheck), On(LangCheck), On(DurationCheck));
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) { Result = null; Close(); }
}
