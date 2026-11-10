using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Persistence;
using DownloadManager.Services.Tmdb;

namespace DownloadManager.ViewModels;

public enum TmdbFilter { All, Rename, Episodes, InCorso, Confirm, NotFound, Ok, Errors, Manual, Skipped }

/// <summary>Una riga della vista "Titoli TMDB": il risultato dell'analisi di un post.</summary>
public partial class TmdbRowViewModel : ObservableObject
{
    public TmdbAnalysis A { get; }
    public TmdbRowViewModel(TmdbAnalysis a)
    {
        A = a;
        _proposedTitle = a.ProposedTitle;
        _isChecked = a.State == TmdbRowState.Rename;
    }

    [ObservableProperty] private bool _isChecked;
    [ObservableProperty] private string _proposedTitle;
    [ObservableProperty] private Bitmap? _poster;

    // ---- Titolo scritto a mano ----
    /// <summary>Il titolo proposto l'hai scritto tu (resta anche cambiando pattern).</summary>
    [ObservableProperty] private bool _isManual;
    /// <summary>Durante il caricamento il titolo cambia senza che sia una tua modifica.</summary>
    internal bool Loading;
    /// <summary>Hai modificato il titolo proposto nel dettaglio.</summary>
    public event Action<TmdbRowViewModel>? ProposedTitleEdited;
    /// <summary>Il titolo che darebbe il pattern (per confrontarlo con quello scritto a mano).</summary>
    public string PatternTitle => A.ProposedTitle;
    /// <summary>Scelte salvate per questo post (stagione o codice TMDB scelti da te).</summary>
    [ObservableProperty] private bool _hasUserChoice;

    // ---- Esito della rinomina sul forum ----
    [ObservableProperty] private RenameResult? _outcome;
    [ObservableProperty] private string _outcomeText = "";
    public bool HasOutcome => OutcomeText.Length > 0;
    public bool IsOutcomeGood => Outcome == RenameResult.Renamed;
    /// <summary>Saltato, errore o problema: lo stato mostra il motivo.</summary>
    public bool IsOutcomeWarn => Outcome is RenameResult.Skipped or RenameResult.Error or RenameResult.Critical;

    public void SetOutcome(RenameOutcome o, string prefix = "")
    {
        Outcome = o.Result;
        OutcomeText = prefix + o.Message;
        Refresh();
    }

    /// <summary>Ricalcola tutto ciò che si vede (stato e titolo cambiano dopo una rinomina).</summary>
    public void Refresh()
    {
        foreach (var n in new[] { nameof(CurrentTitle), nameof(StateText), nameof(StateTip), nameof(IsRename), nameof(IsOk), nameof(IsConfirm),
                                  nameof(IsMissing), nameof(HasOutcome), nameof(IsOutcomeGood), nameof(IsOutcomeWarn), nameof(PatternTitle),
                                  nameof(NotesText), nameof(HasNotes) })
            OnPropertyChanged(n);
    }

    public int PostId => A.PostId;
    public string CurrentTitle => A.CurrentTitle;
    public bool HasProposal => !string.IsNullOrEmpty(ProposedTitle);
    public string Initials => string.Concat((A.MatchName.Length > 0 ? A.MatchName : A.CurrentTitle)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public string MatchText => A.Kind == null ? "Nessun abbinamento"
        : $"{A.MatchName}{(A.Year is { } y ? $" · {y}" : "")} · {(A.Kind == "movie" ? "film" : A.Season is { } s ? (s == 0 ? "speciali" : $"S{s}") : "serie intera")}";
    public string SourceText => A.Kind == null ? (A.Notes.FirstOrDefault() ?? "") : A.IdSource;

    // Episodi
    public bool IsMovie => A.Kind == "movie";
    public string EpisodesText => A.Kind == null ? $"{A.LinkCount} / ?" : IsMovie ? (A.LinkCount <= 1 ? "film" : $"{A.LinkCount} parti") : $"{A.LinkCount} / {A.Total?.ToString() ?? "?"}";
    public bool EpOk => A.Kind != null && !A.HasEpisodeIssue && !A.InCorso;
    public bool EpWarn => A.InCorso && !A.HasEpisodeIssue;
    public bool EpBad => A.HasEpisodeIssue;
    public bool EpNone => A.Kind == null;

    // Stato
    public string StateText => Outcome switch
    {
        RenameResult.Renamed => "Rinominato ✓",
        RenameResult.Skipped => "Saltato",
        RenameResult.Error => "Errore nell'invio",
        RenameResult.Critical => "Da controllare!",
        _ => StateByAnalysis
    };
    private string StateByAnalysis => A.State switch
    {
        TmdbRowState.Rename => A.InCorso ? "Da rinominare · in corso" : "Da rinominare",
        TmdbRowState.AlreadyOk => "Già corretto",
        TmdbRowState.Confirm => A.EpisodeIssue ?? "Da confermare",
        TmdbRowState.NotFound => "Non trovato",
        TmdbRowState.Error => "Errore",
        _ => ""
    };
    public string StateTip => string.Join("\n", new[] { HasOutcome ? OutcomeText : null, A.EpisodeIssue, A.InCorsoNote }.Where(x => x != null).Concat(A.Notes));
    public bool IsRename => A.State == TmdbRowState.Rename && !IsOutcomeWarn;
    public bool IsOk => A.State == TmdbRowState.AlreadyOk && !IsOutcomeWarn;
    public bool IsConfirm => A.State == TmdbRowState.Confirm && !IsOutcomeWarn;
    public bool IsMissing => A.State is TmdbRowState.NotFound or TmdbRowState.Error && !IsOutcomeWarn;

    // Dettaglio
    public string KindText => A.Kind switch { "movie" => "Film", "tv" => "Serie", _ => "—" };
    public string YearText => A.Year?.ToString() ?? "";
    public string TmdbCode => A.TmdbId is { } id ? $"TMDB {id}{(A.Season is { } s ? $" · S{s}" : "")}" : "";
    public string SeasonSourceText => A.SeasonSource;
    public bool HasOriginal => !string.IsNullOrEmpty(A.OriginalName) && !string.Equals(A.OriginalName, A.MatchName, StringComparison.OrdinalIgnoreCase);
    public string OriginalText => $"{A.OriginalName} (titolo originale)";
    public IEnumerable<TmdbSeasonRow> SeasonRows => A.Seasons.OrderBy(s => s.Number == 0 ? int.MaxValue : s.Number)
        .Select(s => new TmdbSeasonRow(s, s.Number == A.Season));
    public bool HasSeasons => A.Seasons.Count > 0;
    public string NotesText => StateTip;
    public bool HasNotes => StateTip.Length > 0;
    public string TmdbUrl => A.TmdbId is { } id ? $"https://www.themoviedb.org/{A.Kind}/{id}" : "";
    public string ImagesText => A.Images == null ? "TMDB non ha immagini per questo titolo" : A.ImagesSource;
    public bool HasMatchImages => A.Kind != null;

    partial void OnProposedTitleChanged(string value)
    {
        OnPropertyChanged(nameof(HasProposal));
        if (!Loading) ProposedTitleEdited?.Invoke(this);
    }
}

/// <summary>Stagione nel pannello di dettaglio (evidenziata quella del post).</summary>
public sealed record TmdbSeasonRow(TmdbSeason S, bool IsCurrent)
{
    public int Number => S.Number;
    public string Label => S.Number == 0 ? "Speciali (esclusi dal conteggio)" : S.HasOwnName ? $"Stagione {S.Number} - {S.Name}" : $"Stagione {S.Number}";
    public string Info => $"{S.EpisodeCount} ep{(S.Year is { } y ? $" · {y}" : "")}";
}

/// <summary>
/// Vista "Titoli TMDB" di Videobox Corrector (fase 1: solo analisi, nessuna modifica al forum).
/// Analizza i post caricati: codice TMDB dalla pagina del post, stagione, episodi, titolo proposto.
/// </summary>
public partial class TmdbTitlesViewModel : ObservableObject
{
    private readonly Func<IReadOnlyList<VideoboxPostItem>> _posts;
    private readonly SettingsService _settings;
    private readonly TmdbKeyRepository _keyRepo;
    private readonly TmdbCacheRepository _cache;
    private readonly TmdbClient _tmdb;
    private readonly TmdbAnalyzer _analyzer;
    private readonly PostPageReader _reader = new();
    private readonly TmdbRenamer? _renamer;
    private string _key = "";
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _renameCts;
    /// <summary>Scelte salvate (titolo a mano, codice, stagione) per numero di post.</summary>
    private Dictionary<int, TmdbCacheRepository.Override> _overrides = new();

