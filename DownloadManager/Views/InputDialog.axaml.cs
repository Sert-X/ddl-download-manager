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

    public static async Task<string?> ShowAsync(Window owner, string prompt, string initialValue = "", string title = "Input")
    {
        var dialog = new InputDialog
        {
            Title = title
        };

        dialog.PromptText.Text = prompt;
        dialog.InputBox.Text = initialValue;

        // Seleziona il testo esistente per una modifica rapida.
        dialog.InputBox.SelectAll();
        dialog.InputBox.Focus();

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