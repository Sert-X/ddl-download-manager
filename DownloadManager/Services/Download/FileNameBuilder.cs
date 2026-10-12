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

        if (isHls)
        {
            originalFileName = $"Episode_{item.EpisodeNumber:D3}";
            ext = "mp4";
        }
        else if (string.IsNullOrEmpty(ext))
        {
            ext = "mp4";
        }

        var fileNameWithoutExt = ApplyPattern(
            pattern,
            series: item.SeriesName,
            episodeNumber: item.EpisodeNumber,
            title: episodeTitle,
            originalFileName: originalFileName,
            year: item.Year);

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

        if (createFolder && !string.IsNullOrWhiteSpace(item.SeriesName))
        {
            var seriesFolder = SanitizeFileName(item.SeriesName);
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
        string year = "")
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