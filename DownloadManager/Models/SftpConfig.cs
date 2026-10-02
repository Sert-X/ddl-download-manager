namespace DownloadManager.Models;

public class SftpConfig
{
    public int Id { get; set; }
    public string Name { get; set; } = "Default";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public bool UseKeyAuth { get; set; } = false;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string PrivateKeyPassphrase { get; set; } = string.Empty;

    public string RemoteBasePath { get; set; } = "/";

    /// <summary>
    /// Fingerprint dell'host key SSH (es. "ssh-rsa 2048 xx:xx:xx...").
    /// Se vuota, WinSCP accetta qualsiasi host key.
    /// </summary>
    public string SshHostKeyFingerprint { get; set; } = string.Empty;
}