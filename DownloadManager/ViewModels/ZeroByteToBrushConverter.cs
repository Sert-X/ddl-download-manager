using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DownloadManager.ViewModels;

public class ZeroByteToBrushConverter : IValueConverter
{
    public static readonly ZeroByteToBrushConverter Default = new();

    private static readonly IBrush Highlight = new SolidColorBrush(Color.Parse("#5A1515"));
    private static readonly IBrush Normal = Brushes.Transparent;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Highlight : Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}