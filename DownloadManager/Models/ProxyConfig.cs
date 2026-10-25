using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public enum ProxyProtocol { Http, Socks5 }
public enum ProxyAuthType { None, UserPassword }

public partial class ProxyConfig : ObservableObject
{
    public int Id { get; set; }

    [ObservableProperty] private string _name = "Proxy";
    [ObservableProperty] private ProxyProtocol _protocol = ProxyProtocol.Http;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 8080;
    [ObservableProperty] private ProxyAuthType _auth = ProxyAuthType.None;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;

    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _testResult = string.Empty;
    [ObservableProperty] private DateTime _lastTested;
    [ObservableProperty] private bool _isSelected;

    // === Provider enablement (per-proxy) ===
    [ObservableProperty] private bool _isEnabledForAnimeWorld;
    [ObservableProperty] private bool _isEnabledForAnimeSaturn;
    [ObservableProperty] private bool _isEnabledForStreamingCommunity;
    [ObservableProperty] private bool _isEnabledForDownloads;
    [ObservableProperty] private bool _isEnabledForCb01;
    /// <summary>Attivo per il provider attualmente selezionato nella UI (aggiornato dal VM).</summary>
    [ObservableProperty] private bool _isActiveForSelectedProvider;

    // Flag interni: "l'utente ha disattivato manualmente questo provider"
    public bool ManuallyDisabledForAnimeWorld { get; set; }
    public bool ManuallyDisabledForAnimeSaturn { get; set; }
    public bool ManuallyDisabledForStreamingCommunity { get; set; }
    public bool ManuallyDisabledForDownloads { get; set; }
    public bool ManuallyDisabledForCb01 { get; set; }

    // Helper: ritorna true se il proxy è attivo per almeno un provider
    public bool IsEnabled =>
        IsEnabledForAnimeWorld ||
        IsEnabledForAnimeSaturn ||
        IsEnabledForStreamingCommunity ||
        IsEnabledForDownloads ||
        IsEnabledForCb01;

    partial void OnIsEnabledForAnimeWorldChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ActiveProvidersText));
    }
    partial void OnIsEnabledForAnimeSaturnChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ActiveProvidersText));
    }
    partial void OnIsEnabledForStreamingCommunityChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ActiveProvidersText));
    }
    partial void OnIsEnabledForDownloadsChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ActiveProvidersText));
    }
    partial void OnIsEnabledForCb01Changed(bool value)
    {
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(ActiveProvidersText));
    }

    public string ProtocolText => Protocol == ProxyProtocol.Http ? "HTTP" : "SOCKS5";
    public string DisplayText => $"{ProtocolText.ToLower()}://{Host}:{Port}";
    public string AuthText => Auth == ProxyAuthType.None
        ? "senza auth"
        : $"auth: {Username}";

    public bool IsFailed => TestResult.StartsWith("❌");

    partial void OnTestResultChanged(string value) => OnPropertyChanged(nameof(IsFailed));

    public string PlaywrightServer => $"{ProtocolText.ToLower()}://{Host}:{Port}";

    public int ProtocolIndex
    {
        get => (int)Protocol;
        set => Protocol = (ProxyProtocol)value;
    }

    public int AuthIndex
    {
        get => (int)Auth;
        set => Auth = (ProxyAuthType)value;
    }

    partial void OnProtocolChanged(ProxyProtocol value)
    {
        OnPropertyChanged(nameof(ProtocolText));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(PlaywrightServer));
        OnPropertyChanged(nameof(ProtocolIndex));
    }

    partial void OnHostChanged(string value)
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(PlaywrightServer));
    }

    partial void OnPortChanged(int value)
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(PlaywrightServer));
    }

    partial void OnUsernameChanged(string value) => OnPropertyChanged(nameof(AuthText));

    partial void OnAuthChanged(ProxyAuthType value)
    {
        OnPropertyChanged(nameof(AuthText));
        OnPropertyChanged(nameof(AuthIndex));
    }

    // === Rotazione ===
    public DateTime? LastFailedUtc { get; set; }
    public int ConsecutiveFailures { get; set; }

    // === Risultati test per provider ===
    private Dictionary<string, string> _providerResults = new();

    public Dictionary<string, string> ProviderResults
    {
        get => _providerResults;
        set
        {
            _providerResults = value ?? new();
            OnPropertyChanged(nameof(ProviderResults));
            OnPropertyChanged(nameof(ProviderResultsText));
        }
    }

    public string ProviderResultsText
    {
        get
        {
            if (_providerResults.Count == 0) return "(nessun test provider)";
            return string.Join(" | ", _providerResults
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key}: {kv.Value}"));
        }
    }

    public void SetProviderResult(string provider, string result)
    {
        _providerResults[provider] = result;
        OnPropertyChanged(nameof(ProviderResults));
        OnPropertyChanged(nameof(ProviderResultsText));
    }

    public string ActiveProvidersText
    {
        get
        {
            var list = new List<string>();
            if (IsEnabledForAnimeWorld) list.Add("AW");
            if (IsEnabledForAnimeSaturn) list.Add("AS");
            if (IsEnabledForStreamingCommunity) list.Add("SC");
            if (IsEnabledForDownloads) list.Add("DL");
            if (IsEnabledForCb01) list.Add("CB");
            return list.Count == 0 ? "" : "[" + string.Join(" ", list) + "]";
        }
    }

    /// <summary>Ritorna true se il proxy ha test ✅ per il provider specificato.</summary>
    public bool HasTestOkFor(string providerKey)
        => ProviderResults.TryGetValue(providerKey, out var r)
           && r.StartsWith("✅");

    /// <summary>
    /// Provider da cui questo proxy è stato "rimosso dalla lista corrente".
    /// Il proxy resta in DB ma non appare nella vista di quel provider.
    /// </summary>
    public HashSet<string> RemovedFromProviders { get; set; } = new();
}