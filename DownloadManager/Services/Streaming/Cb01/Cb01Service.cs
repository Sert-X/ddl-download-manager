using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using DownloadManager.Models;
using DownloadManager.Services.Domains;
using DownloadManager.Services.Proxy;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Streaming.Cb01;

/// <summary>Un link trovato nel post (Maxstream, Mixdrop...), con la sezione in cui era scritto.</summary>
public record Cb01Link(string Url, string Label, string Section, bool DeclaredHd, bool IsSub);

/// <summary>
/// Provider per cb01: cerca film e serie TV, raccoglie i link del post e scarica da Maxstream.
/// I post sono scritti a mano e capita che HD/SD o Maxstream/Mixdrop siano invertiti: per questo
/// non ci si fida delle etichette, si provano i link e si sceglie il file reale migliore.
/// Le pagine cb01 sono HTML semplice, senza Playwright.
/// </summary>
public class Cb01Service : ICb01Service
{
    private readonly ILogger<Cb01Service> _logger;
    private readonly LinkResolver _linkResolver;
    private readonly HttpClient _http;
    private readonly DomainResolver _domains;
    private readonly MaxstreamService _maxstream;

    // Pattern per estrarre le parti delle pagine cb01
    private static readonly Regex RxCard = new(
        @"<div class=""card mp-post horizontal"">(.*?)</div>\s*</div>\s*</div>\s*</div>",
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex RxCardTitle = new(
        @"card-title[^>]*><a\s+href=""([^""]+)"">([^<]+)</a>",
        RegexOptions.Compiled);
    private static readonly Regex RxCardImage = new(
        @"<img[^>]+src=""([^""]+)""",
        RegexOptions.Compiled);
    private static readonly Regex RxYear = new(
        @"\((\d{4})\)",
        RegexOptions.Compiled);

    // Intestazione di una tabella cbtable: "Streaming:", "Streaming HD:", "Download HD:", "Streaming 3D:"
    private static readonly Regex RxSectionHeader = new(
        @"\b(Streaming|Download)\s*(HD|3D)?\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxAnchor = new(
        @"<a\s+[^>]*?href=""([^""]+)""[^>]*>\s*([^<]*?)\s*</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Serie TV: un accordion "sp-wrap" per sezione (intestazione sp-head + corpo sp-body)
    private static readonly Regex RxSpoiler = new(
        @"<div class=""sp-head[^""]*""[^>]*>(.*?)</div>\s*<div class=""sp-body[^""]*"">(.*)",
        RegexOptions.Compiled | RegexOptions.Singleline);
    // "STAGIONE 1 - ITA", "STAGIONE 1 A 37 - ITA - HD"; deve stare all'inizio (esclude spin-off tipo "FORTNITE X ... - STAGIONE 1")
    private static readonly Regex RxSeasonHeader = new(
        @"^STAGIONE\s+(\d+)(?:\s+A\s+(\d+))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Riga episodio dopo la decodifica: "1×01 – ...", "1x01-02 – ..."
    private static readonly Regex RxEpisodeNumber = new(
        @"^\s*(\d{1,2})\s*[×xX]\s*(\d{1,3})",
        RegexOptions.Compiled);
    // Righe della cartella uprot /msfld/: nome file + link /msfi/ (dominio libero: anche uprot cambia)
    private static readonly Regex RxFolderRow = new(
        @"<tr><td>\s*([^<]+?)\s*<td>.*?href='(https?://[^'/]+/msfi/[^']+)'",
        RegexOptions.Compiled | RegexOptions.Singleline);
    // "01x03", "01x01.02" (doppio), "S01E03"; la lookahead evita di leggere "1080p" come episodio
    private static readonly Regex RxFileEpisode = new(
        @"(?<!\d)(\d{1,2})x(\d{1,3})(?:\.(\d{1,3}))?(?![\dp])|S(\d{1,2})E(\d{1,3})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RxResolution = new(
        @"(?<!\d)(2160|1440|1080|720|576|480|360)[pi](?![a-z])|\b(4K|UHD)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private record SeriesEpisode(int Season, int Episode, string Title, List<Cb01Link> Links);
    private readonly Dictionary<string, (DateTime At, List<SeriesEpisode> Episodes)> _seriesCache = new();
    private static readonly TimeSpan SeriesCacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>Dominio corrente di CB01 dalla tab Domini (cambia spesso).</summary>
    private string BaseUrl => _domains.GetBaseUrl(DomainResolver.Cb01);

    public string Name => "cb01";

    public Cb01Service(ILogger<Cb01Service> logger, IProxyProvider proxyProvider, DomainResolver domains, MaxstreamService maxstream)
    {
        _logger = logger;
        _domains = domains;
        _maxstream = maxstream;

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        };

        var proxy = proxyProvider.GetActive(ProviderKeys.Cb01);
        if (proxy != null)
        {
            var scheme = proxy.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
            handler.Proxy = new WebProxy($"{scheme}://{proxy.Host}:{proxy.Port}");
            if (proxy.Auth == ProxyAuthType.UserPassword)
                handler.Proxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
            handler.UseProxy = true;
        }

        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");

        _linkResolver = new LinkResolver(logger, _http, () => _domains.GetBaseUrl(DomainResolver.Maxstream));
    }

    // ============================================================
    //  RICERCA
    // ============================================================

    public async Task<List<MovieSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var results = new List<MovieSearchResult>();
        var encoded = Uri.EscapeDataString(query);

        // Cerca sia nei film che nelle serie TV
        var filmTask = SearchPageAsync($"{BaseUrl}/?s={encoded}", false, ct);
        var serieTask = SearchPageAsync($"{BaseUrl}/serietv/?s={encoded}", true, ct);

        var filmResults = await filmTask;
        var serieResults = await serieTask;

        results.AddRange(filmResults);
        results.AddRange(serieResults);

        _logger.LogInformation("[CB01] Ricerca '{Query}': {Film} film + {Serie} serie",
            query, filmResults.Count, serieResults.Count);
        return results;
    }

    private async Task<List<MovieSearchResult>> SearchPageAsync(string url, bool isSeries, CancellationToken ct)
    {
        var results = new List<MovieSearchResult>();
        try
        {
            var html = await _http.GetStringAsync(url, ct);
            foreach (Match card in RxCard.Matches(html))
            {
                var block = card.Groups[1].Value;
                var titleMatch = RxCardTitle.Match(block);
                if (!titleMatch.Success) continue;

                var link = titleMatch.Groups[1].Value;
                var rawTitle = HttpUtility.HtmlDecode(titleMatch.Groups[2].Value).Trim();

                var imgMatch = RxCardImage.Match(block);
                var yearMatch = RxYear.Match(rawTitle);

                // Togli [HD] / [Sub-ITA] / (anno) dal titolo per la visualizzazione
                var cleanTitle = Regex.Replace(rawTitle, @"\s*\[.*?\]\s*", " ").Trim();
                cleanTitle = Regex.Replace(cleanTitle, @"\s*\(\d{4}\)\s*$", "").Trim();

                results.Add(new MovieSearchResult
                {
                    Title = cleanTitle,
                    Link = link,
                    PosterUrl = imgMatch.Success ? imgMatch.Groups[1].Value : null,
                    Year = yearMatch.Success ? yearMatch.Groups[1].Value : null,
                    IsSeries = isSeries || link.Contains("/serietv/"),
                    Description = isSeries ? "Serie TV" : "Film",
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CB01] Errore ricerca {Url}", url);
        }
        return results;
    }

    // ============================================================
    //  STAGIONI (solo per serie TV)
    // ============================================================

    /// <summary>Lista vuota = film (la tab passa allora a "Scarica film").</summary>
    public async Task<List<Season>> GetSeasonsAsync(string seriesLink, CancellationToken ct = default)
    {
        if (!IsSeriesLink(seriesLink)) return new List<Season>();

        try
        {
            var episodes = await GetSeriesEpisodesAsync(seriesLink, ct);
            var seasons = episodes
                .GroupBy(e => e.Season)
                .OrderBy(g => g.Key)
                .Select(g => new Season { Number = g.Key, Title = $"Stagione {g.Key} ({g.Count()} ep.)" })
                .ToList();

            _logger.LogInformation("[CB01] {Link}: {N} stagioni, {E} episodi", seriesLink, seasons.Count, episodes.Count);
            return seasons;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CB01] Errore stagioni {Link}", seriesLink);
            return new List<Season>();
        }
    }

    private static bool IsSeriesLink(string link) => link.Contains("/serietv/", StringComparison.OrdinalIgnoreCase);

    // ============================================================
    //  EPISODI / LINK FILM
    // ============================================================

    /// <summary>Serie: episodi della stagione. Film: un elemento per ogni link trovato nel post.</summary>
    public async Task<List<StreamingEpisode>> GetEpisodesAsync(
        string contentLink, int seasonNumber, CancellationToken ct = default)
    {
        try
        {
            List<StreamingEpisode> episodes;
            if (IsSeriesLink(contentLink))
            {
                episodes = (await GetSeriesEpisodesAsync(contentLink, ct))
                    .Where(e => e.Season == seasonNumber)
                    .Select(e => new StreamingEpisode
                    {
                        Number = e.Episode.ToString(),
                        Title = e.Title,
                        Link = e.Links[0].Url,
                        SeasonNumber = e.Season,
                    })
                    .ToList();
            }
            else
            {
                episodes = (await GetFilmLinksAsync(contentLink, ct))
                    .Select((l, i) => new StreamingEpisode
                    {
                        Number = (i + 1).ToString(),
                        Title = $"{l.Section} — {l.Label}",
                        Link = l.Url,
                        SeasonNumber = 1,
                    })
                    .ToList();
            }

            _logger.LogInformation("[CB01] {Link} s{Season}: {N} episodi/link",
                contentLink, seasonNumber, episodes.Count);
            return episodes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CB01] Errore episodi {Link}", contentLink);
            return new List<StreamingEpisode>();
        }
    }

    /// <summary>Link che possono portare a Maxstream (protettori o Maxstream stesso), qualunque sia l'etichetta.</summary>
    private bool IsCandidateHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var h = u.Host.ToLowerInvariant();
        return h.Contains("stayonline") || h.Contains("uprot") || h.Contains("maxstream") ||
               DomainResolver.HostOf(url) == DomainResolver.HostOf(_domains.GetBaseUrl(DomainResolver.Maxstream));
    }

    /// <summary>
    /// Tutti gli episodi di una serie, ordinati per (stagione, episodio) e senza doppioni.
    /// Le pagine hanno due forme: righe "1×01 – Maxstream – Mixdrop" con i link dell'episodio, oppure
    /// un unico link a una cartella uprot /msfld/ ("TUTTE LE STAGIONI") che elenca i file.
    /// </summary>
    private async Task<List<SeriesEpisode>> GetSeriesEpisodesAsync(string seriesLink, CancellationToken ct)
    {
        lock (_seriesCache)
        {
            if (_seriesCache.TryGetValue(seriesLink, out var cached) && DateTime.UtcNow - cached.At < SeriesCacheTtl)
                return cached.Episodes;
        }

        var html = await _http.GetStringAsync(seriesLink, ct);

        var sections = new List<(string Header, int From, int To, bool IsSub, bool IsHd, string Body)>();
        foreach (var block in html.Split("<div class=\"sp-wrap", StringSplitOptions.None).Skip(1))
        {
            var m = RxSpoiler.Match(block);
            if (!m.Success) continue;

            var header = WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]+>", "")).Trim();
            var sm = RxSeasonHeader.Match(header);
            if (!sm.Success)
            {
                _logger.LogInformation("[CB01] Sezione ignorata (non è una stagione): '{Header}'", header);
                continue;
            }

            int from = int.Parse(sm.Groups[1].Value);
            int to = sm.Groups[2].Success ? int.Parse(sm.Groups[2].Value) : from;
            bool isSub = header.Contains("SUB", StringComparison.OrdinalIgnoreCase);
            bool isHd = Regex.IsMatch(header, @"\bHD\b", RegexOptions.IgnoreCase);
            sections.Add((header, from, to, isSub, isHd, m.Groups[2].Value));
        }

        // Tutti i link di ogni episodio, da tutte le sezioni: la scelta del migliore avviene al download
        var byEpisode = new Dictionary<(int, int), (string Title, List<Cb01Link> Links)>();
        foreach (var s in sections)
        {
            int count = 0;
            foreach (var rawLine in Regex.Split(s.Body, @"</p>|<br\s*/?>", RegexOptions.IgnoreCase))
            {
                var anchors = RxAnchor.Matches(rawLine)
                    .Select(a => (Url: WebUtility.HtmlDecode(a.Groups[1].Value), Label: WebUtility.HtmlDecode(a.Groups[2].Value).Trim()))
                    .Where(a => IsCandidateHost(a.Url))
                    .ToList();
                if (anchors.Count == 0) continue;

                // Cartella con tutti gli episodi
                var folder = anchors.FirstOrDefault(a => a.Url.Contains("/msfld/", StringComparison.OrdinalIgnoreCase));
                if (folder.Url != null)
                {
                    foreach (var ep in await ParseFolderAsync(folder.Url, s.From, s.To, ct))
                    {
                        AddLink(ep.Season, ep.Episode, ep.Title, new Cb01Link(ep.Url, "Maxstream", s.Header, s.IsHd, s.IsSub));
                        count++;
                    }
                    continue;
                }

                var text = WebUtility.HtmlDecode(Regex.Replace(rawLine, "<[^>]+>", "")).Trim();
                var em = RxEpisodeNumber.Match(text);
                if (!em.Success) continue;
                int season = int.Parse(em.Groups[1].Value), episode = int.Parse(em.Groups[2].Value);
                if (episode <= 0) continue;

                foreach (var a in anchors)
                    AddLink(season, episode, $"Episodio {episode}", new Cb01Link(a.Url, a.Label, s.Header, s.IsHd, s.IsSub));
                count++;
            }
            _logger.LogInformation("[CB01] Sezione '{Header}': {N} episodi", s.Header, count);
        }

        void AddLink(int season, int episode, string title, Cb01Link link)
        {
            if (!byEpisode.TryGetValue((season, episode), out var e))
                byEpisode[(season, episode)] = e = (title, new List<Cb01Link>());
            if (!e.Links.Any(l => l.Url == link.Url)) e.Links.Add(link);
        }

        var episodes = byEpisode
            .Select(kv =>
            {
                // Se c'è la versione italiana, quella sottotitolata non si usa
                var links = kv.Value.Links.Any(l => !l.IsSub) ? kv.Value.Links.Where(l => !l.IsSub).ToList() : kv.Value.Links;
                return new SeriesEpisode(kv.Key.Item1, kv.Key.Item2, kv.Value.Title, OrderCandidates(links));
            })
            .OrderBy(e => e.Season).ThenBy(e => e.Episode)
            .ToList();

        lock (_seriesCache) _seriesCache[seriesLink] = (DateTime.UtcNow, episodes);
        return episodes;
    }

