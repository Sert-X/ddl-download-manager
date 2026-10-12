using System.Collections.Concurrent;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace DownloadManager.ViewModels;

/// <summary>
/// Converte un URL HTTP in Bitmap per il binding di Image.Source.
/// Usa una cache globale per non ri-scaricare la stessa immagine.
/// </summary>
public class PosterUrlConverter : IValueConverter
{
    public static readonly PosterUrlConverter Instance = new();

    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
            return null;

        return Cache.GetOrAdd(url, static u =>
        {
            try { return new Bitmap(u); }
            catch { return null; }
        });
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}