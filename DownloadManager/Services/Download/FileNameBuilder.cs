using System.Text;
using System.Text.RegularExpressions;
using DownloadManager.Models;
using DownloadManager.Persistence;

namespace DownloadManager.Services.Download;

public class FileNameBuilder
{
    private readonly SettingsService _settings;

    // Regex per trovare placeholder tipo {Episode:D3} o {Series}.
    private static readonly Regex PlaceholderRegex = new(
        @"\{(?<name>[A-Za-z]+)(?::(?<format>[^}]+))?\}",
        RegexOptions.Compiled);

    public FileNameBuilder(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Calcola il percorso completo di destinazione per un DownloadItem.
    /// </summary>
    public string BuildDestinationPath(DownloadItem item, string originalVideoUrl, string episodeTitle)
    {
        var settings = _settings.Current;

        // 1. Estrai nome file ed estensione dall'URL
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

        // Rileva se è un HLS playlist (.m3u8) → il nome file è inutile ("playlist"/"master")
        bool isHls = ext.Equals("m3u8", StringComparison.OrdinalIgnoreCase)
                     || originalFileName.Equals("playlist", StringComparison.OrdinalIgnoreCase)
                     || originalFileName.Equals("master", StringComparison.OrdinalIgnoreCase)
                     || string.IsNullOrEmpty(originalFileName);

        if (isHls)
        {
            // Usa un nome generico basato sull'episodio
            originalFileName = $"Episode_{item.EpisodeNumber:D3}";
            ext = "mp4"; // il file scaricato da ffmpeg sarà sempre mp4
        }
        else if (string.IsNullOrEmpty(ext))
        {
            ext = "mp4";
        }

        // 2. Applica il pattern
        var fileNameWithoutExt = ApplyPattern(
            settings.NamingPattern,
            series: item.SeriesName,
            episodeNumber: item.EpisodeNumber,
            title: episodeTitle,
            originalFileName: originalFileName);

        fileNameWithoutExt = SanitizeFileName(fileNameWithoutExt);

        // 3. Componi la cartella finale
        var folder = settings.BaseDownloadFolder;
        if (settings.CreateSeriesFolder && !string.IsNullOrWhiteSpace(item.SeriesName))
        {
            var seriesFolder = SanitizeFileName(item.SeriesName);
            folder = Path.Combine(folder, seriesFolder);
        }

        return Path.Combine(folder, fileNameWithoutExt + "." + ext);
    }

    public static string ApplyPattern(
        string pattern,
        string series,
        int episodeNumber,
        string title,
        string originalFileName)
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
                _ => match.Value
            };
        });
    }

    private static string FormatNumber(int number, string? format)
    {
        // Supporto "D3" → 001, "D2" → 01, ecc.
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