    /// <summary>Ordine di prova: prima ciò che il post dichiara HD, poi le etichette "Maxstream". Solo un'ipotesi: decide il file reale.</summary>
    private static List<Cb01Link> OrderCandidates(IEnumerable<Cb01Link> links) => links
        .Select((l, i) => (l, i))
        .OrderByDescending(x => x.l.DeclaredHd)
        .ThenByDescending(x => x.l.Label.Contains("maxstream", StringComparison.OrdinalIgnoreCase))
        .ThenBy(x => x.i)
        .Select(x => x.l)
        .ToList();

    private record FolderEpisode(int Season, int Episode, string Title, string Url);

    /// <summary>
    /// Legge una cartella uprot /msfld/ (nessun captcha né Cloudflare): una riga per file,
    /// stagione ed episodio presi dal nome ("Serie.01x03.Titolo.mp4", "S01E03").
    /// Gli episodi 00 (speciali) vengono saltati perché la coda richiede numeri > 0.
    /// </summary>
    private async Task<List<FolderEpisode>> ParseFolderAsync(string folderUrl, int fromSeason, int toSeason, CancellationToken ct)
    {
        var list = new List<FolderEpisode>();
        string html;
        try { html = await _http.GetStringAsync(folderUrl, ct); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CB01] Cartella {Url} non raggiungibile", folderUrl);
            return list;
        }

