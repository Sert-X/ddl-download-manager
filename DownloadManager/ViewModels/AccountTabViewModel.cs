using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Persistence;
using DownloadManager.Services.Streaming.Cb01;
using DownloadManager.Services.Streaming.StreamingCommunity;
using Microsoft.Extensions.Logging;

namespace DownloadManager.ViewModels;

public partial class AccountTabViewModel : ViewModelBase
{
    private static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    private readonly StreamingCommunityAuthService _auth;
    private readonly ILogger<AccountTabViewModel> _logger;
    private readonly IStreamingCommunityService _streaming;

    // --- Maxstream ---
    private readonly MaxstreamAccountRepository _msRepo;
    private readonly MaxstreamService _maxstream;
    private readonly SettingsService _settings;

    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _statusMessage = "Inserisci le credenziali e premi Accedi.";
    [ObservableProperty] private bool _isBusy;

    // Stato post-login SC
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isPremium;
    [ObservableProperty] private string _planName = string.Empty;
    [ObservableProperty] private string _planUpdatedAt = string.Empty;
    [ObservableProperty] private int _daysLeft;

    // --- Maxstream: stessi campi della sezione StreamingCommunity ---
    [ObservableProperty] private string _msUsername = string.Empty;
    [ObservableProperty] private string _msPassword = string.Empty;
    [ObservableProperty] private string _msFlareSolverrUrl = string.Empty;
    [ObservableProperty] private string _msStatusMessage = "Inserisci le credenziali Maxstream e premi Accedi.";
    [ObservableProperty] private bool _isMsBusy;
    [ObservableProperty] private bool _isMsLoggedIn;
    [ObservableProperty] private bool _isMsPremium;
    [ObservableProperty] private DateTime? _msPremiumExpire;
    [ObservableProperty] private DateTime? _msLastCheck;
    [ObservableProperty] private bool _hasMsSavedCredentials;

    public bool HasSavedCredentials =>
        !string.IsNullOrEmpty(_auth.Current.Username) ||
        !string.IsNullOrEmpty(_auth.Current.Password);

    /// <summary>Pallino rosso sulla tab: SC non connesso, oppure Maxstream configurato ma senza premium attivo.</summary>
    public bool HasAccountIssue => !IsLoggedIn || (HasMsSavedCredentials && !(IsMsLoggedIn && IsMsPremium));

    /// <summary>Scadenza SC: il sito dà i giorni rimanenti al momento della verifica.</summary>
    private DateTime? ScExpire => IsPremium && _auth.Current.LastLoginUtc is { } at
        ? at.ToLocalTime().Date.AddDays(DaysLeft)
        : null;

    private string ScExpireText => ScExpire?.ToString("d MMMM yyyy", It) ?? "non indicata";

    private int ScDaysLeft => ScExpire is { } d ? Math.Max(0, (d - DateTime.Today).Days) : 0;

    public string SessionBadge => !IsLoggedIn
        ? "🔒 Non connesso"
        : IsPremium ? (ScExpire != null ? $"💎 Premium — scade il {ScExpireText}" : "💎 Premium")
        : "👤 Free — 720p";

    public string SessionDetails => !IsLoggedIn
        ? "Nessun account configurato."
        : IsPremium
            ? $"Piano: {(string.IsNullOrWhiteSpace(PlanName) ? "Premium" : PlanName)}\nScadenza: {ScExpireText}\nGiorni rimanenti: {ScDaysLeft}" +
              (_auth.Current.LastLoginUtc is { } c ? $"\nUltima verifica: {c.ToLocalTime().ToString("g", It)}" : "")
        : "Account gratuito. Qualità massima 720p.\nAccedi con un account premium per 1080p.";

    /// <summary>Stesso testo del banner di Maxstream, ricavato dallo stato salvato.</summary>
    private string ScStatusFromState() => !IsLoggedIn
        ? "Inserisci le credenziali e premi Accedi."
        : IsPremium ? (ScExpire != null ? $"Premium attivo fino al {ScExpireText}" : "Premium attivo")
        : "Account gratuito: qualità massima 720p";

    private int MsDaysLeft => MsPremiumExpire is { } d ? Math.Max(0, (d.Date - DateTime.Today).Days) : 0;

    /// <summary>Stato salvato da una versione precedente, mai verificato: non va mostrato come "Free".</summary>
    private bool IsMsUnverified => IsMsLoggedIn && MsLastCheck == null;

    private string MsExpireText => MsPremiumExpire?.ToString("d MMMM yyyy", It) ?? "non indicata";

