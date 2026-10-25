using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DownloadManager.Models;

namespace DownloadManager.ViewModels;

/// <summary>Colore del pallino di stato di un proxy nella lista del provider.</summary>
public class ProxyStatusToBrushConverter : IValueConverter
{
    public static readonly ProxyStatusToBrushConverter Default = new();

    private static readonly IBrush Ok = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush Failed = new SolidColorBrush(Color.Parse("#EF4444"));
    private static readonly IBrush Excluded = new SolidColorBrush(Color.Parse("#4B5563"));
    private static readonly IBrush Untested = new SolidColorBrush(Color.Parse("#F59E0B"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ProxyEntryStatus.Ok => Ok,
        ProxyEntryStatus.Failed => Failed,
        ProxyEntryStatus.Excluded => Excluded,
        _ => Untested
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