    private readonly List<TmdbRowViewModel> _all = new();
    public ObservableCollection<TmdbRowViewModel> Rows { get; } = new();

    [ObservableProperty] private TmdbRowViewModel? _selectedRow;
    [ObservableProperty] private bool _isAnalyzing;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "Carica i post di una o più categorie (Carica Tutti) e premi «Analizza».";
    [ObservableProperty] private TmdbFilter _filter = TmdbFilter.All;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _seriesPattern;
    [ObservableProperty] private string _moviePattern;
    [ObservableProperty] private Bitmap? _detailPoster;

    // ---- Chiave TMDB (nelle impostazioni di Videobox Corrector) ----
    [ObservableProperty] private string _tmdbKey = string.Empty;
    [ObservableProperty] private string _tmdbStatusMessage = "";
    [ObservableProperty] private bool _isTmdbBusy;
    [ObservableProperty] private bool _hasTmdbKey;
    [ObservableProperty] private string _tmdbBadge = "";
    [ObservableProperty] private string _tmdbTone = "off";

    public TmdbTitlesViewModel(Func<IReadOnlyList<VideoboxPostItem>> posts, SettingsService settings, TmdbKeyRepository keyRepo, TmdbCacheRepository cache,
        TmdbRenamer? renamer = null)
    {
        _renamer = renamer;
        _posts = posts;
        _settings = settings;
        _keyRepo = keyRepo;
        _cache = cache;
        _tmdb = new TmdbClient(() => _key);
        _analyzer = new TmdbAnalyzer(_tmdb);
        _seriesPattern = string.IsNullOrWhiteSpace(settings.Current.TmdbSeriesPattern) ? TitlePattern.DefaultSeries : settings.Current.TmdbSeriesPattern;
        _moviePattern = string.IsNullOrWhiteSpace(settings.Current.TmdbMoviePattern) ? TitlePattern.DefaultMovie : settings.Current.TmdbMoviePattern;
        _updateImages = settings.Current.TmdbUpdateImages && DownloadManager.Services.Videobox.VideoboxApiService.ThumbnailUploadConfirmed;
        _tmdbKey = keyRepo.Load();
        _hasTmdbKey = _tmdbKey.Length > 0;
        UpdateTmdbBadge(_hasTmdbKey ? null : "");
    }

    // ============================================================
    //  CONTEGGI E FILTRI
    // ============================================================

    public int CountAll => _all.Count;
    public int CountRename => _all.Count(r => r.A.State == TmdbRowState.Rename);
    public int CountEpisodes => _all.Count(r => r.A.HasEpisodeIssue);
    public int CountInCorso => _all.Count(r => r.A.InCorso);
    public int CountConfirm => _all.Count(r => r.A.State == TmdbRowState.Confirm);
    public int CountNotFound => _all.Count(r => r.A.State == TmdbRowState.NotFound);
    public int CountOk => _all.Count(r => r.A.State == TmdbRowState.AlreadyOk);
    public int CountErrors => _all.Count(r => r.A.State == TmdbRowState.Error);
    public int CountChecked => _all.Count(r => r.IsChecked);
    public int CountManual => _all.Count(r => r.IsManual);
    public int CountSkipped => _all.Count(r => r.IsOutcomeWarn);
    public bool HasManual => CountManual > 0;
    public bool HasSkipped => CountSkipped > 0;
    public bool HasRows => _all.Count > 0;
    public bool HasErrors => CountErrors > 0;
    public int PostsLoaded => _posts().Count;

