using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Services.Streaming.StreamingCommunity;
using Microsoft.Extensions.Logging;

namespace DownloadManager.ViewModels;

public partial class AccountTabViewModel : ViewModelBase
{
    private readonly StreamingCommunityAuthService _auth;
    private readonly ILogger<AccountTabViewModel> _logger;
    private readonly DownloadManager.Services.Streaming.StreamingCommunity.IStreamingCommunityService _streaming;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _statusMessage = "Inserisci le credenziali e premi Accedi.";
    [ObservableProperty] private bool _isBusy;

    // Stato post-login
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isPremium;
    [ObservableProperty] private string _planName = string.Empty;
    [ObservableProperty] private string _planUpdatedAt = string.Empty;
    [ObservableProperty] private int _daysLeft;

    /// <summary>
    /// True se ci sono credenziali salvate (username e/o password non vuoti).
    /// Usata per mostrare/nascondere il pulsante "Elimina credenziali".
    /// </summary>
    public bool HasSavedCredentials =>
        !string.IsNullOrEmpty(_auth.Current.Username) ||
        !string.IsNullOrEmpty(_auth.Current.Password);

    /// <summary>
    /// True quando l'account NON è connesso. Usato per il pallino rosso
    /// nell'header del tab (visibile se true).
    /// </summary>
    public bool HasAccountIssue => !IsLoggedIn;

    public string SessionBadge => IsLoggedIn
        ? (IsPremium ? $"💎 Premium — {PlanName}" : "👤 Free — 720p")
        : "🔒 Non connesso";

    public string SessionDetails => IsLoggedIn
        ? (IsPremium
            ? $"Piano: {PlanName}\nAggiornato: {PlanUpdatedAt}\nGiorni rimanenti: {DaysLeft}"
            : "Account gratuito. Qualità massima 720p.\nAccedi con un account premium per 1080p.")
        : "Nessun account configurato.";

    public AccountTabViewModel(
        StreamingCommunityAuthService auth,
        DownloadManager.Services.Streaming.StreamingCommunity.IStreamingCommunityService streaming,
        ILogger<AccountTabViewModel> logger)
    {
        _auth = auth;
        _streaming = streaming;
        _logger = logger;

        Username = _auth.Current.Username;
        Password = _auth.Current.Password;
        ApplyState();
        // StateChanged può arrivare da un thread di background (fine login)
        _auth.StateChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyState);
    }

    // ============================================================
    //  PROPERTY CHANGED — notifica HasAccountIssue
    // ============================================================

    partial void OnIsLoggedInChanged(bool value)
    {
        OnPropertyChanged(nameof(HasAccountIssue));
    }

    partial void OnUsernameChanged(string value)
    {
        OnPropertyChanged(nameof(HasSavedCredentials));
    }

    partial void OnPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(HasSavedCredentials));
    }

    // ============================================================
    //  COMANDI
    // ============================================================

    [RelayCommand]
    private async Task TestSessionAsync()
    {
        IsBusy = true;
        try
        {
            StatusMessage = "Test sessione Playwright...";
            var (loggedIn, premium, info) = await _streaming.CheckSessionAsync();
            StatusMessage = $"Playwright: LoggedIn={loggedIn}, Premium={premium} — {info}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Test sessione fallito");
            StatusMessage = $"Errore: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SaveCredentials()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            StatusMessage = "Compila username e password prima di salvare.";
            return;
        }

        _auth.Current.Username = Username.Trim();
        _auth.Current.Password = Password;
        _auth.Save();

        StatusMessage = "Credenziali salvate (cifrate con DPAPI).";
    }

    private void ApplyState()
    {
        IsLoggedIn = _auth.Current.LastLoginUtc.HasValue &&
                    !string.IsNullOrEmpty(_auth.Current.Username);
        IsPremium = _auth.Current.IsPremium;
        PlanName = _auth.Current.PlanName;
        PlanUpdatedAt = _auth.Current.PlanUpdatedAt;
        DaysLeft = _auth.Current.DaysLeft;

        OnPropertyChanged(nameof(SessionBadge));
        OnPropertyChanged(nameof(SessionDetails));
        OnPropertyChanged(nameof(HasAccountIssue));
        OnPropertyChanged(nameof(HasSavedCredentials));   // ← aggiunta
    }

    // ============================================================
    //  LOGIN
    // ============================================================

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            StatusMessage = "Inserisci username e password.";
            return;
        }

        IsBusy = true;
        try
        {
            StatusMessage = "Login in corso...";
            var (ok, msg) = await _auth.LoginAsync(Username.Trim(), Password);
            StatusMessage = msg;

            if (ok) _logger.LogInformation("Login effettuato come {User}", Username.Trim());
            else _logger.LogWarning("Login non riuscito: {Message}", msg);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore durante il login");
            StatusMessage = $"Errore durante il login: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Logout()
    {
        _auth.Logout();

        // Ricarica i campi con le credenziali ancora salvate nel DPAPI
        Username = _auth.Current.Username;
        Password = _auth.Current.Password;

        StatusMessage = "Disconnesso. Credenziali ancora salvate — premi Accedi per riconnetterti.";
    }

    [RelayCommand]
    private void ReloadCredentials()
    {
        var u = _auth.Current.Username;
        var p = _auth.Current.Password;

        if (string.IsNullOrEmpty(u) && string.IsNullOrEmpty(p))
        {
            StatusMessage = "Nessuna credenziale salvata.";
            return;
        }

        Username = u ?? string.Empty;
        Password = p ?? string.Empty;
        StatusMessage = "Credenziali caricate dai campi salvati.";
    }

    [RelayCommand]
    private void DeleteCredentials()
    {
        _auth.Logout();          // azzera sessione
        _auth.ClearAll();        // elimina tutto dal DPAPI
        Username = string.Empty;
        Password = string.Empty;
        StatusMessage = "Credenziali eliminate.";
    }

    [RelayCommand]
    private void ShowSavedPassword()
    {
        // Placeholder: ricarica la password dallo store
        Password = _auth.Current.Password;
    }
}