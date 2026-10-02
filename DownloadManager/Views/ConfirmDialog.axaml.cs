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

    public static async Task<bool> ShowAsync(Window owner, string prompt, string title = "Conferma")
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.PromptText.Text = prompt;

        dialog.Opened += (_, _) => dialog.OkButton.Focus();

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