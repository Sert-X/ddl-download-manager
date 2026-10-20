using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DownloadManager.ViewModels;

public class StatusToBrushConverter : IValueConverter
{
    // AGGIUNTA FONDAMENTALE PER XAML
    public static StatusToBrushConverter Default { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is MappingStatus status)
        {
            return status switch
            {
                MappingStatus.Valid => Brushes.LimeGreen,
                MappingStatus.Invalid => Brushes.OrangeRed,
                MappingStatus.Mismatch => Brushes.Gold,
                _ => Brushes.Gray
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotImplementedException();
}