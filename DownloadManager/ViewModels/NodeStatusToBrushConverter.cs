using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DownloadManager.ViewModels;

public class NodeStatusToBrushConverter : IValueConverter
{
    public static NodeStatusToBrushConverter Default { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is NodeStatus status)
        {
            return status switch
            {
                NodeStatus.Valid => Brushes.LimeGreen,
                NodeStatus.Invalid => Brushes.OrangeRed,
                NodeStatus.Mixed => Brushes.Gold,
                NodeStatus.Mismatch => Brushes.MediumPurple, // <-- VIOLA
                _ => Brushes.Gray
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotImplementedException();
}