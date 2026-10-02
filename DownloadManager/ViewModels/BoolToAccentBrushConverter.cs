using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DownloadManager.ViewModels;

public class BoolToAccentBrushConverter : IValueConverter
{
    public static readonly BoolToAccentBrushConverter Default = new();

    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush Gray = new SolidColorBrush(Color.Parse("#6B7280"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Green : Gray;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}