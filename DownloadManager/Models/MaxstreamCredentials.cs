namespace DownloadManager.Models;

public class MaxstreamCredentials
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public DateTime? LastLoginUtc { get; set; }

    // Ultimo stato letto dalla pagina account (mostrato all'avvio, aggiornato a ogni login/verifica)
    public bool IsPremium { get; set; }
    public DateTime? PremiumExpire { get; set; }
    public DateTime? LastCheckUtc { get; set; }
}
