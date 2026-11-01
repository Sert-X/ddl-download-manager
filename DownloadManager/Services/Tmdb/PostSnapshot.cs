using System.Net;
using System.Text.Json;

namespace DownloadManager.Services.Tmdb;

/// <summary>
/// Un post di Videobox letto dall'API, con quello che serve alla rinomina: titolo, categoria, autore,
/// link (nell'ordine del post) e i campi che la rinomina non deve toccare. Confrontando l'istantanea
/// di prima e di dopo l'invio si verifica che sia cambiato solo il titolo.
/// </summary>
public sealed class PostSnapshot
{
    public int Id { get; private init; }
    public string Title { get; private init; } = "";
    public int CategoryId { get; private init; }
    public int AuthorId { get; private init; }

    /// <summary>Server con i link (null se il post non ne ha).</summary>
    public string? LinkServerId { get; private init; }
    /// <summary>Un server qualsiasi del post (per i post senza link).</summary>
    public string? AnyServerId { get; private init; }
    public int ServersWithLinks { get; private init; }
    /// <summary>Link nell'ordine del post (posizione, poi id).</summary>
    public IReadOnlyList<string> Links { get; private init; } = Array.Empty<string>();
    /// <summary>Almeno un link ha i sottotitoli collegati: rimandando solo gli indirizzi si perderebbero.</summary>
    public bool HasSubtitles { get; private init; }
    /// <summary>Immagini del post (miniatura, immagine, in evidenza), "" se mancano.</summary>
    public PostImages CurrentImages { get; private init; } = new("", "", null);

    /// <summary>Campi che la rinomina non deve cambiare, come testo JSON (prefisso, tag, immagini, visibilità...).</summary>
    private Dictionary<string, string> Protected { get; init; } = new();
    private static readonly string[] ProtectedFields = { "prefix", "tags", "images", "locked", "hidden", "pinned", "featured" };

    public static PostSnapshot Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var p = doc.RootElement;

        string? linkServer = null, anyServer = null;
        int serversWithLinks = 0;
        bool subs = false;
        var links = new List<string>();
        if (p.TryGetProperty("videos", out var videos) && videos.ValueKind == JsonValueKind.Object)
        {
            foreach (var srv in videos.EnumerateObject())
            {
                var srvId = srv.Value.TryGetProperty("id", out var sid) ? sid.ToString() : srv.Name;
                anyServer ??= srvId;
                if (!srv.Value.TryGetProperty("online", out var online) || online.ValueKind != JsonValueKind.Object) continue;
                var eps = online.EnumerateObject().Select(e => e.Value).ToList();
                if (eps.Count == 0) continue;
                serversWithLinks++;
                if (linkServer != null) continue;   // più server con link: la rinomina li salterà
                linkServer = srvId;
                foreach (var ep in eps
                             .OrderBy(e => Num(e, "vbo_position"))
                             .ThenBy(e => Num(e, "vbo_id")))
                {
                    links.Add(ep.TryGetProperty("vbo_url", out var u) ? u.GetString() ?? "" : "");
                    if (ep.TryGetProperty("vbo_sub", out var sub) && sub.ValueKind == JsonValueKind.String && sub.GetString()!.Trim().Length > 0)
                        subs = true;
                }
            }
        }

        string Img(string name) => p.TryGetProperty("images", out var im) && im.ValueKind == JsonValueKind.Object
                                   && im.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var images = new PostImages(Img("thumbnail"), Img("image"), Img("featured") is { Length: > 0 } feat ? feat : null);

        var prot = new Dictionary<string, string>();
        foreach (var f in ProtectedFields)
            prot[f] = p.TryGetProperty(f, out var v) ? v.GetRawText() : "(assente)";

        return new PostSnapshot
        {
            Id = p.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
            Title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            CategoryId = p.TryGetProperty("category", out var c) ? CategoryOf(c) : 0,
            AuthorId = p.TryGetProperty("author", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("id", out var aid) ? aid.GetInt32() : 0,
            LinkServerId = linkServer,
            AnyServerId = anyServer,
            ServersWithLinks = serversWithLinks,
            Links = links,
            HasSubtitles = subs,
            CurrentImages = images,
            Protected = prot,
        };
    }

    private static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : long.MaxValue;

    private static int CategoryOf(JsonElement c)
    {
        if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("id", out var id)) return id.GetInt32();
        if (c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0 && c[0].ValueKind == JsonValueKind.Object && c[0].TryGetProperty("id", out var id0)) return id0.GetInt32();
        if (c.ValueKind == JsonValueKind.Number) return c.GetInt32();
        return 0;
    }

    /// <summary>Titoli uguali come li mostra il forum (spazi ed entità HTML non contano).</summary>
    public static bool SameTitle(string a, string b) =>
        TitlePattern.Same(WebUtility.HtmlDecode(a ?? ""), WebUtility.HtmlDecode(b ?? ""));

    /// <summary>Tutto quello che è cambiato tra due letture, titolo escluso (vuoto = solo il titolo).</summary>
    /// <param name="ignoreImages">Le immagini non si controllano (il forum le può aggiornare da TMDB per conto suo).</param>
    public List<string> DifferencesExceptTitle(PostSnapshot after, bool ignoreImages = false)
    {
        var diff = new List<string>();
        if (after.CategoryId != CategoryId) diff.Add($"categoria {CategoryId} → {after.CategoryId}");
        if (after.AuthorId != AuthorId) diff.Add($"autore {AuthorId} → {after.AuthorId}");
        if (!after.Links.SequenceEqual(Links))
        {
            int lost = Links.Except(after.Links).Count(), added = after.Links.Except(Links).Count();
            diff.Add(lost == 0 && added == 0
                ? $"ordine dei link cambiato ({Links.Count} link)"
                : $"link: {Links.Count} prima, {after.Links.Count} dopo ({lost} spariti, {added} nuovi)");
        }
        if (after.HasSubtitles != HasSubtitles) diff.Add("sottotitoli dei link cambiati");
        foreach (var (field, before) in Protected)
            if (!(ignoreImages && field == "images") && after.Protected.GetValueOrDefault(field) != before) diff.Add($"campo «{field}» cambiato");
        return diff;
    }

    /// <summary>
    /// Valori di prima e di dopo dei campi protetti cambiati (per il registro: servono a ripristinarli a mano).
    /// </summary>
    public string DetailOfChanges(PostSnapshot after, bool ignoreImages = false)
    {
        static string Short(string v) => v.Length > 600 ? v[..600] + "…" : v;
        return string.Join(" | ", Protected
            .Where(kv => !(ignoreImages && kv.Key == "images") && after.Protected.GetValueOrDefault(kv.Key) != kv.Value)
            .Select(kv => $"{kv.Key}: PRIMA {Short(kv.Value)} DOPO {Short(after.Protected.GetValueOrDefault(kv.Key) ?? "")}"));
    }
}
