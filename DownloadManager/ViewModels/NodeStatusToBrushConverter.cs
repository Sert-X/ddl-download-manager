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
                NodeStatus.Valid => VideoboxPalette.Green,
                NodeStatus.Invalid => VideoboxPalette.Red,
                NodeStatus.Mixed => VideoboxPalette.Orange,
                NodeStatus.Mismatch => VideoboxPalette.Purple,
                _ => VideoboxPalette.Gray
            };
        }
        return VideoboxPalette.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotImplementedException();
}