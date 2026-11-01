using System.Text.RegularExpressions;

namespace DownloadManager.Services.Tmdb;

/// <summary>
/// Quello che si ricava dal titolo attuale di un post: lingua, "in corso", numero di stagione, anno,
/// nome pulito (per la ricerca su TMDB quando la pagina non ha il codice).
/// </summary>
public sealed record PostTitle(string Raw, string CleanName, string Language, bool SaysInCorso, int? Season, int? Year, bool IsSpecials = false)
{
    private static readonly Regex Specials = new(@"\b(specials?|speciali|oav|ova)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SubIta = new(@"\bsub[\s\-_]*ita\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex InCorso = new(@"\bin[\s\-_]*corso\b|\bongoing\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YearRx = new(@"\((19\d{2}|20\d{2})\)|\b(19\d{2}|20\d{2})\b", RegexOptions.Compiled);

    // Stagione nel titolo: "Stagione 2", "Season 2", "2° Serie", "[1°Serie]", "S02", "Parte 2"
    private static readonly Regex[] SeasonRx =
    {
        new(@"\b(?:stagione|season|serie)\s*(\d{1,2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(\d{1,2})\s*[°ª]\s*(?:stagione|serie|season)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bS(\d{1,2})\b(?!\s*E\d)", RegexOptions.Compiled),
        // numero subito prima di "[x/y]" o della lingua: "Yui - Ragazza Virtuale 2 [26/26]"
        new(@"\s(\d{1,2})\s*(?=\[\d+\s*/\s*\d+\]|\(\d{4}\)|\s+(?:sub\s*)?ita\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    public static PostTitle Parse(string title)
    {
        var t = title ?? "";
        var lang = SubIta.IsMatch(t) ? "Sub ITA" : "ITA";
        bool inCorso = InCorso.IsMatch(t);

        int? season = null;
        foreach (var rx in SeasonRx)
        {
            var m = rx.Match(t);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var s) && s is > 0 and < 60) { season = s; break; }
        }

        int? year = null;
        var ym = YearRx.Match(t);
        if (ym.Success) year = int.Parse(ym.Groups[1].Success ? ym.Groups[1].Value : ym.Groups[2].Value);

        // Nome pulito per la ricerca: via [x/y], anni, lingua, "streaming", stagione, parentesi vuote
        var clean = Regex.Replace(t, @"\[[^\]]*\]|\([^)]*\)", " ");
        clean = Regex.Replace(clean, @"\b(sub[\s\-_]*)?ita\b|\bstreaming\b|\bin[\s\-_]*corso\b|\b(stagione|season|serie)\s*\d+\b|\b\d+\s*[°ª]\s*(stagione|serie)\b", " ", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\s+\d{1,2}\s*$", " ");
        clean = Regex.Replace(clean, @"[\s\-–:]+$", "").Trim();
        clean = Regex.Replace(clean, @"\s{2,}", " ");

        clean = Regex.Replace(clean, @"\b(specials?|speciali)\b", " ", RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"\s{2,}", " ");
        return new PostTitle(t, clean, lang, inCorso, season, year, Specials.IsMatch(t));
    }
}

/// <summary>Costruzione del titolo da un pattern: i pezzi vuoti spariscono insieme alle parentesi.</summary>
public static class TitlePattern
{
    public const string DefaultSeries = "{Titolo} - [{Episodi}/{Totale}] - ({Anno}) - [{Stagione}] {Lingua} Streaming ({In Corso})";
    public const string DefaultMovie = "{Titolo} ({Anno}) {Lingua} Streaming";

    public static string Apply(string pattern, IReadOnlyDictionary<string, string?> values)
    {
        var s = Regex.Replace(pattern, @"\{([^{}]+)\}", m =>
            values.TryGetValue(m.Groups[1].Value.Trim(), out var v) ? (v ?? "") : m.Value, RegexOptions.None);

        // Gruppi rimasti vuoti: "()", "[]", "[/]" (senza episodi), e i trattini che li separavano
        for (int i = 0; i < 3; i++)
        {
            s = Regex.Replace(s, @"\(\s*\)|\[\s*\]|\[\s*/\s*\]", "");
            s = Regex.Replace(s, @"\s*-\s*(?=-)", " ");          // "- -" → "-"
        }
        s = Regex.Replace(s, @"(\s+-\s*)+(?=\s*$)", "");          // trattino finale
        s = Regex.Replace(s, @"^\s*-\s*", "");                     // trattino iniziale
        s = Regex.Replace(s, @"\s+-\s+-\s+", " - ");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s;
    }

    /// <summary>Confronto tra titoli che ignora solo gli spazi doppi (maiuscole comprese: "Sub ita" va corretto).</summary>
    public static bool Same(string a, string b) =>
        string.Equals(Regex.Replace(a ?? "", @"\s+", " ").Trim(), Regex.Replace(b ?? "", @"\s+", " ").Trim(), StringComparison.Ordinal);
}