    // ---- Passi guidati: 1 carica i post, 2 analizza, 3 controlla ----
    /// <summary>Post caricati non ancora analizzati.</summary>
    private int PostsToDo
    {
        get
        {
            var done = _all.Select(r => r.PostId).ToHashSet();
            return _posts().Count(p => !done.Contains(p.Id));
        }
    }
    public bool IsStep1Done => PostsLoaded > 0;
    public bool IsStep1Current => PostsLoaded == 0;
    public bool IsStep2Done => PostsLoaded > 0 && !IsAnalyzing && PostsToDo == 0;
    public bool IsStep2Current => PostsLoaded > 0 && (IsAnalyzing || PostsToDo > 0);
    public bool IsStep3Current => IsStep2Done && HasRows;
    public bool CanAnalyze => PostsLoaded > 0 && HasTmdbKey && !IsRenaming;

    /// <summary>Cosa fare adesso (al posto del testo dell'analisi finché non si è analizzato niente).</summary>
    public string HintText
    {
        get
        {
            if (IsAnalyzing || HasRows) return ProgressText;
            if (!HasTmdbKey) return "Prima inserisci la chiave TMDB: apri «Impostazioni (forum e TMDB)» qui sopra.";
            if (PostsLoaded == 0) return "Prima carica i post: scegli una o più categorie al passo 1 e premi «Carica Tutti».";
            return $"{PostsLoaded} post pronti: premi «Analizza». Il forum non viene modificato.";
        }
    }

    public string EmptyTitle => !HasTmdbKey ? "Manca la chiave TMDB" : PostsLoaded == 0 ? "Passo 1: carica i post" : "Passo 2: analizza";
    public string EmptyText => !HasTmdbKey
        ? "Apri «Impostazioni (forum e TMDB)» qui sopra e incolla la chiave del tuo account TMDB (si crea gratis su themoviedb.org)."
        : PostsLoaded == 0
            ? "Scegli una o più categorie in alto e premi «Carica Tutti». Poi premi «Analizza»: per ogni post si legge il codice TMDB dalla sua pagina, si controlla il numero di episodi e si propone il titolo secondo il pattern."
            : $"Hai caricato {PostsLoaded} post. Premi «Analizza»: la prima volta si legge la pagina di ogni post (circa mezzo secondo l'uno), le volte dopo è istantaneo. Il forum non viene modificato.";

    private static readonly string[] StepProps =
    {
        nameof(IsStep1Done), nameof(IsStep1Current), nameof(IsStep2Done), nameof(IsStep2Current), nameof(IsStep3Current),
        nameof(CanAnalyze), nameof(HintText), nameof(EmptyTitle), nameof(EmptyText)
    };
    private void NotifySteps() { foreach (var n in StepProps) OnPropertyChanged(n); }
    partial void OnIsAnalyzingChanged(bool value) { NotifySteps(); OnPropertyChanged(nameof(CanRename)); }
    partial void OnProgressTextChanged(string value) => OnPropertyChanged(nameof(HintText));
    partial void OnHasTmdbKeyChanged(bool value) => NotifySteps();
    public string AnalyzeButtonText => _all.Count > 0 && _all.Count < PostsLoaded ? $"Continua ({PostsLoaded - _all.Count} da fare)" : $"Analizza {PostsLoaded} post caricati";

    public bool IsFilterAll => Filter == TmdbFilter.All;
    public bool IsFilterRename => Filter == TmdbFilter.Rename;
    public bool IsFilterEpisodes => Filter == TmdbFilter.Episodes;
    public bool IsFilterInCorso => Filter == TmdbFilter.InCorso;
    public bool IsFilterConfirm => Filter == TmdbFilter.Confirm;
    public bool IsFilterNotFound => Filter == TmdbFilter.NotFound;
    public bool IsFilterOk => Filter == TmdbFilter.Ok;
    public bool IsFilterErrors => Filter == TmdbFilter.Errors;
    public bool IsFilterManual => Filter == TmdbFilter.Manual;
    public bool IsFilterSkipped => Filter == TmdbFilter.Skipped;

    private bool Matches(TmdbRowViewModel r) => MatchesFilter(r) && MatchesSearch(r);

    // ---- Ricerca: titolo attuale, proposto, nome TMDB/originale, numero del post o codice TMDB ----
    private string[] _searchWords = Array.Empty<string>();
    private CancellationTokenSource? _searchDelay;

    public bool HasSearch => _searchWords.Length > 0;
    public string SearchResultText => HasSearch ? (Rows.Count == 1 ? "1 risultato" : $"{Rows.Count} risultati") : "";

