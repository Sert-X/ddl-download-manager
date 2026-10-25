using System.Text;
using System.Text.RegularExpressions;
using DownloadManager.Models;
using DownloadManager.Persistence;

namespace DownloadManager.Services.Download;

public class FileNameBuilder
{
    private readonly SettingsService _settings;

    private static readonly Regex PlaceholderRegex = new(
        @"\{(?<name>[A-Za-z]+)(?::(?<format>[^}]+))?\}",
        RegexOptions.Compiled);

    public FileNameBuilder(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Calcola il percorso completo di destinazione per un DownloadItem.
    /// Il pattern viene preso da settings.NamingPattern, a meno che non venga
    /// passato esplicitamente patternOverride (per distinguere Anime/Streaming).
    /// </summary>
    public string BuildDestinationPath(
        DownloadItem item,
        string originalVideoUrl,
        string episodeTitle,
        string? patternOverride = null)
    {
        var settings = _settings.Current;
        var pattern = patternOverride ?? settings.NamingPattern;

        string originalFileName;
        string ext;

        try
        {
            var uri = new Uri(originalVideoUrl);
            originalFileName = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            ext = Path.GetExtension(uri.AbsolutePath).TrimStart('.');
        }
        catch
        {
            originalFileName = "";
            ext = "";
        }

        bool isHls = ext.Equals("m3u8", StringComparison.OrdinalIgnoreCase)
                    || originalFileName.Equals("playlist", StringComparison.OrdinalIgnoreCase)
                    || originalFileName.Equals("master", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(originalFileName)
                    || originalVideoUrl.Contains("vixcloud.co/playlist/", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(item.SourceFileName))
        {
            // Nome reale dichiarato dal sito (es. Maxstream): vale più di quello ricavato dall'URL
            var srcExt = Path.GetExtension(item.SourceFileName).TrimStart('.');
            bool knownExt = srcExt.Length is > 0 and <= 4 && srcExt.All(char.IsLetterOrDigit) && !srcExt.All(char.IsDigit);
            originalFileName = knownExt ? Path.GetFileNameWithoutExtension(item.SourceFileName) : item.SourceFileName;
            ext = isHls ? "mp4" : (knownExt ? srcExt : (string.IsNullOrEmpty(ext) ? "mp4" : ext));
        }
        else if (isHls)
        {
            originalFileName = $"Episode_{item.EpisodeNumber:D3}";
            ext = "mp4";
        }
        else if (string.IsNullOrEmpty(ext))
        {
            ext = "mp4";
        }

        // La coda raggruppa le stagioni come "Titolo S01": nel nome file e nella cartella serve il titolo puro
        var seriesTitle = SeriesTitle(item.SeriesName, item.SeasonNumber);

        var fileNameWithoutExt = ApplyPattern(
            pattern,
            series: seriesTitle,
            episodeNumber: item.EpisodeNumber,
            title: episodeTitle,
            originalFileName: originalFileName,
            year: item.Year,
            season: item.SeasonNumber);

        fileNameWithoutExt = SanitizeFileName(fileNameWithoutExt);

        // Cartella base: usa quella Streaming se pattern override è Streaming
        // (l'override è passato solo dal tab Streaming → settings streaming)
        var baseFolder = patternOverride != null
            ? settings.StreamingBaseDownloadFolder
            : settings.BaseDownloadFolder;

        var createFolder = patternOverride != null
            ? settings.StreamingCreateSeriesFolder
            : settings.CreateSeriesFolder;

        var folder = string.IsNullOrEmpty(baseFolder)
            ? settings.BaseDownloadFolder
            : baseFolder;

        if (createFolder && !string.IsNullOrWhiteSpace(seriesTitle))
        {
            var seriesFolder = SanitizeFileName(seriesTitle);
            folder = Path.Combine(folder, seriesFolder);

            if (item.SeasonNumber > 0 &&
                !string.IsNullOrWhiteSpace(settings.SeasonFolderPattern))
            {
                var seasonFolder = BuildSeasonFolderName(
                    settings.SeasonFolderPattern, item.SeasonNumber);
                if (!string.IsNullOrWhiteSpace(seasonFolder))
                    folder = Path.Combine(folder, seasonFolder);
            }
        }

        return Path.Combine(folder, fileNameWithoutExt + "." + ext);
    }

    /// <summary>
    /// Nome del gruppo in coda → titolo per file e cartella: "I Simpson S01" → "I Simpson",
    /// "Boruto S01 Sub ITA" → "Boruto (Sub ITA)" (cartella separata dalla versione italiana).
    /// </summary>
    public static string SeriesTitle(string? seriesName, int season)
    {
        var name = seriesName ?? "";
        bool sub = name.EndsWith(" Sub ITA", StringComparison.Ordinal);
        if (sub) name = name[..^" Sub ITA".Length].TrimEnd();
        if (season > 0)
        {
            var suffix = $" S{season:D2}";
            if (name.EndsWith(suffix, StringComparison.Ordinal)) name = name[..^suffix.Length].TrimEnd();
        }
        return sub ? $"{name} (Sub ITA)" : name;
    }

    private static string BuildSeasonFolderName(string pattern, int seasonNumber)
    {
        var result = Regex.Replace(pattern, @"\{number(?::(D\d+))?\}", m =>
        {
            var format = m.Groups[1].Success ? m.Groups[1].Value : null;
            if (!string.IsNullOrEmpty(format) && format.StartsWith("D", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(format.Substring(1), out var digits))
                    return seasonNumber.ToString("D" + digits);
            }
            return seasonNumber.ToString();
        });

        return SanitizeFileName(result);
    }

    public static string ApplyPattern(
        string pattern,
        string series,
        int episodeNumber,
        string title,
        string originalFileName,
        string year = "",
        int season = 0)
    {
        return PlaceholderRegex.Replace(pattern, match =>
        {
            var name = match.Groups["name"].Value;
            var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;

            return name switch
            {
                "Series"           => series ?? "",
                "Episode"          => FormatNumber(episodeNumber, format),
                "Title"            => title ?? "",
                "OriginalFileName" => originalFileName ?? "",
                "Anno"             => year ?? "",
                "Season"           => FormatNumber(Math.Max(season, 1), format),
                _ => match.Value
            };
        });
    }

    private static string FormatNumber(int number, string? format)
    {
        if (!string.IsNullOrEmpty(format) && format.StartsWith("D", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(format.Substring(1), out var digits))
            {
                return number.ToString("D" + digits);
            }
        }
        return number.ToString();
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        return sb.ToString().Trim();
    }
}