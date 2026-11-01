using System.Net;
using DownloadManager.Persistence;
using DownloadManager.Services.Videobox;

namespace DownloadManager.Services.Tmdb;

/// <summary>Esito della rinomina di un post.</summary>
public enum RenameResult
{
    /// <summary>Titolo cambiato e verificato: il resto del post è identico.</summary>
    Renamed,
    /// <summary>Non inviato: il post non è più come all'analisi (o non si può rinominare senza rischi).</summary>
    Skipped,
    /// <summary>Errore (es. connessione): il post non risulta cambiato. Si può riprovare.</summary>
    Error,
    /// <summary>Dopo l'invio è cambiato altro oltre al titolo: va fermato tutto e controllato.</summary>
    Critical,
}

public sealed record RenameOutcome(RenameResult Result, string Message, string? NewTitleOnForum = null);

/// <summary>
/// Rinomina dei post su Videobox con tutti i controlli:
/// prima dell'invio si rilegge il post (eliminato? titolo o link cambiati dopo l'analisi?);
/// si annota nel registro; si invia lo stesso post con il solo titolo nuovo (stessi link, stesso
/// ordine, stessa categoria e autore); si rilegge e si verifica che sia cambiato solo il titolo.
/// </summary>
public sealed class TmdbRenamer
{
    /// <summary>Oltre questo numero di link il forum potrebbe scartarne una parte (max_input_vars di PHP).</summary>
    public const int MaxLinksPerRequest = 4990;

    /// <summary>Oltre questa dimensione la copertina non si carica (il forum la rifiuterebbe).</summary>
    public const int MaxThumbnailBytes = 5 * 1024 * 1024;

    private readonly VideoboxApiService _api;
    private readonly TmdbCacheRepository _journal;
    private readonly Func<string, CancellationToken, Task<byte[]>> _fetchImage;

    /// <summary>Per scaricare le immagini: un client a parte, senza la chiave del forum.</summary>
    private static readonly HttpClient ImageClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <param name="fetchImage">Solo per i test: come scaricare un'immagine (di base da internet).</param>
    public TmdbRenamer(VideoboxApiService api, TmdbCacheRepository journal, Func<string, CancellationToken, Task<byte[]>>? fetchImage = null)
    {
        _api = api;
        _journal = journal;
        _fetchImage = fetchImage ?? DownloadImageAsync;
    }