    private static string Fold(string s)
    {
        var d = (s ?? "").Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var c in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private bool MatchesSearch(TmdbRowViewModel r)
    {
        if (_searchWords.Length == 0) return true;
        var hay = Fold($"{r.A.CurrentTitle} | {r.ProposedTitle} | {r.A.MatchName} | {r.A.OriginalName} | #{r.A.PostId} | {r.A.TmdbId}");
        return _searchWords.All(w => hay.Contains(w));
    }

    partial void OnSearchTextChanged(string value)
    {
        // Piccola attesa: con migliaia di righe non si ricostruisce la lista a ogni tasto
        _searchDelay?.Cancel();
        var cts = _searchDelay = new CancellationTokenSource();
        _ = Task.Delay(180, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                _searchWords = Fold(value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                                .Select(w => w.TrimStart('#')).Where(w => w.Length > 0).ToArray();
                RebuildRows();
            });
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    private bool MatchesFilter(TmdbRowViewModel r) => Filter switch
    {
        TmdbFilter.Rename => r.A.State == TmdbRowState.Rename,
        TmdbFilter.Episodes => r.A.HasEpisodeIssue,
        TmdbFilter.InCorso => r.A.InCorso,
        TmdbFilter.Confirm => r.A.State == TmdbRowState.Confirm,
        TmdbFilter.NotFound => r.A.State == TmdbRowState.NotFound,
        TmdbFilter.Ok => r.A.State == TmdbRowState.AlreadyOk,
        TmdbFilter.Errors => r.A.State == TmdbRowState.Error,
        TmdbFilter.Manual => r.IsManual,
        TmdbFilter.Skipped => r.IsOutcomeWarn,
        _ => true
    };

    [RelayCommand]
    private void SetFilter(string name)
    {
        if (Enum.TryParse<TmdbFilter>(name, out var f)) Filter = f;
    }

    partial void OnFilterChanged(TmdbFilter value)
    {
        foreach (var n in new[] { nameof(IsFilterAll), nameof(IsFilterRename), nameof(IsFilterEpisodes), nameof(IsFilterInCorso),
                                  nameof(IsFilterConfirm), nameof(IsFilterNotFound), nameof(IsFilterOk), nameof(IsFilterErrors),
                                  nameof(IsFilterManual), nameof(IsFilterSkipped) })
            OnPropertyChanged(n);
        RebuildRows();
    }

    private void RebuildRows()
    {
        var keep = SelectedRow;
        Rows.Clear();
        foreach (var r in _all.Where(Matches)) Rows.Add(r);
        SelectedRow = keep != null && Rows.Contains(keep) ? keep : Rows.FirstOrDefault();
        OnPropertyChanged(nameof(HasSearch));
        OnPropertyChanged(nameof(SearchResultText));
    }

    private void NotifyCounts()
    {
        foreach (var n in new[] { nameof(CountAll), nameof(CountRename), nameof(CountEpisodes), nameof(CountInCorso), nameof(CountConfirm),
                                  nameof(CountNotFound), nameof(CountOk), nameof(CountErrors), nameof(CountChecked), nameof(HasRows),
                                  nameof(HasErrors), nameof(PostsLoaded), nameof(AnalyzeButtonText), nameof(SearchResultText),
                                  nameof(CountManual), nameof(CountSkipped), nameof(HasManual), nameof(HasSkipped), nameof(RenameConfirmText) })
            OnPropertyChanged(n);
        NotifySteps();
    }

    /// <summary>Da chiamare quando Videobox carica altri post (aggiorna il pulsante "Analizza N post").</summary>
    public void OnPostsChanged() => NotifyCounts();

    // ============================================================
    //  ANALISI
    // ============================================================

    [RelayCommand]
    private async Task AnalyzeAsync() => await RunAsync(onlyNew: true);

    /// <summary>Rifà tutto (rilegge anche le pagine dei post).</summary>
    [RelayCommand]
    private async Task ReanalyzeAllAsync()
    {
        await _cache.ForgetPageInfoAsync(_all.Select(r => r.PostId));
        _all.Clear();
        Rows.Clear();
        NotifyCounts();
        await RunAsync(onlyNew: true);
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    private async Task RunAsync(bool onlyNew)
    {
        if (IsAnalyzing) return;
        _key = _keyRepo.Load();
        if (string.IsNullOrWhiteSpace(_key)) { ProgressText = "Manca la chiave TMDB: inseriscila in «Impostazioni» qui sopra."; return; }
        var posts = _posts().ToList();
        if (posts.Count == 0) { ProgressText = "Nessun post caricato: scegli una o più categorie e premi «Carica Tutti»."; return; }

        if (IsRenaming) return;
        _analyzer.SeriesPattern = SeriesPattern;
        _analyzer.MoviePattern = MoviePattern;
        _overrides = await _cache.GetOverridesAsync();
        var done = _all.Select(r => r.PostId).ToHashSet();
        var todo = onlyNew ? posts.Where(p => !done.Contains(p.Id)).ToList() : posts;
        if (todo.Count == 0) { ProgressText = $"Tutti i {posts.Count} post sono già analizzati. «Rianalizza tutto» per ripartire da capo."; return; }

        var cts = _cts = new CancellationTokenSource();
        IsAnalyzing = true;
        int n = 0, fromPage = 0, consecutiveErrors = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            foreach (var post in todo)
            {
                cts.Token.ThrowIfCancellationRequested();
                n++;
                Progress = 100.0 * n / todo.Count;
                ProgressText = $"Analisi {n}/{todo.Count}: {post.Title}";

                TmdbAnalysis a;
                try
                {
                    // Codice e stagione: dalla memoria, altrimenti dalla pagina del post (a ritmo calmo)
                    var page = await _cache.GetPageInfoAsync(post.Id);
                    if (page == null)
                    {
                        page = await _reader.ReadAsync(post.Url, cts.Token);
                        await _cache.SavePageInfoAsync(post.Id, page);
                        fromPage++;
                    }
                    bool grew = await _cache.TrackLinkCountAsync(post.Id, post.LinkCount);
                    a = await _analyzer.AnalyzeAsync(new TmdbPostInput(post.Id, post.Title, post.Url, post.LinkCount, grew), WithChoices(post.Id, page), cts.Token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    a = new TmdbAnalysis { PostId = post.Id, CurrentTitle = post.Title, Url = post.Url, LinkCount = post.LinkCount, State = TmdbRowState.Error };
                    a.Notes.Add("Errore: " + ex.Message);
                }

                // Connessione persa: dopo 3 errori di fila si mette in pausa (i post fatti restano)
                consecutiveErrors = a.State == TmdbRowState.Error ? consecutiveErrors + 1 : 0;
                if (a.State == TmdbRowState.Error && consecutiveErrors >= 3)
                {
                    ProgressText = $"In pausa: {consecutiveErrors} errori di fila ({a.Notes.LastOrDefault()}). Controlla la connessione e premi «Continua»: i post già analizzati restano.";
                    return;   // l'ultimo errore non si aggiunge: verrà ripreso
                }

                var row = NewRow(a);
                _all.Add(row);
                if (Matches(row)) Rows.Add(row);
                if (SelectedRow == null && Rows.Count > 0) SelectedRow = Rows[0];
                if (n % 5 == 0 || n == todo.Count) NotifyCounts();
            }
            ProgressText = $"Analisi completata: {todo.Count} post in {sw.Elapsed:mm\\:ss} ({fromPage} pagine lette dal forum, gli altri dalla memoria). Nessuna modifica fatta sul forum.";
        }
        catch (OperationCanceledException) { ProgressText = $"Analisi fermata dopo {n - 1} post: premi «Continua» per riprendere."; }
        finally
        {
            IsAnalyzing = false;
            NotifyCounts();
            _cts = null;
        }
    }


    // ============================================================
    //  SCELTE DELL'UTENTE: titolo a mano, stagione, codice TMDB
    // ============================================================

    /// <summary>Codice e stagione della pagina, sostituiti da quelli che hai scelto tu (se ci sono).</summary>
    private PostPageInfo WithChoices(int postId, PostPageInfo page)
    {
        if (!_overrides.TryGetValue(postId, out var o) || (!o.HasMatch && o.Season == null)) return page;
        return o.HasMatch
            ? new PostPageInfo(o.Kind, (int)o.TmdbId!.Value, (int?)o.Season, null, TmdbAnalyzer.UserSource)
            : new PostPageInfo(page.Kind, page.TmdbId, (int?)o.Season, null, TmdbAnalyzer.UserSource);
    }

    /// <summary>Riga nuova con le scelte salvate applicate (titolo a mano).</summary>
    private TmdbRowViewModel NewRow(TmdbAnalysis a)
    {
        var row = new TmdbRowViewModel(a);
        if (_overrides.TryGetValue(a.PostId, out var o))
        {
            row.HasUserChoice = o.HasMatch || o.Season != null;
            if (o.ManualTitle is { Length: > 0 } manual && a.State is not (TmdbRowState.NotFound or TmdbRowState.Error))
            {
                row.Loading = true;
                row.ProposedTitle = manual;
                row.Loading = false;
                row.IsManual = true;
                a.State = TitlePattern.Same(manual, a.CurrentTitle) ? TmdbRowState.AlreadyOk : TmdbRowState.Rename;
                // Scritto da te: non entra da solo nei «Seleziona tutti», lo spunti tu
                row.IsChecked = false;
                row.Refresh();
            }
        }
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TmdbRowViewModel.IsChecked)) OnPropertyChanged(nameof(CountChecked)); };
        row.ProposedTitleEdited += r => _ = OnProposedTitleEditedAsync(r);
        return row;
    }

