using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DownloadManager.Views;

/// <summary>Soglia dei video corti (minuti), dal menu con l'occhio.</summary>
public partial class DurationThresholdDialog : Window
{
    private double? _result;

    public DurationThresholdDialog()
    {
        InitializeComponent();
    }

    /// <returns>I minuti scelti, null se annullato.</returns>
    public static async Task<double?> ShowAsync(Window owner, double currentMinutes)
    {
        var dialog = new DurationThresholdDialog();
        dialog.MinutesBox.Value = (decimal)Math.Max(0, currentMinutes);
        dialog.Opened += (_, _) => dialog.MinutesBox.Focus();
        await dialog.ShowDialog(owner);
        return dialog._result;
    }

    private void OnOkClick(object? sender, RoutedEventArgs e)
    {
        _result = (double)(MinutesBox.Value ?? 0);
        Close();
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close();
}
