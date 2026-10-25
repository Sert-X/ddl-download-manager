using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public enum ProxyProtocol { Http, Socks5 }
public enum ProxyAuthType { None, UserPassword }

/// <summary>
/// Un proxy dell'archivio. In quali liste provider compare (e con che esito) sta in <see cref="Lists"/>.
/// </summary>
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
    [ObservableProperty] private bool _isSelected;

    // === Vista della lista selezionata nella tab (aggiornati dal VM) ===
    [ObservableProperty] private string _testResult = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusOk), nameof(StatusFailed), nameof(StatusExcluded), nameof(StatusUntested), nameof(StatusText))]
    private ProxyEntryStatus _statusForSelected;
    [ObservableProperty] private bool _isActiveForSelectedProvider;
    [ObservableProperty] private string _lastTestedText = string.Empty;
    [ObservableProperty] private string _latencyText = string.Empty;
    [ObservableProperty] private string _detailText = string.Empty;
    [ObservableProperty] private IReadOnlyList<ProxyListBadge> _listBadges = Array.Empty<ProxyListBadge>();
    [ObservableProperty] private bool _showStatus = true;

    public bool StatusOk => StatusForSelected == ProxyEntryStatus.Ok;
    public bool StatusFailed => StatusForSelected == ProxyEntryStatus.Failed;
    public bool StatusExcluded => StatusForSelected == ProxyEntryStatus.Excluded;
    public bool StatusUntested => StatusForSelected == ProxyEntryStatus.Untested;
    public string StatusText => ProxyListBadge.Label(StatusForSelected);

    // === Rotazione durante l'uso ===
    public DateTime? LastFailedUtc { get; set; }
    public int ConsecutiveFailures { get; set; }

    /// <summary>Liste provider in cui compare il proxy (chiave = ProviderKeys.*).</summary>
    public Dictionary<string, ProxyListEntry> Lists { get; set; } = new();

    public ProxyListEntry? EntryFor(string provider) => Lists.TryGetValue(provider, out var e) ? e : null;

    public ProxyListEntry GetOrAddEntry(string provider)
    {
        if (!Lists.TryGetValue(provider, out var e))
            Lists[provider] = e = new ProxyListEntry { Provider = provider };
        return e;
    }

    /// <summary>Stesso proxy anche se scritto in modo diverso: host (minuscolo), porta e utente.</summary>
    public string IdentityKey => $"{Host.Trim().ToLowerInvariant()}:{Port}:{Username.Trim()}";

    public string ProtocolText => Protocol == ProxyProtocol.Http ? "HTTP" : "SOCKS5";
    public string DisplayText => $"{ProtocolText.ToLower()}://{Host}:{Port}";
    public string Address => $"{Host}:{Port}";
    public string AuthText => Auth == ProxyAuthType.None ? "senza auth" : $"auth: {Username}";
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
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(PlaywrightServer));
    }

    partial void OnPortChanged(int value)
    {
        OnPropertyChanged(nameof(Address));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(PlaywrightServer));
    }

    partial void OnUsernameChanged(string value) => OnPropertyChanged(nameof(AuthText));

    partial void OnAuthChanged(ProxyAuthType value)
    {
        OnPropertyChanged(nameof(AuthText));
        OnPropertyChanged(nameof(AuthIndex));
    }

    public override string ToString() => $"{Host}:{Port}";
}
