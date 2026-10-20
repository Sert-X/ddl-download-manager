using System.Collections.Generic;
using System.Linq;

namespace DownloadManager.Models;

public class AppSettings
{
    public string BaseDownloadFolder { get; set; } = string.Empty;
    public bool CreateSeriesFolder { get; set; } = true;
    public string NamingPattern { get; set; } = "{OriginalFileName}";

    public string StreamingBaseDownloadFolder { get; set; } = string.Empty;
    public bool StreamingCreateSeriesFolder { get; set; } = true;
    public string StreamingMovieNamingPattern { get; set; } = "{Series} ({Anno})";
    public string StreamingSeriesNamingPattern { get; set; } = "{Episode}";
    public string SeasonFolderPattern { get; set; } = "Stagione {number}";

    /// <summary>Download simultanei della tab Anime.</summary>
    public int MaxConcurrency { get; set; } = 3;
    /// <summary>Download simultanei della tab Film/Serie (slot separati da quelli anime).</summary>
    public int MaxConcurrencyStreaming { get; set; } = 1;
    /// <summary>Segmenti HLS scaricati in parallelo per l'intero download (suddivisi tra video e tracce audio).</summary>
    public int StreamingConcurrentFragments { get; set; } = 24;
    /// <summary>Ultime lingue audio scelte in Film/Serie (separate da '|'): preselezionate all'analisi successiva.</summary>
    public string StreamingAudioLanguages { get; set; } = "";
    /// <summary>Ultimi sottotitoli scelti in Film/Serie (separati da '|').</summary>
    public string StreamingSubtitleLanguages { get; set; } = "";
    public bool StreamingBurnSubtitles { get; set; }
    public Dictionary<string, string> ProviderDomains { get; set; } = new();
    public List<string> SearchHistory { get; set; } = new();
    public string ThemeVariant { get; set; } = "Default";

    public bool UseProxyForAnimeWorld { get; set; } = true;
    public bool UseProxyForAnimeSaturn { get; set; } = true;
    public bool UseProxyForStreamingCommunity { get; set; } = true;
    public bool UseProxyForDownloads { get; set; } = true;

    // ============================================================
    //  VIDEBOX CORRECTOR
    // ============================================================
    public string IpsBaseUrl { get; set; } = "https://ddlstreamitaly.cc";
    public string IpsApiKey { get; set; } = "";
    public string PrimaryStorageUrl { get; set; } = "https://ichigolink.top";
    public string SecondaryStorageUrlsRaw { get; set; } = "https://data2ddl.ichigotv.net";
    public string SftpBasePath { get; set; } = "/var/www/html/media";
    public string VideoboxPublicPath { get; set; } = "Anime SUB.ITA"; 

    public IEnumerable<string> AllValidStorageDomains => 
        new[] { PrimaryStorageUrl }
        .Concat(SecondaryStorageUrlsRaw?.Split(',', System.StringSplitOptions.RemoveEmptyEntries) ?? System.Array.Empty<string>())
        .Select(d => d.Trim())
        .Where(d => !string.IsNullOrWhiteSpace(d));
}