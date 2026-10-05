using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.ViewModels;

/// <summary>
/// Stato condiviso tra i sub-ViewModel della MainWindow.
/// Contiene solo le property che NON appartengono a un singolo tab
/// (es. CreateSeriesFolder è usata sia da Download sia da Organizer).
///
/// I sub-VM ricevono questa istanza via DI/costruttore e bindano
/// direttamente (o tramite wrapper) le property che gli servono.
/// </summary>
public partial class SharedState : ObservableObject
{
    /// <summary>Crea una sottocartella per ogni serie (Download + Organizer).</summary>
    [ObservableProperty] private bool _createSeriesFolder = true;

    /// <summary>Cartella base download (default per Organizer).</summary>
    [ObservableProperty] private string _baseDownloadFolder = string.Empty;

    /// <summary>Path remoto corrente nel browser SFTP (default per Organizer remoto).</summary>
    [ObservableProperty] private string _sftpCurrentRemotePath = "/";

    /// <summary>Path locale corrente nel browser SFTP (default per Upload/Download).</summary>
    [ObservableProperty] private string _sftpCurrentLocalPath = string.Empty;

    /// <summary>True se il client SFTP è connesso (abilita Organizer remoto).</summary>
    [ObservableProperty] private bool _isSftpConnected;

    /// <summary>True = Organizer in modalità Locale, false = Remoto.</summary>
    [ObservableProperty] private bool _organizeModeIsLocal = true;
    // ============================================================
    //  Eventi per la top bar
    // ============================================================

    /// <summary>
    /// Scatena quando cambia il proxy attivo. Il MainVM lo ascolta
    /// per ri-notificare ActiveProxyBadgeText e IsProxyActive (bindati in top bar).
    /// </summary>
    public event EventHandler? ActiveProxyChanged;

    public void NotifyActiveProxyChanged() => ActiveProxyChanged?.Invoke(this, EventArgs.Empty);
    /// <summary>
    /// Callback per richiedere un refresh SFTP al MainVM. L'Organizer lo usa
    /// dopo apply remoto. Il MainVM lo imposta nel costruttore.
    /// </summary>
    public Action? RequestSftpRefresh { get; set; }
    /// <summary>Callback per notificare la top bar (badge SFTP-attivo, ecc.).</summary>
    public Action? NotifyQueueChanged { get; set; }
}