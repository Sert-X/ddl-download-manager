using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DownloadManager.Views;

public partial class InfoDialog : Window
{
    public InfoDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Mostra un dialog informativo con un solo pulsante OK.
    /// </summary>
    public static async Task ShowAsync(Window owner, string message, string title = "Informazione")
    {
        var dlg = new InfoDialog
        {
            Title = title
        };

        dlg.MessageText.Text = message;

        await dlg.ShowDialog(owner);
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}