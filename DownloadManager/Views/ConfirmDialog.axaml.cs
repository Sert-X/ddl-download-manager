using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DownloadManager.Views;

public partial class ConfirmDialog : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    /// <param name="safeDefault">Azione distruttiva: il fuoco parte da "Annulla" e Invio non conferma.</param>
    public static async Task<bool> ShowAsync(Window owner, string prompt, string title = "Conferma", bool safeDefault = false,
                                             string? okText = null, string? cancelText = null)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.TitleText.Text = title;
        dialog.PromptText.Text = prompt;
        if (okText != null) dialog.OkText.Text = okText;
        if (cancelText != null) dialog.CancelText.Text = cancelText;
        if (safeDefault)
        {
            dialog.OkButton.IsDefault = false;
            dialog.CancelButton.IsDefault = true;
        }

        dialog.Opened += (_, _) => (safeDefault ? dialog.CancelButton : dialog.OkButton).Focus();

        await dialog.ShowDialog(owner);
        return dialog.Confirmed;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}