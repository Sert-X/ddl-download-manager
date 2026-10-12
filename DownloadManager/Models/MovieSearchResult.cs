using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media.Imaging;

namespace DownloadManager.Models;

/// <summary>
/// Risultato di ricerca per un film su provider streaming.
/// </summary>
public partial class MovieSearchResult : ObservableObject
{
    public string Title { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public string? Year { get; set; }
    public string? Description { get; set; }
    public bool IsSeries { get; set; }
    public string TitleId { get; set; } = string.Empty;
    public string Type => IsSeries ? "Serie" : "Film";

    /// <summary>
    /// Bitmap del poster, caricato in background dopo la ricerca.
    /// Bindato direttamente in Image.Source (sostituisce il vecchio converter).
    /// </summary>
    [ObservableProperty] private Bitmap? _poster;
}