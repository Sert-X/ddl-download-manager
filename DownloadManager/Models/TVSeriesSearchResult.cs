using System.Collections.ObjectModel;

namespace DownloadManager.Models;

/// <summary>
/// Risultato di ricerca per una serie TV su provider streaming.
/// </summary>
public class TVSeriesSearchResult
{
    public string Title { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public string? Year { get; set; }
    public string? Description { get; set; }
    public ObservableCollection<Season> Seasons { get; set; } = new();
}