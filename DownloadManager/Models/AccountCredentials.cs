namespace DownloadManager.Models;

public class AccountCredentials
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Info account premium dopo il login (popolate dall'auth).</summary>
    public bool IsPremium { get; set; }
    public string PlanName { get; set; } = string.Empty;
    public string PlanUpdatedAt { get; set; } = string.Empty;
    public int DaysLeft { get; set; }

    /// <summary>Cookies di sessione serializzati (name=value; name2=value2).</summary>
    public string SessionCookies { get; set; } = string.Empty;

    /// <summary>Timestamp dell'ultimo login riuscito.</summary>
    public DateTime? LastLoginUtc { get; set; }
}