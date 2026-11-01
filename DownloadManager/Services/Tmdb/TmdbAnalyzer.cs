namespace DownloadManager.Services.Tmdb;

public enum TmdbRowState { Rename, AlreadyOk, Confirm, NotFound, Error }

/// <summary>Risultato dell'analisi di un post: abbinamento TMDB, titolo proposto, controllo episodi.</summary>
public sealed class TmdbAnalysis
{
    public int PostId { get; init; }
    public string CurrentTitle { get; set; } = "";
    public string Url { get; init; } = "";
    public int LinkCount { get; init; }

    public string? Kind { get; set; }              // "tv" / "movie"
    public int? TmdbId { get; set; }
    public int? Season { get; set; }               // null = serie intera (o film)
    public string MatchName { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public int? Year { get; set; }
    public string SeasonLabel { get; set; } = "";
    public int? Total { get; set; }
    public int? Aired { get; set; }
    public bool InCorso { get; set; }
    public string? EpisodeIssue { get; set; }      // "Mancano 2 episodi", "3 link in più"...
    public string? InCorsoNote { get; set; }       // "in corso: 4 episodi usciti non ancora sul forum"
    public string ProposedTitle { get; set; } = "";
    public TmdbRowState State { get; set; }
    public string IdSource { get; set; } = "";     // da dove arriva il codice
    public string SeasonSource { get; set; } = ""; // da dove arriva la stagione
    public List<string> Notes { get; } = new();    // motivi di "da confermare" e avvisi
    public string? PosterPath { get; set; }
    /// <summary>Immagini da mettere nel post se scegli di aggiornarle (stagione, altrimenti serie/film).</summary>
    public PostImages? Images { get; set; }
    /// <summary>Da dove vengono le immagini ("copertina della stagione 2", "copertina della serie"...).</summary>
    public string ImagesSource { get; set; } = "";
    public IReadOnlyList<TmdbSeason> Seasons { get; set; } = Array.Empty<TmdbSeason>();
    public DateTime AnalyzedAt { get; init; } = DateTime.Now;

    public bool HasEpisodeIssue => EpisodeIssue != null;
}

/// <summary>Dati del post da analizzare (presi da Videobox: titolo, indirizzo e link).</summary>
public sealed record TmdbPostInput(int Id, string Title, string Url, int LinkCount, bool LinksGrewRecently);

/// <summary>
/// Abbina un post a TMDB e propone il titolo secondo i pattern. Non scrive niente da nessuna parte.
/// </summary>
public class TmdbAnalyzer
{
    private readonly TmdbClient _tmdb;
    public TmdbAnalyzer(TmdbClient tmdb) => _tmdb = tmdb;

    /// <summary>Fonte del codice/stagione quando li ha scelti l'utente (non vanno "confermati").</summary>
    public const string UserSource = "scelto da te";

    public string SeriesPattern { get; set; } = TitlePattern.DefaultSeries;

    /// <summary>Spazi doppi e agli estremi (alcuni titoli di TMDB ne hanno).</summary>
    private static string Clean(string s) => System.Text.RegularExpressions.Regex.Replace((s ?? "").Replace('’', '\'').Replace('‘', '\''), @"\s+", " ").Trim();
    public string MoviePattern { get; set; } = TitlePattern.DefaultMovie;

    public async Task<TmdbAnalysis> AnalyzeAsync(TmdbPostInput post, PostPageInfo page, CancellationToken ct = default)
    {
        var a = new TmdbAnalysis { PostId = post.Id, CurrentTitle = post.Title, Url = post.Url, LinkCount = post.LinkCount };
        var title = PostTitle.Parse(post.Title);
        try
        {
            // 1) Codice TMDB: dalla pagina del post, altrimenti ricerca per titolo
            string? kind = page.Kind;
            int? id = page.TmdbId;
            a.IdSource = page.Source;
            bool searched = false;
            if (!page.HasId)
            {
                var hit = await SearchAsync(title, post.LinkCount, ct);
                if (hit == null)
                {
                    a.State = TmdbRowState.NotFound;
                    a.Notes.Add("Nessun codice TMDB nella pagina e nessun risultato cercando «" + title.CleanName + "»");
                    return a;
                }
                (kind, id) = (hit.Kind, hit.Id);
                searched = true;
                a.IdSource = $"trovato cercando «{title.CleanName}»";
                a.Notes.Add("Codice non presente nella pagina: abbinamento trovato per titolo, da confermare");
            }
            a.Kind = kind;
            a.TmdbId = id;

            if (kind == "movie") await FillMovieAsync(a, title, id!.Value, ct);
            else await FillTvAsync(a, title, page, post, id!.Value, ct);

            if (a.State is TmdbRowState.NotFound or TmdbRowState.Error) return a;
            if (searched || a.Notes.Any(n => n.StartsWith("!"))) a.State = TmdbRowState.Confirm;
            else a.State = TitlePattern.Same(a.ProposedTitle, post.Title) ? TmdbRowState.AlreadyOk : TmdbRowState.Rename;
            // "!" segna i motivi che richiedono conferma: si tolgono dal testo mostrato
            for (int i = 0; i < a.Notes.Count; i++) a.Notes[i] = a.Notes[i].TrimStart('!');
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            a.State = TmdbRowState.Error;
            a.Notes.Add("Errore: " + ex.Message);
        }
        return a;
    }

