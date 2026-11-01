using DownloadManager.Models;
using Renci.SshNet;

namespace DownloadManager.Services.Sftp;

/// <summary>
/// Apre una connessione SSH.NET (SftpClient) con i dati di un profilo SFTP: password (anche come
/// "keyboard-interactive") oppure chiave privata. Usata dove serve leggere pezzi di file con seek:
/// verifica dei video e anteprima.
/// </summary>
public static class SshNetConnector
{
    /// <summary>Dati di connessione dal profilo: chiave privata, oppure password (anche come "keyboard-interactive").</summary>
    public static ConnectionInfo BuildConnectionInfo(SftpConfig cfg)
    {
        var methods = new List<AuthenticationMethod>();
        if (cfg.UseKeyAuth && !string.IsNullOrEmpty(cfg.PrivateKeyPath))
        {
            var key = string.IsNullOrEmpty(cfg.PrivateKeyPassphrase)
                ? new PrivateKeyFile(cfg.PrivateKeyPath)
                : new PrivateKeyFile(cfg.PrivateKeyPath, cfg.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(cfg.Username, key));
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(cfg.Username, cfg.Password ?? ""));
            // Alcuni server accettano la password solo come "keyboard-interactive"
            var ki = new KeyboardInteractiveAuthenticationMethod(cfg.Username);
            ki.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) p.Response = cfg.Password ?? "";
            };
            methods.Add(ki);
        }

        return new ConnectionInfo(cfg.Host, cfg.Port, cfg.Username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    /// <summary>Client per leggere e scrivere file (SFTP).</summary>
    public static Task<SftpClient> ConnectAsync(SftpConfig cfg, CancellationToken ct = default)
        => OpenAsync(new SftpClient(BuildConnectionInfo(cfg))
        {
            BufferSize = 256 * 1024,
            OperationTimeout = TimeSpan.FromSeconds(60)
        }, ct);

    /// <summary>Client per eseguire comandi sul server (SSH).</summary>
    public static Task<SshClient> ConnectSshAsync(SftpConfig cfg, CancellationToken ct = default)
        => OpenAsync(new SshClient(BuildConnectionInfo(cfg)), ct);

    private static async Task<T> OpenAsync<T>(T client, CancellationToken ct) where T : BaseClient
    {
        try
        {
            // Connect è bloccante: annullando si chiude il client e la connessione si interrompe
            using var reg = ct.Register(() => { try { client.Dispose(); } catch { } });
            await Task.Run(client.Connect, ct);
            ct.ThrowIfCancellationRequested();
            return client;
        }
        catch
        {
            try { client.Dispose(); } catch { }
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            throw;
        }
    }
}
