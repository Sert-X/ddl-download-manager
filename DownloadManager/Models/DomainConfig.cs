using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

/// <summary>
/// Dominio di un provider anime. Url editabile in UI, Status/LastTested runtime.
/// </summary>
public partial class DomainConfig : ObservableObject
{
    /// <summary>Nome del provider (es. "AnimeWorld"). Chiave per il resolver.</summary>
    public string ProviderName { get; set; } = "";

    /// <summary>URL base (es. "https://www.animeworld.ac"). Editabile.</summary>
    [ObservableProperty] private string _url = string.Empty;

    /// <summary>URL di default (per reset).</summary>
    public string DefaultUrl { get; set; } = string.Empty;

    [ObservableProperty] private DomainStatus _status = DomainStatus.Unknown;
    [ObservableProperty] private string _statusMessage = "Mai testato";
    [ObservableProperty] private DateTime? _lastTested;
    [ObservableProperty] private bool _isTesting;

    /// <summary>Icona in base allo stato (bindata in UI).</summary>
    public string StatusIcon => Status switch
    {
        DomainStatus.Ok => "🟢",
        DomainStatus.Unreachable => "🔴",
        _ => "⚪"
    };

    /// <summary>True se l'URL è stato modificato rispetto al default.</summary>
    public bool IsModified => !string.Equals(Url, DefaultUrl, StringComparison.OrdinalIgnoreCase);

    partial void OnStatusChanged(DomainStatus value)
    {
        OnPropertyChanged(nameof(StatusIcon));
    }

    partial void OnUrlChanged(string value)
    {
        OnPropertyChanged(nameof(IsModified));
    }
}