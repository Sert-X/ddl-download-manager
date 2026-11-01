using DownloadManager.Models;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace DownloadManager.Services.Sftp;

/// <summary>
/// Elimina cartelle sul server con una connessione SSH.NET a parte, non con la sessione WinSCP
/// del browser.
///
/// Prima con WinSCP una sola operazione cancellava tutta la cartella: con migliaia di file durava
/// più dei 60 secondi di timeout, WinSCP chiudeva la sessione e l'SFTP smetteva di rispondere
/// finché non ci si riconnetteva. Ora:
/// 1. si chiede al server di farlo lui ("rm -rf"): pochi secondi anche per migliaia di file;
/// 2. se il server non accetta comandi, si cancella file per file via SFTP, controllando lo stop
///    a ogni file e dicendo quanti ne mancano.
/// Il browser intanto resta libero.
/// </summary>
public static class RemoteDeleter
{
    /// <param name="progress">Avanzamento leggibile (es. "1.234 file eliminati").</param>
    public static async Task DeleteDirectoryAsync(SftpConfig server, string remotePath,
        IProgress<string>? progress, CancellationToken ct)
    {
        var path = CheckPath(remotePath);

        using var sftp = await SshNetConnector.ConnectAsync(server, ct);
        var attrs = await Task.Run(() => sftp.GetAttributes(path), ct);
        if (!attrs.IsDirectory)
            throw new InvalidOperationException($"'{path}' non è una cartella.");

        // 1. Il server cancella da sé
        progress?.Report("eliminazione sul server...");
        if (await TryServerRemoveAsync(server, path, ct) && !await ExistsAsync(sftp, path, ct))
            return;

        // 2. File per file
        ct.ThrowIfCancellationRequested();
        int deleted = 0;
        await Task.Run(() => DeleteTree(sftp, path, ref deleted, progress, ct), ct);
    }

    /// <summary>Percorso assoluto, senza "..", mai la radice: "rm -rf" non deve poter sbagliare bersaglio.</summary>
    private static string CheckPath(string remotePath)
    {
        var path = (remotePath ?? "").Trim().TrimEnd('/');
        if (!path.StartsWith('/') || path.Length < 2)
            throw new InvalidOperationException($"Percorso non valido per l'eliminazione: '{remotePath}'");
        if (path.Split('/').Any(seg => seg == ".." || seg == "."))
            throw new InvalidOperationException($"Percorso non valido per l'eliminazione: '{remotePath}'");
        return path;
    }

    private static async Task<bool> TryServerRemoveAsync(SftpConfig server, string path, CancellationToken ct)
    {
        try
        {
            using var ssh = await SshNetConnector.ConnectSshAsync(server, ct);
            var quoted = "'" + path.Replace("'", "'\\''") + "'";
            using var cmd = ssh.CreateCommand($"rm -rf -- {quoted}");
            cmd.CommandTimeout = TimeSpan.FromMinutes(30);
            // Lo stop chiude la connessione: il comando si interrompe (o finisce da solo poco dopo)
            using var reg = ct.Register(() => { try { ssh.Disconnect(); } catch { } });
            await Task.Run(() => cmd.Execute(), ct);
            ct.ThrowIfCancellationRequested();
            return cmd.ExitStatus == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch
        {
            // Niente shell sul server (solo SFTP) o comando non permesso: si va file per file
            return false;
        }
    }

    private static async Task<bool> ExistsAsync(SftpClient sftp, string path, CancellationToken ct)
    {
        try { return await Task.Run(() => sftp.Exists(path), ct); }
        catch (OperationCanceledException) { throw; }
        catch { return true; }
    }

    private static void DeleteTree(SftpClient sftp, string dir, ref int deleted, IProgress<string>? progress, CancellationToken ct)
    {
        foreach (ISftpFile f in sftp.ListDirectory(dir))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Name is "." or "..") continue;
            if (f.IsDirectory && !f.IsSymbolicLink)
                DeleteTree(sftp, f.FullName, ref deleted, progress, ct);
            else
            {
                sftp.DeleteFile(f.FullName);
                deleted++;
                if (deleted % 25 == 0) progress?.Report($"{deleted:N0} file eliminati");
            }
        }
        ct.ThrowIfCancellationRequested();
        sftp.DeleteDirectory(dir);
    }
}
