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

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _testResult = string.Empty;
    [ObservableProperty] private DateTime _lastTested;
    [ObservableProperty] private bool _isSelected;

    public string ProtocolText => Protocol == ProxyProtocol.Http ? "HTTP" : "SOCKS5";

    public string DisplayText => $"{ProtocolText.ToLower()}://{Host}:{Port}";

    public string AuthText => Auth == ProxyAuthType.None
        ? "senza auth"
        : $"auth: {Username}";

    public bool IsFailed => TestResult.StartsWith("❌");

    partial void OnTestResultChanged(string value) => OnPropertyChanged(nameof(IsFailed));

    /// <summary>
    /// Formato accettato da Playwright: "http://host:port" o "socks5://host:port".
    /// </summary>
    public string PlaywrightServer => $"{ProtocolText.ToLower()}://{Host}:{Port}";

    // Helper per le ComboBox in XAML (SelectedIndex è int, gli enum non lo sono)
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

    // ==== Notifiche cambio proprietà (una sola dichiarazione per ciascuna!) ====

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

    partial void OnUsernameChanged(string value)
    {
        OnPropertyChanged(nameof(AuthText));
    }

    partial void OnAuthChanged(ProxyAuthType value)
    {
        OnPropertyChanged(nameof(AuthText));
        OnPropertyChanged(nameof(AuthIndex));
    }
}