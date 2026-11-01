namespace DownloadManager.Services.Tmdb;

/// <summary>Stagione di una serie su TMDB.</summary>
public sealed record TmdbSeason(int Number, string Name, int EpisodeCount, int? Year, string? PosterPath = null)
{
    /// <summary>Nome "di default" (Stagione 1, Season 1, Specials...): non è un vero nome di stagione.</summary>
    public bool HasOwnName => !string.IsNullOrWhiteSpace(Name)
        && !System.Text.RegularExpressions.Regex.IsMatch(Name.Trim(),
            @"^(stagione|season|temporada|saison|staffel|serie)\s*\d+$|^(specials?|speciali|extra)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

/// <summary>Serie TV (anche anime) su TMDB, con le stagioni e gli episodi già usciti.</summary>
public sealed record TmdbTv(
    int Id, string Name, string OriginalName, int? Year, string Status, bool InProduction,
    IReadOnlyList<TmdbSeason> Seasons, int? LastAiredSeason, int? LastAiredEpisode, int? NextEpisodeSeason, string? PosterPath,
    string? BackdropPath = null)
{
    public TmdbSeason? Season(int n) => Seasons.FirstOrDefault(s => s.Number == n);

    /// <summary>Episodi della stagione già usciti (stagioni passate: tutti; ultima: fino all'ultimo uscito).</summary>
    public int AiredIn(int season)
    {
        var s = Season(season);
        if (s == null) return 0;
        if (LastAiredSeason is not { } last) return s.EpisodeCount;
        if (season < last) return s.EpisodeCount;
        if (season == last) return Math.Min(s.EpisodeCount, LastAiredEpisode ?? s.EpisodeCount);
        return 0;
    }

    /// <summary>La stagione è ancora in onda (ne devono uscire altri episodi).</summary>
    public bool IsAiring(int season) => NextEpisodeSeason == season
        || (InProduction && LastAiredSeason == season && (Season(season)?.EpisodeCount ?? 0) > (LastAiredEpisode ?? 0));

    /// <summary>Stagioni "vere" (senza gli speciali, stagione 0).</summary>
    public IEnumerable<TmdbSeason> RegularSeasons => Seasons.Where(s => s.Number > 0);
}

/// <summary>Film su TMDB.</summary>
public sealed record TmdbMovie(int Id, string Title, string OriginalTitle, int? Year, string? PosterPath, string? BackdropPath = null);

/// <summary>Risultato di una ricerca su TMDB.</summary>
public sealed record TmdbSearchHit(string Kind, int Id, string Name, int? Year, double Popularity);

/// <summary>
/// Immagini del post prese da TMDB, come le mette il forum: copertina (w500) per miniatura e immagine,
/// sfondo a piena risoluzione per "in evidenza".
/// </summary>
public sealed record PostImages(string Thumbnail, string Image, string? Featured)
{
    public const string PosterSize = "w500";
    public static string Url(string path, string size) => $"https://image.tmdb.org/t/p/{size}{path}";

    /// <summary>Copertina (della stagione se c'è, altrimenti della serie o del film) e sfondo.</summary>
    public static PostImages? From(string? posterPath, string? backdropPath)
    {
        if (string.IsNullOrEmpty(posterPath)) return null;
        var poster = Url(posterPath, PosterSize);
        return new PostImages(poster, poster, string.IsNullOrEmpty(backdropPath) ? null : Url(backdropPath, "original"));
    }
}
