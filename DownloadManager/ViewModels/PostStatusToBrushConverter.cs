using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DownloadManager.ViewModels;

public class PostStatusToBrushConverter : IValueConverter
{
    public static PostStatusToBrushConverter Default { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is PostStatus status)
        {
            return status switch
            {
                PostStatus.Valid => Brushes.LimeGreen,
                PostStatus.Invalid => Brushes.OrangeRed,
                PostStatus.Mixed => Brushes.Gold,
                PostStatus.Mismatch => Brushes.MediumPurple, // <-- VIOLA
                _ => Brushes.Gray
            };
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) 
        => throw new NotImplementedException();
}