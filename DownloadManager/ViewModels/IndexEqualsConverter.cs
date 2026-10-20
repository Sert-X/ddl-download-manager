using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace DownloadManager.ViewModels;

/// <summary>
/// true se l'indice (es. SelectedIndex di una TabControl) è uguale al parametro.
/// Usato per mostrare il contenuto della tab selezionata tenendo le altre nel
/// visual tree (così mantengono posizione di scroll e stato).
/// </summary>
public class IndexEqualsConverter : IValueConverter
{
    public static readonly IndexEqualsConverter Default = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int index) return false;
        return parameter switch
        {
            int i => index == i,
            string s when int.TryParse(s, out var i) => index == i,
            _ => false
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
