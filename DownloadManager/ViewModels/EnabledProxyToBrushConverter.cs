using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DownloadManager.ViewModels;

public class EnabledProxyToBrushConverter : IValueConverter
{
    public static readonly EnabledProxyToBrushConverter Default = new();

    private static readonly IBrush Active = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush Inactive = new SolidColorBrush(Color.Parse("#4B5563"));

    public object? Convert(object? value, Type t, object? p, CultureInfo c)
        => value is true ? Active : Inactive;

    public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public class EnabledProxyToBackgroundConverter : IValueConverter
{
    public static readonly EnabledProxyToBackgroundConverter Default = new();

    private static readonly IBrush ActiveBg = new SolidColorBrush(Color.Parse("#14321E"));
    private static readonly IBrush InactiveBg = Brushes.Transparent;

    public object? Convert(object? value, Type t, object? p, CultureInfo c)
        => value is true ? ActiveBg : InactiveBg;

    public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}