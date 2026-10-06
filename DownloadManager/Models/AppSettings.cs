using System.Collections.Generic;

namespace DownloadManager.Models;

public class AppSettings
{
    public string BaseDownloadFolder { get; set; } = string.Empty;
    public bool CreateSeriesFolder { get; set; } = true;
    public string NamingPattern { get; set; } = "{OriginalFileName}";
    public int MaxConcurrency { get; set; } = 3;
    public Dictionary<string, string> ProviderDomains { get; set; } = new();
    public List<string> SearchHistory { get; set; } = new();
    public string ThemeVariant { get; set; } = "Default";
}