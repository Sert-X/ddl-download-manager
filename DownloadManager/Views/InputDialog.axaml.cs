using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DownloadManager.Views;

public partial class InputDialog : Window
{
    public string? Result { get; private set; }

    public InputDialog()
    {
        InitializeComponent();
    }

    /// <param name="selectNameOnly">
    /// Seleziona solo il nome, senza l'estensione (come Esplora risorse): scrivendo si
    /// cambia il nome e l'estensione resta. Per le cartelle va lasciato false.
    /// </param>
    public static async Task<string?> ShowAsync(Window owner, string prompt, string initialValue = "", string title = "Input",
                                                bool selectNameOnly = false)
    {
        var dialog = new InputDialog
        {
            Title = title
        };

        dialog.TitleText.Text = title;
        dialog.PromptText.Text = prompt;
        dialog.InputBox.Text = initialValue;

        // Seleziona il testo esistente per una modifica rapida (dopo l'apertura,
        // altrimenti il focus non viene preso).
        dialog.Opened += (_, _) =>
        {
            // Prima il focus, poi la selezione (prendendo il focus il cursore si sposta)
            dialog.InputBox.Focus();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var text = dialog.InputBox.Text ?? "";
                int dot = selectNameOnly ? text.LastIndexOf('.') : -1;
                if (dot > 0)
                {
                    dialog.InputBox.SelectionStart = 0;
                    dialog.InputBox.SelectionEnd = dot;
                }
                else dialog.InputBox.SelectAll();
            }, Avalonia.Threading.DispatcherPriority.Input);
        };

        await dialog.ShowDialog(owner);
        return dialog.Result;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        Result = InputBox.Text?.Trim();
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = null;
        Close();
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Result = InputBox.Text?.Trim();
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Result = null;
            Close();
            e.Handled = true;
        }
    }
}