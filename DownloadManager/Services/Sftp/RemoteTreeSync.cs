using System.Globalization;
using DownloadManager.Models;

namespace DownloadManager.Services.Sftp;

/// <summary>
/// Elenco completo di file e cartelle sotto una cartella del server, chiesto al server con un solo
/// comando ("find"): un paio di secondi anche per migliaia di file, invece di centinaia di letture
/// SFTP. Serve ad allineare gli esiti dei controlli alle modifiche fatte da fuori (WinSCP, altri PC):
/// file eliminati, sostituiti, rinominati o spostati anche in sottocartelle mai aperte.
/// </summary>
public static class RemoteTreeSync
{
    public sealed record RemoteItem(string Path, bool IsDirectory, long Size, DateTime Modified);

    /// <summary>Oltre questo numero di righe non si allinea (cartella troppo grande).</summary>
    public const int MaxItems = 200_000;

    /// <returns>Elenco (cartelle nascoste escluse), oppure null se il server non accetta comandi o la cartella è troppo grande.</returns>
    public static async Task<List<RemoteItem>?> ListTreeAsync(SftpConfig server, string root, CancellationToken ct)
    {
        var path = (root ?? "").TrimEnd('/');
        if (path.Length == 0 || !path.StartsWith('/')) return null;   // mai tutto il disco del server

        try
        {
            using var ssh = await SshNetConnector.ConnectSshAsync(server, ct);
            var quoted = "'" + path.Replace("'", "'\\''") + "'";
            // tipo, dimensione, data (secondi dal 1970, UTC), percorso. Si saltano solo le CARTELLE
            // nascoste (".thumbnails"...): i file che iniziano con un punto ("...E continuavano a
            // chiamarlo Trinità.mp4") sono file veri e vanno elencati, altrimenti i loro esiti
            // verrebbero tolti come se il file non ci fosse più.
            var command = "find " + quoted + " -mindepth 1 -type d -name '.*' -prune -o -printf '%y\\t%s\\t%T@\\t%p\\n' | head -n " + (MaxItems + 1);
            using var cmd = ssh.CreateCommand(command);
            cmd.CommandTimeout = TimeSpan.FromMinutes(2);
            using var reg = ct.Register(() => { try { ssh.Disconnect(); } catch { } });
            var output = await Task.Run(() => cmd.Execute(), ct);
            ct.ThrowIfCancellationRequested();
            if (cmd.ExitStatus is not 0) return null;

            var items = new List<RemoteItem>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t', 4);
                if (parts.Length < 4) continue;
                if (!long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size)) continue;
                if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var epoch)) continue;
                var modified = DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000)).LocalDateTime;
                items.Add(new RemoteItem(parts[3], parts[0] == "d", size, modified));
            }
            return items.Count > MaxItems ? null : items;
        }
        catch (OperationCanceledException) { throw; }
        catch when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch
        {
            return null;   // niente shell o comando non permesso
        }
    }
}