        foreach (Match row in RxFolderRow.Matches(html))
        {
            var fileName = WebUtility.HtmlDecode(row.Groups[1].Value).Trim();
            var em = RxFileEpisode.Match(fileName);
            if (!em.Success)
            {
                _logger.LogInformation("[CB01] File senza numero episodio, saltato: {File}", fileName);
                continue;
            }

            int season, episode;
            string? second = null;
            if (em.Groups[1].Success)
            {
                season = int.Parse(em.Groups[1].Value);
                episode = int.Parse(em.Groups[2].Value);
                if (em.Groups[3].Success) second = em.Groups[3].Value;
            }
            else
            {
                season = int.Parse(em.Groups[4].Value);
                episode = int.Parse(em.Groups[5].Value);
            }

            if (episode <= 0) continue;
            if (season < fromSeason || season > toSeason)
                _logger.LogInformation("[CB01] {File}: stagione {S} fuori da {From}-{To}, la tengo comunque", fileName, season, fromSeason, toSeason);

            var title = second != null ? $"Episodi {episode}-{int.Parse(second)}" : $"Episodio {episode}";
            var rest = fileName[(em.Index + em.Length)..].Trim('.', ' ', '-', '_');
            rest = Regex.Replace(rest, @"\.(mp4|mkv|avi)$", "", RegexOptions.IgnoreCase).Replace('.', ' ').Trim();
            if (rest.Length > 0) title += $" — {rest}";

            list.Add(new FolderEpisode(season, episode, title, row.Groups[2].Value));
        }