    private static async Task<byte[]> DownloadImageAsync(string url, CancellationToken ct)
    {
        using var resp = await ImageClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxThumbnailBytes) throw new InvalidOperationException("immagine troppo grande");
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length > MaxThumbnailBytes) throw new InvalidOperationException("immagine troppo grande");
        return bytes;
    }

    /// <summary>Larghezza e altezza se i byte sono un'immagine leggibile, altrimenti null.</summary>
    internal static (int W, int H)? ImageSize(byte[] bytes)
    {
        try
        {
            using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(bytes));
            return codec == null || codec.Info.Width <= 0 ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch { return null; }
    }

    /// <summary>Nome del file dall'indirizzo ("…/abc.jpg" → "abc.jpg").</summary>
    private static string FileNameOf(string url)
    {
        var name = Uri.TryCreate(url, UriKind.Absolute, out var u) ? Path.GetFileName(u.AbsolutePath) : "";
        name = new string(name.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_').ToArray());
        return name.Contains('.') ? name : "thumbnail.jpg";
    }

    /// <summary>Miniatura da caricare (scaricata e controllata), o il motivo per cui non si può.</summary>
    private async Task<(VideoboxApiService.ThumbnailUpload? Upload, string? Error)> PrepareThumbnailAsync(string url, CancellationToken ct)
    {
        string? lastError = null;
        foreach (var candidate in LargerVersions(url))
        {
            byte[] bytes;
            try { bytes = await _fetchImage(candidate, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { lastError = $"non riesco a scaricare la copertina ({ex.Message})"; continue; }
            if (ImageSize(bytes) == null) { lastError = "la copertina scaricata non è un'immagine valida"; continue; }
            return (new VideoboxApiService.ThumbnailUpload(FileNameOf(candidate), bytes), null);
        }
        return (null, lastError ?? "copertina non disponibile");
    }

    /// <summary>
    /// Per le copertine TMDB si carica la versione più grande (il forum la rimpicciolisce da solo, e
    /// così non viene sgranata): originale, poi w780, poi quella indicata.
    /// </summary>
    internal static IEnumerable<string> LargerVersions(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"^(https://image\.tmdb\.org/t/p/)[^/]+(/.+)$");
        if (!m.Success) { yield return url; yield break; }
        foreach (var size in new[] { "original", "w780" })
        {
            var u = m.Groups[1].Value + size + m.Groups[2].Value;
            if (u != url) yield return u;
        }
        yield return url;
    }

    /// <summary>Lettura del post; null se non esiste più.</summary>
    private async Task<PostSnapshot?> ReadAsync(int postId, CancellationToken ct)
    {
        try { return PostSnapshot.Parse(await _api.GetVideoJsonAsync(postId, ct)); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return null; }
    }

    /// <summary>
    /// Rinomina un post. <paramref name="expectedTitle"/> e <paramref name="expectedLinks"/> sono quelli
    /// dell'analisi: se sul forum sono diversi il post è stato modificato nel frattempo e si salta.
    /// </summary>
    /// <param name="images">Immagini di TMDB: se c'è, si carica la sua copertina come miniatura del post
    /// (l'API non permette altro). null = la miniatura non si tocca e non si controlla.</param>
    public async Task<RenameOutcome> RenameAsync(string batchId, int postId, string expectedTitle, int expectedLinks,
        string newTitle, CancellationToken ct, PostImages? images = null)
    {
        newTitle = (newTitle ?? "").Trim();
        if (newTitle.Length == 0) return new(RenameResult.Skipped, "titolo proposto vuoto");

        // 1) Il post è ancora come all'analisi?
        var before = await ReadAsync(postId, ct);
        if (before == null) return new(RenameResult.Skipped, "il post non esiste più sul forum");
        var thumbFrom = string.IsNullOrEmpty(images?.Thumbnail) ? null : images!.Thumbnail;
        if (thumbFrom != null && (before.CurrentImages.Thumbnail == thumbFrom
                                  || await _journal.HasThumbnailAsync(postId, thumbFrom, before.CurrentImages.Thumbnail)))
            thumbFrom = null;   // ha già questa miniatura
        if (PostSnapshot.SameTitle(before.Title, newTitle) && thumbFrom == null)
            return new(RenameResult.Skipped, images != null ? "ha già questo titolo e questa miniatura" : "ha già questo titolo", before.Title);
        if (!PostSnapshot.SameTitle(before.Title, expectedTitle))
            return new(RenameResult.Skipped, $"titolo cambiato dopo l'analisi (ora «{WebUtility.HtmlDecode(before.Title)}»): rianalizza");
        if (before.Links.Count != expectedLinks)
            return new(RenameResult.Skipped, $"link cambiati dopo l'analisi ({before.Links.Count} invece di {expectedLinks}): rianalizza");

        var unsafeReason = UnsafeReason(before);
        if (unsafeReason != null) return new(RenameResult.Skipped, unsafeReason);

        VideoboxApiService.ThumbnailUpload? upload = null;
        if (thumbFrom != null)
        {
            var (u, err) = await PrepareThumbnailAsync(thumbFrom, ct);
            if (u == null) return new(RenameResult.Error, err!);
            upload = u;
        }

        // 2) Registro prima dell'invio, poi l'invio
        var entry = await _journal.AddRenameAsync(batchId, postId, before.Title, newTitle,
            upload != null ? System.Text.Json.JsonSerializer.Serialize(before.CurrentImages) : null);
        return await SendAndVerifyAsync(entry, before, newTitle, ct, upload, thumbFrom);
    }

    /// <summary>
    /// Rimette il titolo di prima, solo se il post ha ancora il titolo dato dalla rinomina (se l'hai
    /// cambiato dopo, non si tocca). Stessi controlli della rinomina.
    /// </summary>
    public async Task<RenameOutcome> UndoAsync(TmdbCacheRepository.RenameEntry done, CancellationToken ct)
    {
        var before = await ReadAsync((int)done.PostId, ct);
        if (before == null)
        {
            await _journal.SetRenameStatusAsync(done.Id, "UndoSkipped", "il post non esiste più");
            return new(RenameResult.Skipped, "il post non esiste più sul forum");
        }
        // (se aveva cambiato anche la miniatura si va avanti: c'è da rimettere quella)
        if (PostSnapshot.SameTitle(before.Title, done.OldTitle) && done.OldImages == null)
        {
            await _journal.SetRenameStatusAsync(done.Id, "Undone", "aveva già il titolo di prima");
            return new(RenameResult.Renamed, "aveva già il titolo di prima", before.Title);
        }
        if (!PostSnapshot.SameTitle(before.Title, done.NewTitle))
        {
            await _journal.SetRenameStatusAsync(done.Id, "UndoSkipped", "titolo cambiato dopo la rinomina");
            return new(RenameResult.Skipped, $"titolo cambiato dopo la rinomina (ora «{WebUtility.HtmlDecode(before.Title)}»): non lo tocco");
        }
        var unsafeReason = UnsafeReason(before);
        if (unsafeReason != null) return new(RenameResult.Skipped, unsafeReason);

        // Miniatura di prima: la si riscarica e la si ricarica (se prima non c'era, non si può "togliere")
        VideoboxApiService.ThumbnailUpload? upload = null;
        string? oldThumb = null, thumbNote = null;
        if (done.OldImages != null)
        {
            try { oldThumb = System.Text.Json.JsonSerializer.Deserialize<PostImages>(done.OldImages)?.Thumbnail; } catch { }
            if (string.IsNullOrEmpty(oldThumb)) thumbNote = "prima non aveva una miniatura: resta quella di TMDB";
            else if (oldThumb != before.CurrentImages.Thumbnail)
            {
                var (u, err) = await PrepareThumbnailAsync(oldThumb, ct);
                if (u != null) upload = u;
                else thumbNote = "miniatura di prima non più scaricabile, resta quella di TMDB";
            }
        }
        var undoEntry = await _journal.AddRenameAsync("annulla-" + done.BatchId, (int)done.PostId, before.Title, done.OldTitle);
        var outcome = await SendAndVerifyAsync(undoEntry, before, done.OldTitle, ct, upload, oldThumb);
        if (outcome.Result == RenameResult.Renamed)
        {
            await _journal.SetRenameStatusAsync(done.Id, "Undone", thumbNote ?? "");
            if (upload != null) await _journal.ForgetThumbnailAsync((int)done.PostId);
            if (thumbNote != null) outcome = outcome with { Message = outcome.Message + " (" + thumbNote + ")" };
        }
        return outcome;
    }

    /// <summary>
    /// Rinomine rimaste "in invio" (app chiusa a metà): si guarda sul forum com'è finita.
    /// Restituisce quante erano.
    /// </summary>
    public async Task<int> ResolvePendingAsync(CancellationToken ct)
    {
        var pending = await _journal.GetPendingRenamesAsync();
        foreach (var p in pending)
        {
            var now = await ReadAsync((int)p.PostId, ct);
            if (now == null) await _journal.SetRenameStatusAsync(p.Id, "Failed", "post non più presente");
            else if (PostSnapshot.SameTitle(now.Title, p.NewTitle)) await _journal.SetRenameStatusAsync(p.Id, "Done", "verificata dopo un riavvio");
            else if (PostSnapshot.SameTitle(now.Title, p.OldTitle)) await _journal.SetRenameStatusAsync(p.Id, "Failed", "non applicata (app chiusa durante l'invio)");
            else await _journal.SetRenameStatusAsync(p.Id, "Failed", "titolo diverso da entrambi: controllato a mano?");
        }
        return pending.Count;
    }

    /// <summary>Post che non si possono rinominare senza rischiare di perdere qualcosa.</summary>
    private static string? UnsafeReason(PostSnapshot p)
    {
        if (p.ServersWithLinks > 1) return "link su più server: rinominalo a mano";
        if (p.HasSubtitles) return "ha sottotitoli collegati ai link: rinominalo a mano";
        if (p.Links.Count > MaxLinksPerRequest) return $"{p.Links.Count} link: il forum potrebbe salvarne solo una parte, rinominalo a mano";
        if ((p.LinkServerId ?? p.AnyServerId) == null) return "nessun server video nel post";
        if (p.CategoryId <= 0 || p.AuthorId <= 0) return "categoria o autore non leggibili";
        return null;
    }

    /// <summary>
    /// Dopo il caricamento: la miniatura è cambiata ed è un'immagine integra? null se va bene,
    /// altrimenti cosa non va.
    /// </summary>
    private async Task<string?> CheckUploadedThumbnailAsync(PostSnapshot before, PostSnapshot after, VideoboxApiService.ThumbnailUpload sent)
    {
        var now = after.CurrentImages.Thumbnail;
        if (string.IsNullOrEmpty(now) || now == before.CurrentImages.Thumbnail)
            return "la miniatura non è cambiata (il forum non ha accettato il file?)";
        byte[] bytes;
        try { bytes = await _fetchImage(now, CancellationToken.None); }
        catch (Exception ex) { return $"la nuova miniatura non si apre ({ex.Message})"; }
        var got = ImageSize(bytes);
        if (got == null) return "la nuova miniatura è un file rovinato (il forum ha salvato male l'immagine)";
        var want = ImageSize(sent.Content);
        // Il forum può ridimensionarla: conta che le proporzioni siano quelle
        if (want is { } w && Math.Abs((double)got.Value.W / got.Value.H - (double)w.W / w.H) > 0.05)
            return $"la nuova miniatura ha proporzioni diverse ({got.Value.W}×{got.Value.H} invece di {w.W}×{w.H})";
        return null;
    }

    private async Task<RenameOutcome> SendAndVerifyAsync(long entryId, PostSnapshot before, string newTitle, CancellationToken ct,
        VideoboxApiService.ThumbnailUpload? upload = null, string? thumbFrom = null)
    {
        Exception? sendError = null;
        try
        {
            await _api.UpdateVideoUrlsAsync(before.Id, before.LinkServerId ?? before.AnyServerId!, newTitle,
                before.CategoryId, before.AuthorId, before.Links.ToList(), upload);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { sendError = new OperationCanceledException(); }
        catch (Exception ex) { sendError = ex; }

        // 3) Verifica: si rilegge sempre, anche dopo un errore (l'invio può essere arrivato lo stesso)
        PostSnapshot? after;
        try { after = await ReadAsync(before.Id, CancellationToken.None); }
        catch (Exception ex)
        {
            // Non si sa com'è andata: resta "Pending" e si verifica alla prossima rinomina o annullamento
            return new(RenameResult.Error, $"invio {(sendError == null ? "fatto" : "forse non arrivato")}, ma non riesco a rileggere il post ({ex.Message}): verrà controllato alla prossima occasione");
        }
        if (after == null)
        {
            await _journal.SetRenameStatusAsync(entryId, "Failed", "post sparito dopo l'invio");
            return new(RenameResult.Critical, "il post non risulta più sul forum dopo l'invio");
        }

        // Le immagini non fanno parte dei controlli "non deve cambiare": il forum le aggiorna da TMDB per conto
        // suo. Se abbiamo caricato la miniatura, si controlla invece che sia cambiata e integra.
        var diff = before.DifferencesExceptTitle(after, ignoreImages: true);
        if (upload != null && sendError == null)
        {
            var thumbProblem = await CheckUploadedThumbnailAsync(before, after, upload);
            if (thumbProblem != null) diff.Add(thumbProblem);
            else if (thumbFrom != null) await _journal.SaveThumbnailAsync(before.Id, thumbFrom, after.CurrentImages.Thumbnail);
        }
        bool titleOk = PostSnapshot.SameTitle(after.Title, newTitle);
        if (diff.Count > 0)
        {
            var what = string.Join("; ", diff);
            var detail = before.DetailOfChanges(after, ignoreImages: true);
            await _journal.SetRenameStatusAsync(entryId, titleOk ? "Done" : "Failed",
                "ATTENZIONE, cambiato anche: " + what + (detail.Length > 0 ? " — " + detail : ""));
            return new(RenameResult.Critical, "dopo l'invio è cambiato anche: " + what, after.Title);
        }
        if (titleOk)
        {
            await _journal.SetRenameStatusAsync(entryId, "Done");
            return new(RenameResult.Renamed, "rinominato e verificato", after.Title);
        }
        if (PostSnapshot.SameTitle(after.Title, before.Title))
        {
            var why = sendError?.Message ?? "il forum non ha applicato il titolo";
            await _journal.SetRenameStatusAsync(entryId, "Failed", why);
            return new(RenameResult.Error, why);
        }
        await _journal.SetRenameStatusAsync(entryId, "Done", $"il forum l'ha salvato come «{after.Title}»");
        return new(RenameResult.Renamed, $"salvato dal forum come «{WebUtility.HtmlDecode(after.Title)}»", after.Title);
    }
}
