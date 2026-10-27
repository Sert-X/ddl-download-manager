using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Media.Imaging;
using System.Text.RegularExpressions;

namespace DownloadManager.Models;

public partial class AnimeSearchResult : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }

    /// <summary>Anno di uscita (null se il sito non lo indica).</summary>
    public int? Year { get; set; }
    /// <summary>Numero di episodi ("??" se in corso; null se il sito non lo indica).</summary>
    public string? Episodes { get; set; }

    /// <summary>Riga sotto il titolo, es. "2017 · 24 ep".</summary>
    public string InfoText
    {
        get
        {
            var parts = new List<string>();
            if (Year is { } y) parts.Add(y.ToString());
            if (!string.IsNullOrEmpty(Episodes)) parts.Add(Episodes == "??" ? "in corso" : $"{Episodes} ep");
            return string.Join(" · ", parts);
        }
    }
    public bool HasInfo => InfoText.Length > 0;

    [ObservableProperty] private Bitmap? _poster;

    public AnimeSearchResult Clone() => new()
    {
        Name = Name, Link = Link, PosterUrl = PosterUrl, Year = Year, Episodes = Episodes
    };

    private static readonly Regex YearRegex = new(@"\b(19[5-9]\d|20\d\d)\b", RegexOptions.Compiled);
    private static readonly Regex EpisodesRegex = new(@"(\d+|\?\?)\s*ep\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Anno da un testo come "05 Aprile 2017".</summary>
    public static int? ParseYear(string? text)
    {
        var m = YearRegex.Match(text ?? "");
        return m.Success ? int.Parse(m.Value) : null;
    }

    /// <summary>Episodi da un testo come "06 Agosto 2022 · 12 ep".</summary>
    public static string? ParseEpisodes(string? text)
    {
        var m = EpisodesRegex.Match(text ?? "");
        return m.Success ? m.Groups[1].Value : null;
    }
}