        _logger.LogInformation("[CB01] Cartella {Url}: {N} episodi", folderUrl, list.Count);
        return list;
    }

    /// <summary>
    /// Tutti i link Streaming di un film (HD e SD, Maxstream e non), qualunque etichetta abbiano.
    /// La pagina divide i link in tabelle "cbtable" con intestazione "Streaming:", "Streaming HD:",
    /// "Download:"...; la stessa scritta compare anche nei metadati JSON in cima alla pagina,
    /// quindi si cerca l'intestazione solo dentro ciascuna tabella.
    /// </summary>
    private async Task<List<Cb01Link>> GetFilmLinksAsync(string filmLink, CancellationToken ct)
    {
        var html = await _http.GetStringAsync(filmLink, ct);
        var links = new List<Cb01Link>();

        foreach (var section in html.Split("<table class=\"cbtable\"", StringSplitOptions.None).Skip(1))
        {
            var header = RxSectionHeader.Match(section);
            if (!header.Success) continue;
            if (!header.Groups[1].Value.Equals("Streaming", StringComparison.OrdinalIgnoreCase)) continue;
            var quality = header.Groups[2].Value.Trim().ToUpperInvariant();
            if (quality == "3D") continue;

            var sectionName = quality == "HD" ? "Streaming HD" : "Streaming";
            foreach (Match a in RxAnchor.Matches(section))
            {
                var url = WebUtility.HtmlDecode(a.Groups[1].Value);
                if (!IsCandidateHost(url) || links.Any(l => l.Url == url)) continue;
                links.Add(new Cb01Link(url, WebUtility.HtmlDecode(a.Groups[2].Value).Trim(), sectionName, quality == "HD", false));
            }
        }

        _logger.LogInformation("[CB01] Link film: {Links}",
            links.Count == 0 ? "(nessuno)" : string.Join(" | ", links.Select(l => $"{l.Section}/{l.Label}")));
        return OrderCandidates(links);
    }

    // ============================================================
    //  SCELTA DEL FILE MIGLIORE
    // ============================================================

    /// <summary>
    /// Prova i link del film/episodio e restituisce il file migliore (risoluzione dal nome reale,
    /// poi dimensione). Si ferma al primo in 1080p o più. Servono solo dati salvati nella coda
    /// (pagina, stagione, episodio), così funziona anche dopo un riavvio dell'app.
    /// </summary>
    public async Task<MaxstreamDownload?> ResolveBestAsync(
        string contentLink, bool isSeries, int season, int episode, CancellationToken ct = default)
    {
        List<Cb01Link> candidates;
        if (isSeries)
        {
            var ep = (await GetSeriesEpisodesAsync(contentLink, ct)).FirstOrDefault(e => e.Season == season && e.Episode == episode);
            candidates = ep?.Links ?? new List<Cb01Link>();
        }
        else
        {
            candidates = await GetFilmLinksAsync(contentLink, ct);
        }

        if (candidates.Count == 0)
        {
            _logger.LogWarning("[CB01] Nessun link per {Link} s{S}e{E}", contentLink, season, episode);
            return null;
        }

        MaxstreamDownload? best = null;
        (int Height, long Size) bestScore = (-1, -1);
        var seen = new HashSet<string>();

        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var entry = await _linkResolver.ResolveAsync(c.Url, ct);
            if (entry == null)
            {
                _logger.LogInformation("[CB01] {Section}/{Label}: non porta a Maxstream", c.Section, c.Label);
                continue;
            }
            if (!seen.Add(entry)) continue;

            var d = await _maxstream.ResolveDownloadAsync(entry, ct);
            if (d == null) continue;

            var score = (HeightOf(d.FileName), d.SizeBytes ?? 0);
            _logger.LogInformation("[CB01] {Section}/{Label}: {File} — {H}, {Size}", c.Section, c.Label, d.FileName,
                score.Item1 > 0 ? $"{score.Item1}p" : "risoluzione non indicata",
                score.Item2 > 0 ? $"{score.Item2 / 1048576.0:F0} MB" : "dimensione ignota");

            if (score.CompareTo(bestScore) > 0) { best = d; bestScore = score; }
            if (score.Item1 >= 1080) break;
        }

        if (best != null)
            _logger.LogInformation("[CB01] Scelto: {File} ({Variant})", best.FileName, best.Variant);
        else
            _logger.LogWarning("[CB01] Nessun link funzionante per {Link} s{S}e{E}", contentLink, season, episode);
        return best;
    }

    private static int HeightOf(string fileName)
    {
        var m = RxResolution.Match(fileName);
        if (!m.Success) return 0;
        return m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 2160;
    }

    /// <summary>
    /// Collega al download l'estrazione del link: al via sceglie il file migliore e ne salva
    /// il nome reale, usato dal pattern {OriginalFileName}.
    /// </summary>
    public void AttachExtractor(DownloadItem item)
    {
        var link = item.ContentLink;
        var isSeries = item.IsSeries;
        var season = item.SeasonNumber;
        var episode = item.EpisodeNumber;

        item.CookieExtractor = null;
        item.UrlExtractor = async ct =>
        {
            var d = await ResolveBestAsync(link, isSeries, season, episode, ct);
            if (d == null) return null;
            item.SourceFileName = d.FileName;
            return d.Url;
        };
    }
}

/// <summary>Interfaccia per il provider cb01 (film e serie via Maxstream).</summary>
public interface ICb01Service
{
    string Name { get; }
    Task<List<MovieSearchResult>> SearchAsync(string query, CancellationToken ct = default);
    Task<List<Season>> GetSeasonsAsync(string seriesLink, CancellationToken ct = default);
    Task<List<StreamingEpisode>> GetEpisodesAsync(string contentLink, int seasonNumber, CancellationToken ct = default);
    Task<MaxstreamDownload?> ResolveBestAsync(string contentLink, bool isSeries, int season, int episode, CancellationToken ct = default);
    void AttachExtractor(DownloadItem item);
}
