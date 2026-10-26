using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DownloadManager.Services.Download;

/// <summary>
/// Ordine delle tracce audio nei .mp4 creati dall'app: l'italiano sempre per primo e predefinito
/// (i player web come Plyr riproducono solo la prima traccia). Le altre restano, nell'ordine originale.
/// </summary>
public static class AudioTracks
{
    private static readonly Regex RxItalianTitle = new(@"\b(ita|italian[oa]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Indici delle tracce audio (0:a:N) nell'ordine da usare: italiano per primo.</summary>
    public static async Task<List<int>> ItalianFirstOrderAsync(string ffmpegPath, string file, CancellationToken ct)
    {
        var tracks = await ProbeAsync(ffmpegPath, file, ct);
        var order = Enumerable.Range(0, tracks.Count).ToList();
        var ita = tracks.FindIndex(IsItalian);
        if (ita > 0) { order.Remove(ita); order.Insert(0, ita); }
        return order;
    }

    /// <summary>True se il file ha una traccia italiana che non è la prima.</summary>
    public static async Task<bool> NeedsReorderAsync(string ffmpegPath, string file, CancellationToken ct)
    {
        var order = await ItalianFirstOrderAsync(ffmpegPath, file, ct);
        return order.Count > 0 && order[0] != 0;
    }

    /// <summary>
    /// Argomenti ffmpeg per mappare l'audio nell'ordine dato e rendere predefinita solo la prima traccia.
    /// Senza informazioni (ffprobe mancante) si tengono tutte le tracce come sono.
    /// </summary>
    public static string[] MapArgs(IReadOnlyList<int> order)
    {
        if (order.Count == 0) return new[] { "-map", "0:a?" };
        var args = new List<string>();
        foreach (var i in order) args.AddRange(new[] { "-map", $"0:a:{i}" });
        for (int i = 0; i < order.Count; i++)
            args.AddRange(new[] { $"-disposition:a:{i}", i == 0 ? "default" : "0" });
        return args.ToArray();
    }

    private static bool IsItalian((string Language, string Title) t) =>
        t.Language is "ita" or "it" or "italian" || RxItalianTitle.IsMatch(t.Title);

    private static async Task<List<(string Language, string Title)>> ProbeAsync(string ffmpegPath, string file, CancellationToken ct)
    {
        var list = new List<(string, string)>();
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
        if (!File.Exists(ffprobe)) return list;

        var psi = new ProcessStartInfo { FileName = ffprobe, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", "-select_streams", "a", "-show_entries", "stream=index:stream_tags=language,title", "-of", "json", file })
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var stderr = p.StandardError.ReadToEndAsync(ct);
            var json = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            await stderr;
            if (p.ExitCode != 0) return list;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("streams", out var streams)) return list;
            foreach (var s in streams.EnumerateArray())
            {
                string lang = "", title = "";
                if (s.TryGetProperty("tags", out var tags))
                {
                    foreach (var tag in tags.EnumerateObject())
                    {
                        if (tag.Name.Equals("language", StringComparison.OrdinalIgnoreCase)) lang = (tag.Value.GetString() ?? "").ToLowerInvariant();
                        else if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) title = tag.Value.GetString() ?? "";
                    }
                }
                list.Add((lang, title));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { list.Clear(); }
        return list;
    }
}
