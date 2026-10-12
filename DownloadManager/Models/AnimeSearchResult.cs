using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media.Imaging;

namespace DownloadManager.Models;

public partial class AnimeSearchResult : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }

    [ObservableProperty] private Bitmap? _poster;
}