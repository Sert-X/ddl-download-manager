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
                MappingStatus.Valid => VideoboxPalette.Green,
                MappingStatus.Invalid => VideoboxPalette.Red,
                MappingStatus.Mismatch => VideoboxPalette.Orange,
                _ => VideoboxPalette.Gray
            };
        }
        return VideoboxPalette.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotImplementedException();
}