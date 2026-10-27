using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace DownloadManager.ViewModels;

/// <summary>
/// Colori degli stati di Videobox Corrector: gli stessi del resto dell'app (pallini delle tab,
/// guida). Prima erano colori "di sistema" (LimeGreen, OrangeRed, Gold) che stonavano.
/// </summary>
public static class VideoboxPalette
{
    public static readonly IBrush Green = new ImmutableSolidColorBrush(Color.Parse("#22C55E"));
    public static readonly IBrush Orange = new ImmutableSolidColorBrush(Color.Parse("#F59E0B"));
    public static readonly IBrush Red = new ImmutableSolidColorBrush(Color.Parse("#EF4444"));
    public static readonly IBrush Purple = new ImmutableSolidColorBrush(Color.Parse("#A855F7"));
    public static readonly IBrush Gray = new ImmutableSolidColorBrush(Color.Parse("#8B92A8"));
}

/// <summary>Testo breve dello stato di un post (scheda del post in alto a destra).</summary>
public class PostStatusToTextConverter : IValueConverter
{
    public static PostStatusToTextConverter Default { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        PostStatus.Valid => "Link validi",
        PostStatus.Mixed => "Link misti",
        PostStatus.Invalid => "Link da sistemare",
        PostStatus.Mismatch => "File e link non coincidono",
        _ => "Nessun link"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Spiegazione del pallino di una riga della tabella.</summary>
public class MappingStatusToTextConverter : IValueConverter
{
    public static MappingStatusToTextConverter Default { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        MappingStatus.Valid => "Dominio valido",
        MappingStatus.Invalid => "Dominio non valido: link da correggere",
        MappingStatus.Mismatch => "Da controllare",
        _ => "Non ancora valutato"
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