    private async Task<TmdbSearchHit?> SearchAsync(PostTitle title, int links, CancellationToken ct)
    {
        if (title.CleanName.Length < 2) return null;
        var hits = await _tmdb.SearchAsync(title.CleanName, title.Year, ct);
        if (hits.Count == 0) return null;
        // Un solo link fa pensare a un film; l'anno uguale pesa molto
        return hits
            .OrderByDescending(h => (title.Year != null && h.Year == title.Year ? 100 : 0)
                                    + (links <= 1 ? (h.Kind == "movie" ? 20 : 0) : (h.Kind == "tv" ? 20 : 0))
                                    + Math.Min(h.Popularity, 50) / 5)
            .First();
    }

    private async Task FillMovieAsync(TmdbAnalysis a, PostTitle title, int id, CancellationToken ct)
    {
        var m = await _tmdb.GetMovieAsync(id, ct);
        if (m == null) { a.State = TmdbRowState.NotFound; a.Notes.Add($"Il codice TMDB film {id} non esiste"); return; }
        a.MatchName = Clean(m.Title);
        a.OriginalName = m.OriginalTitle;
        a.Year = m.Year;
        a.PosterPath = m.PosterPath;
        a.Images = PostImages.From(m.PosterPath, m.BackdropPath);
        a.ImagesSource = "copertina del film";
        a.ProposedTitle = TitlePattern.Apply(MoviePattern, new Dictionary<string, string?>
        {
            ["Titolo"] = Clean(m.Title),
            ["Titolo Originale"] = Clean(m.OriginalTitle),
            ["Anno"] = m.Year?.ToString(),
            ["Lingua"] = title.Language,
            ["Episodi"] = null, ["Totale"] = null, ["Stagione"] = null, ["Stagione N"] = null, ["Numero Stagione"] = null, ["In Corso"] = null,
        });
    }