    private TmdbCacheRepository.Override OverrideFor(int postId) =>
        _overrides.TryGetValue(postId, out var o) ? o : _overrides[postId] = new TmdbCacheRepository.Override { PostId = postId };

    /// <summary>Hai cambiato il titolo proposto: si ricorda (o si dimentica, se torna quello del pattern).</summary>
    private async Task OnProposedTitleEditedAsync(TmdbRowViewModel row)
    {
        var o = OverrideFor(row.PostId);
        bool manual = !TitlePattern.Same(row.ProposedTitle, row.PatternTitle) && row.ProposedTitle.Trim().Length > 0;
        o.ManualTitle = manual ? row.ProposedTitle.Trim() : null;
        row.IsManual = manual;
        if (manual)
        {
            row.A.State = TitlePattern.Same(row.ProposedTitle, row.CurrentTitle) ? TmdbRowState.AlreadyOk : TmdbRowState.Rename;
            row.IsChecked = row.A.State == TmdbRowState.Rename;   // l'hai scritto per rinominarlo
        }
        row.Refresh();
        await _cache.SaveOverrideAsync(o);
        NotifyCounts();
    }

    /// <summary>Torna al titolo del pattern (dimentica quello scritto a mano).</summary>
    [RelayCommand]
    private void UsePatternTitle()
    {
        if (SelectedRow is { } row && row.HasProposal) row.ProposedTitle = row.PatternTitle;
    }

    /// <summary>Usa questa stagione per il post (clic su una stagione nel dettaglio).</summary>
    [RelayCommand]
    private async Task ChooseSeasonAsync(int number)
    {
        if (SelectedRow is not { } row || row.A.Kind != "tv" || IsRenaming) return;
        var o = OverrideFor(row.PostId);
        o.Season = number;
        if (!o.HasMatch && row.A.TmdbId is { } id) { o.Kind = row.A.Kind; o.TmdbId = id; }
        await _cache.SaveOverrideAsync(o);
        await ReanalyzeRowAsync(row);
    }

    /// <summary>Codice TMDB scritto nel dettaglio: "tv/1234", "movie 5678" o l'indirizzo della pagina TMDB.</summary>
    [ObservableProperty] private string _codeInput = "";

