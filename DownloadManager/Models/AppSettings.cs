using System.Collections.Generic;

namespace DownloadManager.Models;

public class AppSettings
{
    // ============================================================
    //  ANIME (tab "Anime Download")
    // ============================================================
    public string BaseDownloadFolder { get; set; } = string.Empty;
    public bool CreateSeriesFolder { get; set; } = true;
    public string NamingPattern { get; set; } = "{OriginalFileName}";

    // ============================================================
    //  STREAMING (tab "Film/Serie Download")
    // ============================================================
    public string StreamingBaseDownloadFolder { get; set; } = string.Empty;
    public bool StreamingCreateSeriesFolder { get; set; } = true;
    public string StreamingMovieNamingPattern { get; set; } = "{Series} ({Anno})";
    public string StreamingSeriesNamingPattern { get; set; } = "{Episode}";
    public string SeasonFolderPattern { get; set; } = "Stagione {number}";

    // ============================================================
    //  CONDIVISI
    // ============================================================
    public int MaxConcurrency { get; set; } = 3;
    public Dictionary<string, string> ProviderDomains { get; set; } = new();
    public List<string> SearchHistory { get; set; } = new();
    public string ThemeVariant { get; set; } = "Default";

    // ==== Proxy globali per provider ====
    public bool UseProxyForAnimeWorld { get; set; } = true;
    public bool UseProxyForAnimeSaturn { get; set; } = true;
    public bool UseProxyForStreamingCommunity { get; set; } = true;
    public bool UseProxyForDownloads { get; set; } = true;
}