    private async Task FillTvAsync(TmdbAnalysis a, PostTitle title, PostPageInfo page, TmdbPostInput post, int id, CancellationToken ct)
    {
        var tv = await _tmdb.GetTvAsync(id, ct);
        if (tv == null) { a.State = TmdbRowState.NotFound; a.Notes.Add($"Il codice TMDB serie {id} non esiste"); return; }
        a.MatchName = Clean(tv.Name);
        a.OriginalName = tv.OriginalName;
        a.PosterPath = tv.PosterPath;
        a.Seasons = tv.Seasons;
        var regular = tv.RegularSeasons.ToList();
        bool Exists(int? n) => n is > 0 && tv.Season(n.Value) != null;

        // 2) Stagione: pagina → titolo → unica stagione → numero di episodi. La locandina solo come avviso.
        int? season = null;
        if (title.IsSpecials && tv.Season(0) != null)
        {
            season = 0;
            a.SeasonSource = "speciali (stagione 0 di TMDB), dal titolo";
        }
        else if (page.Source == UserSource && page.Season == 0 && tv.Season(0) != null)
        {
            season = 0;
            a.SeasonSource = "speciali, scelti da te";
        }
        else if (Exists(page.Season))
        {
            season = page.Season;
            bool byUser = page.Source == UserSource;
            a.SeasonSource = byUser ? "stagione scelta da te" : "stagione indicata nella pagina";
            if (!byUser && title.Season is { } ts && ts != season && Exists(ts))
                a.Notes.Add($"!La pagina indica la stagione {season}, il titolo la {ts}");
        }
        else if (Exists(title.Season))
        {
            season = title.Season;
            a.SeasonSource = "stagione dal titolo";
        }
        else if (regular.Count == 1)
        {
            season = regular[0].Number;
            a.SeasonSource = "la serie ha una sola stagione";
            if (title.Season is { } ts1 && ts1 != season)
                a.Notes.Add($"!Il titolo indica la stagione {ts1}, ma su TMDB la serie ha una sola stagione");
        }
        else
        {
            var byCount = regular.Where(s => s.EpisodeCount == post.LinkCount).ToList();
            int all = regular.Sum(s => s.EpisodeCount);
            if (byCount.Count == 1)
            {
                season = byCount[0].Number;
                a.SeasonSource = "stagione dedotta dal numero di episodi";
                a.Notes.Add($"!Stagione dedotta dal numero di episodi ({post.LinkCount}): né la pagina né il titolo la indicano");
            }
            else if (post.LinkCount == all && all > 0)
            {
                a.SeasonSource = "il post contiene tutta la serie";
                a.Notes.Add($"!Il post sembra contenere tutta la serie ({all} episodi in {regular.Count} stagioni)");
            }
            else
            {
                a.SeasonSource = "stagione non determinata";
                a.Notes.Add($"!Stagione non indicata e il numero di link ({post.LinkCount}) non corrisponde a nessuna delle {regular.Count} stagioni");
            }
        }

        a.Season = season;
        var s = season != null ? tv.Season(season.Value) : null;
        // Immagini: la copertina della stagione se c'è, altrimenti quella della serie; sfondo della serie
        a.Images = PostImages.From(s?.PosterPath ?? tv.PosterPath, tv.BackdropPath);
        a.ImagesSource = s?.PosterPath != null ? (s.Number == 0 ? "copertina degli speciali" : $"copertina della stagione {s.Number}") : "copertina della serie";
        if (s?.PosterPath != null) a.PosterPath = s.PosterPath;
        int total = s?.EpisodeCount ?? regular.Sum(x => x.EpisodeCount);
        int aired = season != null ? tv.AiredIn(season.Value) : regular.Sum(x => tv.AiredIn(x.Number));
        a.Total = total > 0 ? total : null;
        a.Aired = aired;
        a.Year = s?.Year ?? tv.Year;
        // Nome della stagione solo se è davvero suo (non "Stagione 1" né uguale al titolo della serie)
        bool ownName = s != null && s.HasOwnName && !Clean(s.Name).Contains(Clean(tv.Name), StringComparison.OrdinalIgnoreCase);
        a.SeasonLabel = s == null ? "" : s.Number == 0 ? "Speciali" : ownName ? $"Stagione {s.Number} - {Clean(s.Name)}" : $"Stagione {s.Number}";
        if (total == 0) a.Notes.Add("!TMDB non indica ancora quanti episodi ha la stagione");

        // 3) "In Corso": meno episodi del totale e lo si sta aggiornando
        bool airing = season != null && tv.IsAiring(season.Value);
        bool fewer = total > 0 && post.LinkCount < total;
        a.InCorso = fewer && (title.SaysInCorso || airing || post.LinksGrewRecently);

        // 4) Episodi
        if (total > 0 && post.LinkCount > total)
            a.EpisodeIssue = $"{post.LinkCount - total} link in più degli episodi su TMDB ({total})";
        else if (fewer && !a.InCorso)
            a.EpisodeIssue = $"{(total - post.LinkCount == 1 ? "Manca 1 episodio" : $"Mancano {total - post.LinkCount} episodi")} (TMDB: {total})";
        // Episodi diversi: può essere un episodio perso o una divisione delle stagioni diversa da
        // TMDB (es. 128 episodi sul forum, 26 nella stagione TMDB). Lo decide l'utente.
        if (a.EpisodeIssue != null)
            a.Notes.Add("!Il numero di episodi non corrisponde a TMDB: controlla che la stagione sia giusta prima di rinominare");
        else if (a.InCorso && post.LinkCount < aired)
            a.InCorsoNote = $"In corso: {aired - post.LinkCount} {(aired - post.LinkCount == 1 ? "episodio già uscito" : "episodi già usciti")} non ancora sul forum";
        else if (a.InCorso)
            a.InCorsoNote = airing ? "In corso: la stagione è ancora in onda" : "In corso";

        a.ProposedTitle = TitlePattern.Apply(SeriesPattern, new Dictionary<string, string?>
        {
            ["Titolo"] = Clean(tv.Name),
            ["Titolo Originale"] = Clean(tv.OriginalName),
            ["Episodi"] = post.LinkCount.ToString(),
            ["Totale"] = total > 0 ? total.ToString() : "?",
            ["Anno"] = a.Year?.ToString(),
            ["Stagione"] = a.SeasonLabel,
            ["Stagione N"] = s == null ? "" : s.Number == 0 ? "Speciali" : $"Stagione {s.Number}",
            ["Numero Stagione"] = s?.Number.ToString(),
            ["Lingua"] = title.Language,
            ["In Corso"] = a.InCorso ? "In Corso" : null,
        });
    }
}