    [RelayCommand]
    private async Task ApplyCodeAsync()
    {
        if (SelectedRow is not { } row || IsRenaming) return;
        var m = System.Text.RegularExpressions.Regex.Match(CodeInput ?? "", @"\b(tv|movie)\s*[/ :]\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) { ProgressText = "Codice TMDB non valido: scrivi ad esempio «tv/1234» o «movie/5678», oppure incolla l'indirizzo della pagina TMDB."; return; }
        var o = OverrideFor(row.PostId);
        o.Kind = m.Groups[1].Value.ToLowerInvariant();
        o.TmdbId = long.Parse(m.Groups[2].Value);
        o.Season = null;
        await _cache.SaveOverrideAsync(o);
        CodeInput = "";
        await ReanalyzeRowAsync(row);
    }

    /// <summary>Dimentica codice e stagione scelti da te (il titolo scritto a mano resta).</summary>
    [RelayCommand]
    private async Task ResetChoicesAsync()
    {
        if (SelectedRow is not { } row || IsRenaming) return;
        var o = OverrideFor(row.PostId);
        o.Kind = null; o.TmdbId = null; o.Season = null;
        await _cache.SaveOverrideAsync(o);
        await ReanalyzeRowAsync(row);
    }

    /// <summary>Rianalizza un solo post (dopo una tua scelta): la riga viene sostituita al suo posto.</summary>
    private async Task ReanalyzeRowAsync(TmdbRowViewModel row)
    {
        _key = _keyRepo.Load();
        _analyzer.SeriesPattern = SeriesPattern;
        _analyzer.MoviePattern = MoviePattern;
        var post = _posts().FirstOrDefault(p => p.Id == row.PostId);
        string title = post?.Title ?? row.CurrentTitle, url = post?.Url ?? row.A.Url;
        int links = post?.LinkCount ?? row.A.LinkCount;
        try
        {
            var page = await _cache.GetPageInfoAsync(row.PostId) ?? await _reader.ReadAsync(url);
            var a = await _analyzer.AnalyzeAsync(new TmdbPostInput(row.PostId, title, url, links, false), WithChoices(row.PostId, page));
            var fresh = NewRow(a);
            int i = _all.IndexOf(row), j = Rows.IndexOf(row);
            if (i >= 0) _all[i] = fresh;
            if (j >= 0) Rows[j] = fresh;
            SelectedRow = fresh;
            ProgressText = $"Rianalizzato «{title}» con la tua scelta.";
        }
        catch (Exception ex) { ProgressText = "Rianalisi non riuscita: " + ex.Message; }
        NotifyCounts();
    }

    // ============================================================
    //  RINOMINA SUL FORUM (fase 2)
    // ============================================================

    /// <summary>Pausa tra un post e l'altro: il forum non va sovraccaricato.</summary>
    private static readonly TimeSpan RenamePause = TimeSpan.FromMilliseconds(1500);

    [ObservableProperty] private bool _isRenaming;
    /// <summary>Rinominando, carica come miniatura del post la copertina di TMDB (stagione, altrimenti serie/film).</summary>
    [ObservableProperty] private bool _updateImages;
    /// <summary>La spunta si attiva solo dopo la prova del caricamento sul forum vero.</summary>
    public bool CanUpdateImages => DownloadManager.Services.Videobox.VideoboxApiService.ThumbnailUploadConfirmed;
    public string UpdateImagesTip => CanUpdateImages
        ? "Carica come miniatura del post la copertina di TMDB: quella della stagione se c'è, altrimenti quella della serie (o del film). " +
          "Il forum la usa anche come immagine del post. Dopo l'invio si controlla che sia cambiata e integra. Lo sfondo lo mette il forum da solo."
        : "In prova: il caricamento della miniatura va prima verificato su un post del forum. Senza spunta le immagini non si toccano e non vengono controllate.";
    partial void OnUpdateImagesChanged(bool value) { _settings.Current.TmdbUpdateImages = value; _settings.Save(); }
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private double _renameProgress;
    [ObservableProperty] private bool _canUndo;
    [ObservableProperty] private string _undoConfirmText = "";
    public bool CanRename => _renamer != null && !IsRenaming && !IsAnalyzing;
    public bool HasRenameText => RenameText.Length > 0;

    public string RenameConfirmText
    {
        get
        {
            int n = _all.Count(r => r.IsChecked && r.HasProposal);
            return $"Rinominare {n} post sul forum{(UpdateImages && CanUpdateImages ? ", caricando anche la miniatura da TMDB" : "")}?\n\nOgni post viene riletto prima dell'invio (se nel frattempo è cambiato si salta) e " +
                   "ricontrollato dopo: se cambiasse qualcosa oltre al titolo mi fermo subito. Le rinomine si possono annullare con «Annulla ultima rinomina».";
        }
    }

    partial void OnIsRenamingChanged(bool value) { OnPropertyChanged(nameof(CanRename)); NotifySteps(); }
    partial void OnRenameTextChanged(string value) => OnPropertyChanged(nameof(HasRenameText));

    [RelayCommand]
    private void StopRename() => _renameCts?.Cancel();

    /// <summary>Rinomina i post spuntati, uno alla volta, con tutti i controlli (vedi TmdbRenamer).</summary>
    [RelayCommand]
    private async Task RenameCheckedAsync()
    {
        if (_renamer == null || IsRenaming || IsAnalyzing) return;
        var rows = _all.Where(r => r.IsChecked && r.HasProposal).ToList();
        if (rows.Count == 0) { RenameText = "Nessun post spuntato da rinominare."; return; }

        var cts = _renameCts = new CancellationTokenSource();
        IsRenaming = true;
        var batch = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        int renamed = 0, skipped = 0, errors = 0, consecutive = 0, n = 0;
        string? stopReason = null;
        try
        {
            RenameText = "Controllo delle rinomine rimaste a metà...";
            await _renamer.ResolvePendingAsync(cts.Token);

            foreach (var row in rows)
            {
                cts.Token.ThrowIfCancellationRequested();
                n++;
                RenameProgress = 100.0 * n / rows.Count;
                RenameText = $"Rinomina {n}/{rows.Count}: {row.CurrentTitle}";

                RenameOutcome o;
                try
                {
                    o = await _renamer.RenameAsync(batch, row.PostId, row.CurrentTitle, row.A.LinkCount, row.ProposedTitle, cts.Token,
                        UpdateImages && CanUpdateImages ? row.A.Images : null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { o = new RenameOutcome(RenameResult.Error, ex.Message); }

                row.IsChecked = false;
                if (o.Result == RenameResult.Renamed || (o.Result == RenameResult.Critical && o.NewTitleOnForum != null))
                    ApplyNewTitle(row, o.NewTitleOnForum ?? row.ProposedTitle);
                row.SetOutcome(o);

                switch (o.Result)
                {
                    case RenameResult.Renamed: renamed++; consecutive = 0; break;
                    case RenameResult.Skipped: skipped++; consecutive = 0; break;
                    case RenameResult.Error: errors++; consecutive++; break;
                    case RenameResult.Critical: errors++; stopReason = $"FERMATO su «{row.CurrentTitle}»: {o.Message}. Controlla il post sul forum prima di continuare."; break;
                }
                if (stopReason != null) break;
                if (consecutive >= 3) { stopReason = $"In pausa dopo 3 errori di fila ({o.Message}). Controlla la connessione e premi di nuovo «Rinomina selezionati»: i post fatti restano."; break; }
                if (n < rows.Count) await Task.Delay(RenamePause, cts.Token);
            }
        }
        catch (OperationCanceledException) { stopReason = "Rinomina fermata."; }
        finally
        {
            IsRenaming = false;
            _renameCts = null;
            var summary = $"{renamed} rinominati e verificati · {skipped} saltati · {errors} errori";
            RenameText = stopReason != null ? $"{stopReason} ({summary})" : $"Rinomina completata: {summary}." +
                         (skipped + errors > 0 ? " Il filtro «Saltati» mostra il motivo di ognuno." : "");
            NotifyCounts();
            await RefreshUndoAsync();
        }
    }

    /// <summary>Il post ora ha questo titolo sul forum: si aggiornano riga e lista dei post.</summary>
    private void ApplyNewTitle(TmdbRowViewModel row, string title)
    {
        var shown = System.Net.WebUtility.HtmlDecode(title);
        row.A.CurrentTitle = shown;
        row.A.State = TitlePattern.Same(shown, row.ProposedTitle) ? TmdbRowState.AlreadyOk : TmdbRowState.Rename;
        if (_posts().FirstOrDefault(p => p.Id == row.PostId) is { } post) post.Title = shown;
        row.Refresh();
    }

    /// <summary>Rimette i titoli di prima per l'ultimo gruppo di rinomine (solo i post non toccati dopo).</summary>
    [RelayCommand]
    private async Task UndoLastBatchAsync()
    {
        if (_renamer == null || IsRenaming || IsAnalyzing) return;
        var batch = await _cache.GetLastUndoableBatchAsync();
        if (batch == null) { RenameText = "Nessuna rinomina da annullare."; await RefreshUndoAsync(); return; }
        var entries = (await _cache.GetBatchAsync(batch)).Where(e => e.Status == "Done").ToList();

        var cts = _renameCts = new CancellationTokenSource();
        IsRenaming = true;
        int restored = 0, skipped = 0, errors = 0, consecutive = 0, n = 0;
        string? stopReason = null;
        try
        {
            await _renamer.ResolvePendingAsync(cts.Token);
            foreach (var e in entries)
            {
                cts.Token.ThrowIfCancellationRequested();
                n++;
                RenameProgress = 100.0 * n / entries.Count;
                RenameText = $"Annullo {n}/{entries.Count}: «{e.NewTitle}» → «{e.OldTitle}»";

                RenameOutcome o;
                try { o = await _renamer.UndoAsync(e, cts.Token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { o = new RenameOutcome(RenameResult.Error, ex.Message); }

                var row = _all.FirstOrDefault(r => r.PostId == e.PostId);
                if (row != null)
                {
                    if (o.Result == RenameResult.Renamed) ApplyNewTitle(row, o.NewTitleOnForum ?? e.OldTitle);
                    row.SetOutcome(o, o.Result == RenameResult.Renamed ? "rinomina annullata: " : "annullamento: ");
                    if (o.Result == RenameResult.Renamed) { row.Outcome = null; row.Refresh(); }
                }
                else if (o.Result == RenameResult.Renamed && _posts().FirstOrDefault(p => p.Id == e.PostId) is { } post)
                    post.Title = System.Net.WebUtility.HtmlDecode(o.NewTitleOnForum ?? e.OldTitle);

                switch (o.Result)
                {
                    case RenameResult.Renamed: restored++; consecutive = 0; break;
                    case RenameResult.Skipped: skipped++; consecutive = 0; break;
                    case RenameResult.Error: errors++; consecutive++; break;
                    case RenameResult.Critical: errors++; stopReason = $"FERMATO sul post {e.PostId}: {o.Message}."; break;
                }
                if (stopReason != null) break;
                if (consecutive >= 3) { stopReason = $"In pausa dopo 3 errori di fila ({o.Message}). Riprova «Annulla ultima rinomina» più tardi."; break; }
                if (n < entries.Count) await Task.Delay(RenamePause, cts.Token);
            }
        }
        catch (OperationCanceledException) { stopReason = "Annullamento fermato."; }
        finally
        {
            IsRenaming = false;
            _renameCts = null;
            var summary = $"{restored} titoli rimessi · {skipped} saltati (cambiati dopo la rinomina) · {errors} errori";
            RenameText = stopReason != null ? $"{stopReason} ({summary})" : $"Annullamento completato: {summary}.";
            NotifyCounts();
            await RefreshUndoAsync();
        }
    }

    /// <summary>Aggiorna il pulsante «Annulla ultima rinomina» dal registro.</summary>
    public async Task RefreshUndoAsync()
    {
        try
        {
            var batch = await _cache.GetLastUndoableBatchAsync();
            if (batch == null) { CanUndo = false; UndoConfirmText = ""; return; }
            var entries = await _cache.GetBatchAsync(batch);
            int done = entries.Count(e => e.Status == "Done");
            CanUndo = done > 0 && _renamer != null;
            UndoConfirmText = $"Rimettere il titolo di prima a {done} post (rinomina del {FormatBatch(batch)})?\n\n" +
                              "Si tocca solo chi ha ancora il titolo dato dalla rinomina: i post che hai modificato dopo restano come sono.";
        }
        catch { CanUndo = false; }
    }

    private static string FormatBatch(string batch) =>
        DateTime.TryParseExact(batch, "yyyyMMdd-HHmmss", null, System.Globalization.DateTimeStyles.None, out var d) ? d.ToString("dd/MM HH:mm") : batch;

    // ============================================================
    //  PATTERN
    // ============================================================

    partial void OnSeriesPatternChanged(string value) { _settings.Current.TmdbSeriesPattern = value; _settings.Save(); }
    partial void OnMoviePatternChanged(string value) { _settings.Current.TmdbMoviePattern = value; _settings.Save(); }

    /// <summary>Ricalcola i titoli proposti con i pattern attuali (dalla memoria: niente forum, TMDB già letto).</summary>
    [RelayCommand]
    private async Task ApplyPatternsAsync()
    {
        if (_all.Count == 0 || IsAnalyzing) return;
        var ids = _all.Select(r => r.PostId).ToHashSet();
        _all.Clear();
        Rows.Clear();
        await RunAsync(onlyNew: true);
    }

    [RelayCommand]
    private void ResetPatterns()
    {
        SeriesPattern = TitlePattern.DefaultSeries;
        MoviePattern = TitlePattern.DefaultMovie;
    }

    // ============================================================
    //  SELEZIONE E DETTAGLIO
    // ============================================================

    [RelayCommand]
    private void CheckAllRename() { foreach (var r in _all) r.IsChecked = r.A.State == TmdbRowState.Rename && !r.IsManual && !r.IsOutcomeWarn; }

    [RelayCommand]
    private void UncheckAll() { foreach (var r in _all) r.IsChecked = false; }

    [RelayCommand]
    private void OpenTmdb() { if (SelectedRow?.TmdbUrl is { Length: > 0 } u) OpenUrl(u); }

    [RelayCommand]
    private void OpenPost() { if (SelectedRow?.A.Url is { Length: > 0 } u) OpenUrl(u); }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    partial void OnSelectedRowChanged(TmdbRowViewModel? value)
    {
        DetailPoster = null;
        if (value?.A.PosterPath is { Length: > 0 } p) _ = LoadPosterAsync(p, "w342", bmp => { if (ReferenceEquals(SelectedRow, value)) DetailPoster = bmp; });
    }

    /// <summary>Locandine piccole delle righe visibili (si caricano quando la riga compare).</summary>
    public void EnsureRowPoster(TmdbRowViewModel row)
    {
        if (row.Poster != null || row.A.PosterPath is not { Length: > 0 } p) return;
        _ = LoadPosterAsync(p, "w92", bmp => row.Poster = bmp);
    }

    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly ConcurrentDictionary<string, Bitmap> Posters = new();

    private static async Task LoadPosterAsync(string path, string size, Action<Bitmap> done)
    {
        var url = $"https://image.tmdb.org/t/p/{size}{path}";
        if (Posters.TryGetValue(url, out var cached)) { done(cached); return; }
        try
        {
            var bytes = await ImageHttp.GetByteArrayAsync(url);
            var bmp = await Task.Run(() => new Bitmap(new MemoryStream(bytes)));
            Posters[url] = bmp;
            await Dispatcher.UIThread.InvokeAsync(() => done(bmp));
        }
        catch { /* senza locandina */ }
    }

    // ============================================================
    //  TMDB
    // ============================================================

    /// <summary>Stato mostrato nella scheda: null = chiave salvata, "" = nessuna chiave, altro = errore.</summary>
    private void UpdateTmdbBadge(string? error)
    {
        if (error == null && HasTmdbKey)
        {
            TmdbTone = "premium";
            TmdbBadge = "Chiave TMDB salvata";
            TmdbStatusMessage = $"Chiave {(Services.Tmdb.TmdbClient.IsReadToken(TmdbKey) ? "Token di lettura" : "API")} salvata (cifrata nel PC). Serve per i titoli e il controllo degli episodi.";
        }
        else if (string.IsNullOrEmpty(error))
        {
            TmdbTone = "off";
            TmdbBadge = "Nessuna chiave";
            TmdbStatusMessage = "Incolla la Chiave API o il Token di lettura del tuo account TMDB e premi «Verifica e salva».";
        }
        else
        {
            TmdbTone = "limited";
            TmdbBadge = "Chiave non valida";
            TmdbStatusMessage = error;
        }
    }

    [RelayCommand]
    private async Task SaveTmdbKeyAsync()
    {
        var key = (TmdbKey ?? "").Trim();
        if (key.Length == 0) { UpdateTmdbBadge("Inserisci la chiave."); return; }
        IsTmdbBusy = true;
        TmdbStatusMessage = "Verifica della chiave con TMDB...";
        try
        {
            var error = await Services.Tmdb.TmdbClient.ValidateKeyAsync(key);
            if (error != null) { UpdateTmdbBadge(error); return; }
            _keyRepo.Save(key);
            TmdbKey = key;
            HasTmdbKey = true;
            _key = key;
            UpdateTmdbBadge(null);
            if (ProgressText.StartsWith("Manca la chiave")) ProgressText = "Chiave pronta: premi «Analizza».";
                    }
        finally { IsTmdbBusy = false; }
    }

    [RelayCommand]
    private async Task TestTmdbKeyAsync()
    {
        IsTmdbBusy = true;
        TmdbStatusMessage = "Verifica della chiave con TMDB...";
        try
        {
            var error = await Services.Tmdb.TmdbClient.ValidateKeyAsync(_keyRepo.Load());
            UpdateTmdbBadge(error);
            if (error == null) TmdbStatusMessage = "La chiave salvata funziona.";
        }
        finally { IsTmdbBusy = false; }
    }

    [RelayCommand]
    private void DeleteTmdbKey()
    {
        _keyRepo.Clear();
        TmdbKey = "";
        HasTmdbKey = false;
        UpdateTmdbBadge("");
    }
}
