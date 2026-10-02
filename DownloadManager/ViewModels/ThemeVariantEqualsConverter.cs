using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace DownloadManager.ViewModels;

/// <summary>
/// Converter per legare RadioButton a una proprietà stringa.
/// Restituisce true se il valore corrisponde al parametro del converter.
/// </summary>
public class ThemeVariantEqualsConverter : IValueConverter
{
    public static readonly ThemeVariantEqualsConverter Default = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var current = value as string ?? "Default";
        var target = parameter as string ?? "Default";
        return string.Equals(current, target, StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is string target)
            return target;

        return BindingOperations.DoNothing;
    }
}