    public string MsSessionBadge => !IsMsLoggedIn
        ? "🔒 Non connesso"
        : IsMsUnverified ? "⏳ Da verificare"
        : IsMsPremium ? (MsPremiumExpire != null ? $"💎 Premium — scade il {MsExpireText}" : "💎 Premium")
        : "👤 Free — download CB01 non disponibili";

    public string MsSessionDetails => !IsMsLoggedIn
        ? "Nessun account configurato."
        : IsMsUnverified ? "Stato non ancora verificato: premi Testa sessione o Accedi."
        : IsMsPremium
            ? $"Piano: Premium\nScadenza: {MsExpireText}\nGiorni rimanenti: {MsDaysLeft}" +
              (MsLastCheck is { } c ? $"\nUltima verifica: {c.ToLocalTime().ToString("g", It)}" : "")
        : MsPremiumExpire is { } exp
            ? $"Premium scaduto il {exp.ToString("d MMMM yyyy", It)}.\nSenza premium Maxstream chiede il captcha e i download da CB01 falliscono."
            : "Account gratuito.\nSenza premium Maxstream chiede il captcha e i download da CB01 falliscono.";

    public AccountTabViewModel(
        StreamingCommunityAuthService auth,
        IStreamingCommunityService streaming,
        MaxstreamAccountRepository msRepo,
        MaxstreamService maxstream,
        SettingsService settings,
        ILogger<AccountTabViewModel> logger)
    {
        _auth = auth;
        _streaming = streaming;
        _msRepo = msRepo;
        _maxstream = maxstream;
        _settings = settings;
        _logger = logger;

        Username = _auth.Current.Username;
        Password = _auth.Current.Password;
        ApplyState();
        StatusMessage = ScStatusFromState();
        _auth.StateChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(ApplyState);

        // Come per Maxstream: all'avvio si rifà il login per leggere piano e scadenza aggiornati
        if (!string.IsNullOrEmpty(_auth.Current.Username) && !string.IsNullOrEmpty(_auth.Current.Password))
        {
            StatusMessage = "Verifica account StreamingCommunity...";
            var (u, pw) = (_auth.Current.Username, _auth.Current.Password);
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                try
                {
                    var (ok, msg) = await _auth.LoginAsync(u, pw);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        ApplyState();
                        StatusMessage = ok ? ScStatusFromState() : msg;
                    });
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[SC-ACCOUNT] Verifica all'avvio fallita"); }
            });
        }

        var msCreds = _msRepo.Load();
        MsUsername = msCreds.Username;
        MsPassword = msCreds.Password;
        MsFlareSolverrUrl = _settings.Current.FlareSolverrUrl ?? string.Empty;
        ApplyMsState(msCreds);
        if (!string.IsNullOrEmpty(msCreds.Username))
            _maxstream.SetCredentials(msCreds.Username, msCreds.Password);

        // Anche i login automatici fatti durante i download aggiornano scadenza e stato
        _maxstream.AccountChanged += info => Avalonia.Threading.Dispatcher.UIThread.Post(() => OnMaxstreamAccountChanged(info));

        // All'avvio si rilegge lo stato reale (premium, scadenza): lo stato salvato può essere vecchio
        if (!string.IsNullOrEmpty(msCreds.Username) && !string.IsNullOrEmpty(msCreds.Password))
        {
            MsStatusMessage = "Verifica account Maxstream...";
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000);
                try { await _maxstream.LoginAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "[MAXSTREAM-ACCOUNT] Verifica all'avvio fallita"); }
            });
        }
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
            StatusMessage = "Verifica sessione StreamingCommunity...";
            var (loggedIn, premium, info) = await _streaming.CheckSessionAsync();
            StatusMessage = loggedIn
                ? $"Sessione valida — {(premium ? ScStatusFromState() : "account gratuito (720p)")}"
                : $"Sessione non valida: premi Accedi. {info}".Trim();
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
        OnPropertyChanged(nameof(HasSavedCredentials));
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
            if (ok) ApplyState();   // lo StateChanged arriva in coda: serve lo stato nuovo subito
            StatusMessage = ok ? ScStatusFromState() : msg;

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
        Password = _auth.Current.Password;
    }

    // ============================================================
    //  MAXSTREAM
    // ============================================================

    private void ApplyMsState(Models.MaxstreamCredentials creds)
    {
        IsMsLoggedIn = creds.LastLoginUtc.HasValue && !string.IsNullOrEmpty(creds.Username);
        // Premium senza data leggibile ("Extend Premium account") resta premium
        IsMsPremium = creds.IsPremium && (creds.PremiumExpire is not { } d || d.Date >= DateTime.Today);
        MsPremiumExpire = creds.PremiumExpire;
        MsLastCheck = creds.LastCheckUtc;
        HasMsSavedCredentials = !string.IsNullOrEmpty(creds.Username) || !string.IsNullOrEmpty(creds.Password);

        OnPropertyChanged(nameof(MsSessionBadge));
        OnPropertyChanged(nameof(MsSessionDetails));
        OnPropertyChanged(nameof(HasAccountIssue));
    }

    private void OnMaxstreamAccountChanged(MaxstreamAccountInfo info)
    {
        var creds = _msRepo.Load();
        if (info.LoggedIn)
        {
            creds.LastLoginUtc = DateTime.UtcNow;
            creds.IsPremium = info.IsPremium;
            creds.PremiumExpire = info.PremiumExpire;
            creds.LastCheckUtc = DateTime.UtcNow;
        }
        else
        {
            creds.LastLoginUtc = null;
        }
        _msRepo.Save(creds);
        ApplyMsState(creds);
        MsStatusMessage = info.Message;
    }

    partial void OnMsFlareSolverrUrlChanged(string value)
    {
        _settings.Current.FlareSolverrUrl = value?.Trim() ?? "";
        _settings.Save();
    }

    private bool ValidateMsFields()
    {
        if (!string.IsNullOrWhiteSpace(MsUsername) && !string.IsNullOrWhiteSpace(MsPassword)) return true;
        MsStatusMessage = "Inserisci username e password Maxstream.";
        return false;
    }

    private void PersistMsCredentials()
    {
        var creds = _msRepo.Load();
        creds.Username = MsUsername.Trim();
        creds.Password = MsPassword;
        _msRepo.Save(creds);
        _maxstream.SetCredentials(creds.Username, creds.Password);
        ApplyMsState(creds);
    }

    [RelayCommand]
    private async Task MsLoginAsync()
    {
        if (IsMsBusy || !ValidateMsFields()) return;

        IsMsBusy = true;
        try
        {
            PersistMsCredentials();
            MsStatusMessage = string.IsNullOrWhiteSpace(MsFlareSolverrUrl)
                ? "Login in corso (senza FlareSolverr: Cloudflare potrebbe bloccare)..."
                : "Login in corso tramite FlareSolverr...";

            var info = await _maxstream.LoginAsync();
            MsStatusMessage = info.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[MAXSTREAM-ACCOUNT] Errore durante il login");
            MsStatusMessage = $"Errore durante il login: {ex.Message}";
        }
        finally
        {
            IsMsBusy = false;
        }
    }

    [RelayCommand]
    private async Task MsTestSessionAsync()
    {
        if (IsMsBusy) return;
        IsMsBusy = true;
        try
        {
            MsStatusMessage = "Verifica sessione Maxstream...";
            var info = await _maxstream.CheckSessionAsync();
            MsStatusMessage = info.LoggedIn ? $"Sessione valida — {info.Message}" : info.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MAXSTREAM-ACCOUNT] Test sessione fallito");
            MsStatusMessage = $"Errore: {ex.Message}";
        }
        finally
        {
            IsMsBusy = false;
        }
    }

    [RelayCommand]
    private void MsSaveCredentials()
    {
        if (!ValidateMsFields()) return;
        PersistMsCredentials();
        MsStatusMessage = "Credenziali Maxstream salvate (cifrate con DPAPI).";
    }

    [RelayCommand]
    private void MsReloadCredentials()
    {
        var creds = _msRepo.Load();
        if (string.IsNullOrEmpty(creds.Username) && string.IsNullOrEmpty(creds.Password))
        {
            MsStatusMessage = "Nessuna credenziale Maxstream salvata.";
            return;
        }
        MsUsername = creds.Username;
        MsPassword = creds.Password;
        MsStatusMessage = "Credenziali caricate dai campi salvati.";
    }

    [RelayCommand]
    private async Task MsLogoutAsync()
    {
        await _maxstream.LogoutAsync();
        MsStatusMessage = "Disconnesso. Credenziali ancora salvate — premi Accedi per riconnetterti.";
    }

    [RelayCommand]
    private async Task MsDeleteCredentialsAsync()
    {
        await _maxstream.LogoutAsync();
        _msRepo.Clear();
        _maxstream.SetCredentials(string.Empty, string.Empty);
        MsUsername = string.Empty;
        MsPassword = string.Empty;
        ApplyMsState(new Models.MaxstreamCredentials());
        MsStatusMessage = "Credenziali Maxstream eliminate.";
    }
}
