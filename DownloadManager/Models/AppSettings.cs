using System.Collections.Generic;

namespace DownloadManager.Models;

public class AppSettings
{
    public string BaseDownloadFolder { get; set; } = string.Empty;
    public bool CreateSeriesFolder { get; set; } = true;
    public string NamingPattern { get; set; } = "{OriginalFileName}";
    public int MaxConcurrency { get; set; } = 3;

    /// <summary>
    /// Ultime ricerche (la più recente per prima).
    /// </summary>
    public List<string> SearchHistory { get; set; } = new();

    /// <summary>
    /// Tema dell'applicazione: "Default" (sistema), "Light", "Dark".
    /// </summary>
    public string ThemeVariant { get; set; } = "Default";
}