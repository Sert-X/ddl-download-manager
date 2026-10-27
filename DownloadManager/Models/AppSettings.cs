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
    /// <summary>Pattern per i download da CB01: {OriginalFileName} è il nome del file su Maxstream.</summary>
    public string Cb01MovieNamingPattern { get; set; } = "{OriginalFileName}";
    public string Cb01SeriesNamingPattern { get; set; } = "{OriginalFileName}";
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
    /// <summary>Versione dell'ultimo avvio: se cambia, al primo avvio si mostrano le novità.</summary>
    public string LastRunVersion { get; set; } = "";

    /// <summary>Notifiche di Windows a fine lavoro (serie, code SFTP, errori, disco pieno).</summary>
    public bool NotificationsEnabled { get; set; } = true;
    /// <summary>Ogni quanti minuti ricontrollare i domini in background (0 = solo all'avvio).</summary>
    public int DomainCheckIntervalMinutes { get; set; } = 10;
    /// <summary>Conversione proposta per i download CB01 (si può cambiare per ogni download).</summary>
    public VideoConversion DefaultVideoConversion { get; set; } = VideoConversion.Mp4Copy;   // come fino alla 1.3.0: sempre .mp4

    public bool UseProxyForAnimeWorld { get; set; } = true;
    public bool UseProxyForAnimeSaturn { get; set; } = true;
    public bool UseProxyForStreamingCommunity { get; set; } = true;
    public bool UseProxyForDownloads { get; set; } = true;
    public bool UseProxyForCb01 { get; set; }

    /// <summary>Dopo un test fallito il proxy viene escluso dalla lista del provider (non si ritesta più).</summary>
    public bool ProxyAutoExcludeFailed { get; set; } = true;
    public int ProxyTestConcurrency { get; set; } = 10;

    /// <summary>URL di FlareSolverr (es. http://host:8191/token). Vuoto = bypass CF locale con Playwright.</summary>
    public string FlareSolverrUrl { get; set; } = string.Empty;

    // ============================================================
    //  VIDEBOX CORRECTOR
    // ============================================================
    public string IpsBaseUrl { get; set; } = "https://ddlstreamitaly.cc";
    public string IpsApiKey { get; set; } = "";
    public string PrimaryStorageUrl { get; set; } = "https://ichigolink.top";
    public string SecondaryStorageUrlsRaw { get; set; } = "https://data2ddl.ichigotv.net";
    public string SftpBasePath { get; set; } = "/var/www/html/media";
    public string VideoboxPublicPath { get; set; } = "Anime SUB.ITA"; 

    /// <summary>Sorgenti SFTP di Videobox Corrector (vuoto = si crea dalla vecchia configurazione singola).</summary>
    public List<VideoboxSourceConfig> VideoboxSources { get; set; } = new();
    /// <summary>Sorgenti da caricare per ogni categoria (chiave = Id categoria). Assente = tutte.</summary>
    public Dictionary<string, List<string>> VideoboxCategorySources { get; set; } = new();
    /// <summary>
    /// Sorgenti ESCLUSE per categoria (sostituisce l'elenco delle attive): una sorgente aggiunta
    /// dopo parte attiva in tutte le categorie, invece di risultare spenta.
    /// </summary>
    public Dictionary<string, List<string>> VideoboxCategoryDisabledSources { get; set; } = new();

    /// <summary>Domini considerati validi: quelli delle sorgenti, il primario storico e i secondari.</summary>
    public IEnumerable<string> AllValidStorageDomains =>
        VideoboxSources.Select(s => s.Domain)
        .Append(PrimaryStorageUrl)
        .Concat(SecondaryStorageUrlsRaw?.Split(',', System.StringSplitOptions.RemoveEmptyEntries) ?? System.Array.Empty<string>())
        .Select(d => (d ?? "").Trim())
        .Where(d => !string.IsNullOrWhiteSpace(d))
        .Distinct(System.StringComparer.OrdinalIgnoreCase);
}