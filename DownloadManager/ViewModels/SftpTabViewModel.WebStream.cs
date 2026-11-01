using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Services.Sftp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DownloadManager.ViewModels;

/// <summary>
/// Verifiche dei video nel pannello di destra, su cartella aperta e sottocartelle:
/// - "Web": si riproducono nei player web? (colonna Web)
/// - "Lingua": hanno l'audio in italiano? (colonna Lingua)
/// Una lettura del file produce entrambi gli esiti. Gli esiti sono salvati nel database:
/// riaprendo l'app si vedono subito.
/// </summary>
public partial class SftpTabViewModel
{
    private WebStreamCheckerService? _webChecker;
    /// <summary>Video in lettura in questo momento (server, percorso).</summary>
    private readonly HashSet<(string Server, string Path)> _readingPaths = new();

    /// <summary>"In lettura…" sulle righe di entrambi i pannelli.</summary>
    private void UpdateAllReadingMarks()
    {
        UpdateReadingMarks(SftpRemoteEntries, RemoteConfig?.ServerKey);
        if (IsServer2Connected) UpdateReadingMarks(Server2Entries, _server2Config?.ServerKey);
        UpdateReadingMarks(SftpLocalEntries, WebStreamCheckerService.LocalKey);
    }

    /// <summary>"In lettura…" sulla riga del video, o della cartella che lo contiene.</summary>
    private void UpdateReadingMarks(IEnumerable<CheckedFileEntry> entries, string? server)
    {
        var paths = _readingPaths.Where(r => string.Equals(r.Server, server, StringComparison.OrdinalIgnoreCase))
                                 .Select(r => r.Path).ToList();
        foreach (var e in entries)
        {
            var prefix = e.EntryPath.TrimEnd('/') + "/";
            e.IsVideoChecking = paths.Count > 0 &&
                                paths.Any(p => p == e.EntryPath || (e.EntryIsDirectory && p.StartsWith(prefix, StringComparison.Ordinal)));
        }
    }
    private DispatcherTimer? _webApplyDebounce;

    [ObservableProperty] private bool _isCheckingWebStream;
    [ObservableProperty] private string _webStreamCheckStatus = string.Empty;
    /// <summary>Avanzamento per esteso (tooltip della riga di stato).</summary>
    [ObservableProperty] private string _webStreamCheckDetail = string.Empty;
    [ObservableProperty] private bool _isCheckingLanguage;

    /// <summary>Pannello della verifica in corso: il suo avanzamento si mostra nella sua riga di stato.</summary>
    public enum CheckPanel { Right, Left, Local }
    [ObservableProperty] private CheckPanel _videoCheckPanel = CheckPanel.Right;
    partial void OnVideoCheckPanelChanged(CheckPanel value) => NotifyAllStatus();

    private void NotifyAllStatus()
    {
        NotifyRemoteStatus();
        NotifyServer2Status();
        OnPropertyChanged(nameof(LocalStatusText));
        OnPropertyChanged(nameof(HasLocalStatus));
        OnPropertyChanged(nameof(ShowLocalVideoIcon));
        OnPropertyChanged(nameof(ShowServer2VideoIcon));
    }

    private bool CheckingRight => IsCheckingWebStream && VideoCheckPanel == CheckPanel.Right;
    public bool ShowServer2VideoIcon => IsCheckingWebStream && VideoCheckPanel == CheckPanel.Left && !IsDeletingServer2;
    public bool ShowLocalVideoIcon => IsCheckingWebStream && VideoCheckPanel == CheckPanel.Local;

    // ---- Esiti mostrati nelle tabelle dei file remoti (entrambi i pannelli) ----
    private Persistence.SettingsService? _displaySettings;
    /// <summary>Colonna 0 byte ed evidenziazione rossa dei file vuoti e delle cartelle che li contengono.</summary>
    [ObservableProperty] private bool _showZeroByteResults = true;
    /// <summary>Colonna Contenuto (numero di cartelle e file).</summary>
    [ObservableProperty] private bool _showContentsResults = true;
    /// <summary>Colonne Dim. e Ultima modifica (si nascondono dal tasto destro sulle intestazioni).</summary>
    [ObservableProperty] private bool _showSizeColumn = true;
    [ObservableProperty] private bool _showModifiedColumn = true;
    partial void OnShowSizeColumnChanged(bool value) => SaveDisplay(s => s.ShowSizeColumn = value);
    /// <summary>Evidenziazione delle righe (indipendente dalle colonne).</summary>
    [ObservableProperty] private bool _highlightRows = true;
    partial void OnHighlightRowsChanged(bool value) => SaveDisplay(s => s.HighlightRows = value);
    partial void OnShowModifiedColumnChanged(bool value) => SaveDisplay(s => s.ShowModifiedColumn = value);

    /// <summary>Solo il nome: tutte le altre colonne nascoste (si rimettono una per una o con "Mostra tutte").</summary>
    public void HideAllColumns()
    {
        ShowWebResults = ShowLanguageResults = ShowDurationResults = false;
        ShowZeroByteResults = ShowContentsResults = false;
        ShowSizeColumn = ShowModifiedColumn = false;
    }

    /// <summary>Tutte le colonne delle tabelle dei file di nuovo visibili.</summary>
    public void ShowAllColumns()
    {
        ShowWebResults = ShowLanguageResults = ShowDurationResults = true;
        ShowZeroByteResults = ShowContentsResults = true;
        ShowSizeColumn = ShowModifiedColumn = true;
    }
    /// <summary>Colonna Web ed evidenziazione dei video non adatti al web.</summary>
    [ObservableProperty] private bool _showWebResults = true;
    /// <summary>Colonna Lingua ed evidenziazione dei video senza italiano.</summary>
    [ObservableProperty] private bool _showLanguageResults = true;

    /// <summary>Colonna Durata e segnalazione dei video corti.</summary>
    [ObservableProperty] private bool _showDurationResults = true;
    /// <summary>Durata minima (minuti): i video più corti sono "corti". 0 = nessuna segnalazione.</summary>
    [ObservableProperty] private double _minVideoDurationMinutes = 5;

    public bool ShowsAllResults => ShowZeroByteResults && ShowContentsResults && ShowWebResults && ShowLanguageResults && ShowDurationResults;
    public string MinDurationMenuText => MinVideoDurationMinutes > 0
        ? $"Durata minima: {WebStreamCheckerService.FormatMinutes(MinVideoDurationMinutes * 60)}..."
        : "Durata minima: nessuna...";

    partial void OnShowDurationResultsChanged(bool value) => SaveDisplay(s => s.ShowDurationResults = value);
    partial void OnMinVideoDurationMinutesChanged(double value)
    {
        OnPropertyChanged(nameof(MinDurationMenuText));
        if (_webChecker != null) _webChecker.ShortThresholdSeconds = Math.Max(0, value) * 60;
        SaveDisplay(s => s.MinVideoDurationMinutes = value);
        // Le segnalazioni si ricalcolano dalle durate salvate, senza rileggere i file
        if (!_loadingDisplay) _ = ApplyWebStreamToCurrentAsync();
    }

    partial void OnShowZeroByteResultsChanged(bool value) => SaveDisplay(s => s.ShowZeroByteResults = value);
    partial void OnShowContentsResultsChanged(bool value) => SaveDisplay(s => s.ShowContentsResults = value);
    partial void OnShowWebResultsChanged(bool value) => SaveDisplay(s => s.ShowWebResults = value);
    partial void OnShowLanguageResultsChanged(bool value) => SaveDisplay(s => s.ShowLanguageResults = value);

    private bool _loadingDisplay;
    private void SaveDisplay(Action<Models.AppSettings> set)
    {
        OnPropertyChanged(nameof(ShowsAllResults));
        if (_loadingDisplay || _displaySettings == null) return;
        set(_displaySettings.Current);
        try { _displaySettings.Save(); } catch { }
    }

    /// <summary>Mostra tutti gli esiti, oppure li nasconde tutti se erano già tutti visibili.</summary>
    [RelayCommand]
    private void ToggleAllResults()
    {
        var show = !ShowsAllResults;
        ShowZeroByteResults = show;
        ShowContentsResults = show;
        ShowWebResults = show;
        ShowLanguageResults = show;
        ShowDurationResults = show;
    }

    /// <summary>Icona della riga di stato: pellicola o globo durante le verifiche.</summary>
    public bool ShowWebStreamStatusIcon => !IsDeletingRemote && CheckingRight && !IsCheckingLanguage && !IsCheckingZeroByte;
    public bool ShowLanguageStatusIcon => !IsDeletingRemote && CheckingRight && IsCheckingLanguage && !IsCheckingZeroByte;
    public bool ShowZeroByteStatusIcon => !IsDeletingRemote && IsCheckingZeroByte;
    public bool ShowRefreshStatusIcon => !IsDeletingRemote && !CheckingRight && !IsCheckingZeroByte;

    /// <summary>Tooltip della riga di stato: il dettaglio della verifica, altrimenti il testo intero.</summary>
    public string? RemoteStatusTooltip => !IsDeletingRemote && CheckingRight && !IsCheckingZeroByte && !string.IsNullOrEmpty(WebStreamCheckDetail)
        ? WebStreamCheckDetail
        : string.IsNullOrEmpty(RemoteStatusText) ? null : RemoteStatusText;

    private void InitWebStream()
    {
        _displaySettings = App.Services?.GetService<Persistence.SettingsService>();
        if (_displaySettings != null)
        {
            _loadingDisplay = true;
            ShowZeroByteResults = _displaySettings.Current.ShowZeroByteResults;
            ShowContentsResults = _displaySettings.Current.ShowContentsResults;
            ShowSizeColumn = _displaySettings.Current.ShowSizeColumn;
            HighlightRows = _displaySettings.Current.HighlightRows;
            ShowModifiedColumn = _displaySettings.Current.ShowModifiedColumn;
            ShowWebResults = _displaySettings.Current.ShowWebResults;
            ShowLanguageResults = _displaySettings.Current.ShowLanguageResults;
            ShowDurationResults = _displaySettings.Current.ShowDurationResults;
            MinVideoDurationMinutes = _displaySettings.Current.MinVideoDurationMinutes;
            _loadingDisplay = false;
        }

        _webChecker = App.Services?.GetService<WebStreamCheckerService>();
        if (_webChecker == null) return;
        _webChecker.ShortThresholdSeconds = Math.Max(0, MinVideoDurationMinutes) * 60;

        _webChecker.StateChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            IsCheckingLanguage = _webChecker.CheckingKind == WebStreamCheckKind.Language;
            IsCheckingWebStream = _webChecker.IsChecking;
            WebStreamCheckStatus = _webChecker.ProgressStatus;
            WebStreamCheckDetail = _webChecker.ProgressDetail;
        });
        _webChecker.Completed += (_, message) => Dispatcher.UIThread.Post(async () =>
        {
            _webApplyDebounce?.Stop();
            _readingPaths.Clear();
            UpdateAllReadingMarks();
            // Si rilegge la cartella (come uscire e rientrare): righe e conteggi tornano allineati
            // con gli esiti salvati anche dopo uno stop a metà
            try { await SftpRefreshInternalAsync(silent: true); } catch { }
            try { if (IsServer2Connected) await Server2RefreshInternalAsync(silent: true); } catch { }
            try { await SftpRefreshLocalInternalAsync(silent: true); } catch { }
            SftpStatusMessage = message;
        });
        _webChecker.FileChecked += (serverKey, result) => Dispatcher.UIThread.Post(() => OnWebFileChecked(serverKey, result));
        _webChecker.FileReading += (serverKey, path, started) => Dispatcher.UIThread.Post(() =>
        {
            if (started) _readingPaths.Add((serverKey, path));
            else _readingPaths.Remove((serverKey, path));
            UpdateAllReadingMarks();
        });

        // Durante la verifica i conteggi delle cartelle si ricalcolano una volta al secondo
        _webApplyDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _webApplyDebounce.Tick += (_, _) =>
        {
            _webApplyDebounce.Stop();
            _ = ApplyWebStreamToCurrentAsync();
        };
    }

    partial void OnIsCheckingWebStreamChanged(bool value)
    {
        NotifyAllStatus();
        OnPropertyChanged(nameof(RemoteStatusText));
        OnPropertyChanged(nameof(HasRemoteStatus));
        OnPropertyChanged(nameof(RemoteStatusTooltip));
        NotifyStatusIcons();
    }

    partial void OnIsCheckingLanguageChanged(bool value) => NotifyStatusIcons();

    private void NotifyStatusIcons()
    {
        OnPropertyChanged(nameof(ShowWebStreamStatusIcon));
        OnPropertyChanged(nameof(ShowLanguageStatusIcon));
        OnPropertyChanged(nameof(ShowZeroByteStatusIcon));
        OnPropertyChanged(nameof(ShowRefreshStatusIcon));
    }

    partial void OnWebStreamCheckStatusChanged(string value) => NotifyAllStatus();
    partial void OnWebStreamCheckDetailChanged(string value) => OnPropertyChanged(nameof(RemoteStatusTooltip));

    /// <summary>Nessun controllo dei video in corso (per i pannelli di sinistra e locale)? Altrimenti spiega perché.</summary>
    public bool CanStartVideoCheckAnywhere()
    {
        if (_webChecker == null) { SftpStatusMessage = "Controllo dei video non disponibile."; return false; }
        if (_webChecker.IsChecking)
        {
            SftpStatusMessage = "Un controllo dei video è già in corso: fermalo prima di avviarne un altro.";
            return false;
        }
        return true;
    }

    /// <summary>Si può avviare un controllo dei video (server aperto e nessun controllo in corso)? Altrimenti spiega perché.</summary>
    public bool CanStartVideoCheck()
    {
        if (_webChecker == null) { SftpStatusMessage = "Controllo dei video non disponibile."; return false; }
        if (!IsRemoteConnected || RemoteConfig == null)
        {
            SftpStatusMessage = "Nessun server aperto: connettine uno nel pannello di destra.";
            return false;
        }
        if (_webChecker.IsChecking)
        {
            SftpStatusMessage = "Un controllo dei video è già in corso: fermalo prima di avviarne un altro.";
            return false;
        }
        return true;
    }

    [RelayCommand]
    private void StopWebStreamCheck() => _webChecker?.Stop();

    /// <summary>Verifica solo i file e le cartelle selezionati (riletti anche se già controllati).</summary>
    /// <param name="left">Pannello di sinistra (altrimenti quello di destra).</param>
    public void StartVideoCheckOnSelection(bool left, IReadOnlyList<SftpRemoteEntry> targets, bool web, bool lang, bool duration = false)
    {
        if (targets.Count == 0) return;
        var server = left ? _server2Config : RemoteConfig;
        if (_webChecker == null || server == null || (left ? !IsServer2Connected : !IsRemoteConnected))
        {
            SftpStatusMessage = "Nessun server aperto in questo pannello.";
            return;
        }
        if (_webChecker.IsChecking)
        {
            SftpStatusMessage = "Un controllo dei video è già in corso: fermalo prima di avviarne un altro.";
            return;
        }
        var listing = left ? _server2 : Remote;
        var folder = left ? (Server2Path ?? "/") : SftpCurrentRemotePath;
        var what = WebStreamCheckerService.CheckName(web, lang, duration);
        SftpStatusMessage = targets.Count == 1 ? $"{what} di {targets[0].Name}..." : $"{what} di {targets.Count} elementi...";
        VideoCheckPanel = left ? CheckPanel.Left : CheckPanel.Right;
        _ = _webChecker.RunCheckAsync(server, listing, folder, web, lang, recheckAll: true, targets: targets, duration: duration);
    }

    /// <summary>Verifica di tutta la cartella aperta nel pannello di sinistra (server).</summary>
    public void StartVideoCheckLeft(bool web, bool lang, bool recheckAll, bool duration = false)
    {
        if (_webChecker == null || _server2Config == null || !IsServer2Connected) { SftpStatusMessage = "Collega prima il server nel pannello di sinistra."; return; }
        if (_webChecker.IsChecking) { SftpStatusMessage = "Un controllo dei video è già in corso: fermalo prima di avviarne un altro."; return; }
        var root = string.IsNullOrWhiteSpace(Server2Path) ? "/" : Server2Path;
        var what = WebStreamCheckerService.CheckName(web, lang, duration);
        SftpStatusMessage = $"{what} dei video in {root} su '{Server2Name}'...";
        VideoCheckPanel = CheckPanel.Left;
        _ = _webChecker.RunCheckAsync(_server2Config, _server2, root, web, lang, recheckAll, duration: duration);
    }

    /// <summary>Verifica dei video sul PC: cartella aperta (targets null) o elementi selezionati.</summary>
    public void StartVideoCheckLocal(bool web, bool lang, bool recheckAll, IReadOnlyList<LocalFileEntry>? targets = null, bool duration = false)
    {
        if (_webChecker == null) return;
        if (_webChecker.IsChecking) { SftpStatusMessage = "Un controllo dei video è già in corso: fermalo prima di avviarne un altro."; return; }
        if (string.IsNullOrWhiteSpace(SftpCurrentLocalPath) || !Directory.Exists(SftpCurrentLocalPath)) { SftpStatusMessage = "Apri una cartella del PC."; return; }
        var what = WebStreamCheckerService.CheckName(web, lang, duration);
        var remoteTargets = targets?.Select(t => new SftpRemoteEntry
        {
            Name = t.Name, FullPath = t.EntryPath, IsDirectory = t.IsDirectory, Size = t.Size, Modified = t.Modified
        }).ToList();
        SftpStatusMessage = remoteTargets == null ? $"{what} dei video in {SftpCurrentLocalPath}..."
            : remoteTargets.Count == 1 ? $"{what} di {remoteTargets[0].Name}..." : $"{what} di {remoteTargets.Count} elementi...";
        VideoCheckPanel = CheckPanel.Local;
        _ = _webChecker.RunLocalCheckAsync(SftpCurrentLocalPath, web, lang, recheckAll || targets != null, remoteTargets, duration);
    }

    /// <param name="web">Codec (colonna Web).</param>
    /// <param name="lang">Lingua dell'audio (colonna Lingua).</param>
    public void StartVideoCheck(bool web, bool lang, bool recheckAll, bool duration = false)
    {
        if (!CanStartVideoCheck() || _webChecker == null || RemoteConfig == null) return;
        var what = WebStreamCheckerService.CheckName(web, lang, duration);
        var root = string.IsNullOrWhiteSpace(SftpCurrentRemotePath) ? "/" : SftpCurrentRemotePath;
        SftpStatusMessage = recheckAll ? $"{what} di tutti i video in {root}..." : $"{what} dei video in {root}...";
        VideoCheckPanel = CheckPanel.Right;
        _ = _webChecker.RunCheckAsync(RemoteConfig, Remote, root, web, lang, recheckAll, duration: duration);
    }

    private void OnWebFileChecked(string serverKey, WebStreamResult result)
    {
        if (serverKey == WebStreamCheckerService.LocalKey)
        {
            var local = SftpLocalEntries.FirstOrDefault(e => !e.IsDirectory && e.EntryPath == result.FullPath);
            if (local != null)
            {
                local.SetWebStream(result.Level, result.ShortText, result.Detail);
                local.SetLanguage(result.LangLevel, result.LangText, result.LangDetail);
                var (dl, dt, dd) = DurationOfResult(result);
                local.SetDuration(dl, dt, dd);
            }
            else if (_webApplyDebounce is { IsEnabled: false }) _webApplyDebounce.Start();
            return;
        }

        // Pannello di sinistra sullo stesso server: stessa riga aggiornata
        if (IsServer2Connected && string.Equals(_server2Config?.ServerKey, serverKey, StringComparison.OrdinalIgnoreCase))
        {
            var left = Server2Entries.FirstOrDefault(e => !e.IsDirectory && e.FullPath == result.FullPath);
            if (left != null)
            {
                left.SetWebStream(result.Level, result.ShortText, result.Detail);
                left.SetLanguage(result.LangLevel, result.LangText, result.LangDetail);
                var (ldl, ldt, ldd) = DurationOfResult(result);
                left.SetDuration(ldl, ldt, ldd);
            }
            else if (result.FullPath.StartsWith((Server2Path ?? "/").TrimEnd('/') + "/", StringComparison.Ordinal)
                     && _webApplyDebounce is { IsEnabled: false })
                _webApplyDebounce.Start();
        }

        if (RemoteConfig == null || !string.Equals(RemoteConfig.ServerKey, serverKey, StringComparison.OrdinalIgnoreCase))
            return;

        var entry = SftpRemoteEntries.FirstOrDefault(e => !e.IsDirectory && e.FullPath == result.FullPath);
        if (entry != null)
        {
            entry.SetWebStream(result.Level, result.ShortText, result.Detail);
            entry.SetLanguage(result.LangLevel, result.LangText, result.LangDetail);
            var (edl, edt, edd) = DurationOfResult(result);
            entry.SetDuration(edl, edt, edd);
        }
        else if (result.FullPath.StartsWith(SftpCurrentRemotePath.TrimEnd('/') + "/", StringComparison.Ordinal))
        {
            // File in una sottocartella: si aggiorna il conteggio della cartella entro un secondo.
            // Il timer non si fa ripartire: con tre video alla volta non scatterebbe mai.
            if (_webApplyDebounce is { IsEnabled: false }) _webApplyDebounce.Start();
        }
    }

    /// <summary>Esiti salvati applicati alle righe della cartella aperta.</summary>
    private async Task ApplyWebStreamToCurrentAsync()
    {
        if (RemoteConfig != null)
            await ApplyWebStreamAsync(SftpRemoteEntries.ToList(), SftpCurrentRemotePath, RemoteConfig.ServerKey);
        if (IsServer2Connected && _server2Config != null)
            await ApplyWebStreamAsync(Server2Entries.ToList(), Server2Path ?? "/", _server2Config.ServerKey);
        if (!string.IsNullOrWhiteSpace(SftpCurrentLocalPath))
            await ApplyWebStreamAsync(SftpLocalEntries.ToList(), SftpCurrentLocalPath.Replace('\\', '/'), WebStreamCheckerService.LocalKey);
    }

    /// <summary>
    /// File: esiti salvati se dimensione e data non sono cambiate, altrimenti "da ricontrollare".
    /// Cartelle: conteggio degli esiti dei video che contengono (anche nelle sottocartelle).
    /// </summary>
    /// <param name="cleanup">
    /// <paramref name="entries"/> è l'elenco appena letto della cartella: gli esiti di file e
    /// sottocartelle che non ci sono più si tolgono, e un file rinominato da fuori (stessa
    /// dimensione e data di un esito rimasto senza file) si riprende il suo esito.
    /// </param>
    private async Task ApplyWebStreamAsync(IReadOnlyList<CheckedFileEntry> entries, string folder, string serverKey, bool cleanup = false)
    {
        if (_webChecker == null || entries.Count == 0) return;
        var root = string.IsNullOrWhiteSpace(folder) ? "/" : folder;
        var results = await _webChecker.Repository.GetUnderAsync(serverKey, root);

        if (cleanup)
            results = await CleanupWithListingAsync(serverKey, root, entries, results);

        var byPath = results.ToDictionary(r => r.FullPath, StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!e.EntryIsDirectory)
            {
                if (!byPath.TryGetValue(e.EntryPath, out var r))
                {
                    e.SetWebStream(WebStreamLevel.None, "", "");
                    e.SetLanguage(WebStreamLevel.None, "", "");
                    e.SetDuration(WebStreamLevel.None, "", "");
                }
                else if (r.Matches(e.EntrySize, e.EntryModified))
                {
                    e.SetWebStream(r.Level, r.ShortText, r.Detail);
                    e.SetLanguage(r.LangLevel, r.LangText, r.LangDetail);
                    var (dl, dt, dd) = DurationOfResult(r);
                    e.SetDuration(dl, dt, dd);
                }
                else
                {
                    const string changed = "Il file è cambiato dopo l'ultimo controllo: rilancialo.";
                    e.SetWebStream(r.Level == WebStreamLevel.None ? WebStreamLevel.None : WebStreamLevel.Stale,
                        r.Level == WebStreamLevel.None ? "" : "Da ricontrollare", changed);
                    e.SetLanguage(r.LangLevel == WebStreamLevel.None ? WebStreamLevel.None : WebStreamLevel.Stale,
                        r.LangLevel == WebStreamLevel.None ? "" : "Da ricontrollare", changed);
                    e.SetDuration(r.Duration == null ? WebStreamLevel.None : WebStreamLevel.Stale,
                        r.Duration == null ? "" : "Da ricontrollare", changed);
                }
                continue;
            }

            var prefix = e.EntryPath.TrimEnd('/') + "/";
            var inside = results.Where(r => r.FullPath.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            var (webLevel, webInfo, webDetail) = Summarize(inside, r => r.Level, "non idone", "o", "i", "video OK");
            e.SetWebStream(webLevel, webInfo, webDetail);
            var (langLevel, langInfo, langDetail) = Summarize(inside, r => r.LangLevel, "senza italiano", "", "", "con italiano");
            e.SetLanguage(langLevel, langInfo, langDetail);
            var (durLevel, durInfo, durDetail) = SummarizeDuration(inside);
            e.SetDuration(durLevel, durInfo, durDetail);
        }
        UpdateReadingMarks(entries, serverKey);
    }

    /// <summary>Pulizia con l'elenco appena letto della cartella (vedi ApplyWebStreamAsync).</summary>
    private async Task<List<WebStreamResult>> CleanupWithListingAsync(string serverKey, string folder,
        IReadOnlyList<CheckedFileEntry> entries, List<WebStreamResult> results)
    {
        var dir = folder.TrimEnd('/');
        // Sicurezza: l'elenco deve essere davvero quello di questa cartella
        if (entries.Any(e => !string.Equals(TransferJobBase.GetRemoteParent(e.EntryPath).TrimEnd('/'), dir, StringComparison.Ordinal)))
            return results;

        var files = entries.Where(e => !e.EntryIsDirectory).ToDictionary(e => e.EntryPath, StringComparer.Ordinal);
        var dirs = entries.Where(e => e.EntryIsDirectory).Select(e => e.EntryPath.TrimEnd('/')[(e.EntryPath.TrimEnd('/').LastIndexOf('/') + 1)..]).ToHashSet(StringComparer.Ordinal);
        var known = results.Select(r => r.FullPath).ToHashSet(StringComparer.Ordinal);

        var orphans = new List<WebStreamResult>();
        foreach (var r in results)
        {
            var rest = r.FullPath.Substring(dir.Length + 1);
            int slash = rest.IndexOf('/');
            bool gone = slash < 0 ? !files.ContainsKey(r.FullPath) : !dirs.Contains(rest[..slash]);
            if (gone) orphans.Add(r);
        }
        if (orphans.Count == 0) return results;

        // Rinominati da fuori nella stessa cartella: stessa dimensione e data, file senza esito
        var unclaimed = files.Values.Where(f => !known.Contains(f.EntryPath)).ToList();
        var moved = new List<(string From, string To)>();
        foreach (var o in orphans.Where(o => !o.FullPath.Substring(dir.Length + 1).Contains('/')).ToList())
        {
            var matches = unclaimed.Where(f => o.Matches(f.EntrySize, f.EntryModified)).ToList();
            if (matches.Count != 1) continue;
            moved.Add((o.FullPath, matches[0].EntryPath));
            unclaimed.Remove(matches[0]);
            orphans.Remove(o);
        }

        foreach (var (from, to) in moved)
            await _webChecker!.Repository.MoveAsync(serverKey, from, to);
        await _webChecker!.Repository.DeleteManyAsync(serverKey, orphans.Select(o => o.FullPath).ToList());

        return moved.Count == 0 && orphans.Count == 0
            ? results
            : await _webChecker.Repository.GetUnderAsync(serverKey, folder);
    }

    private double ShortThresholdSeconds => Math.Max(0, MinVideoDurationMinutes) * 60;

    /// <summary>Colonna Durata di un file: "23:41", oppure "3:05 · corto" sotto la soglia.</summary>
    private (WebStreamLevel Level, string Text, string Detail) DurationOfResult(WebStreamResult r)
    {
        if (r.ShortText == WebStreamCheckerService.DamagedText) return (WebStreamLevel.Bad, r.ShortText, r.Detail);
        if (r.Duration is not { } d || r.Level == WebStreamLevel.Unreadable) return (WebStreamLevel.None, "", "");
        var text = (r.DurationApprox ? "~" : "") + WebStreamCheckerService.FormatDuration(d);
        var threshold = ShortThresholdSeconds;
        var approx = r.DurationApprox ? "\nDurata stimata dalla dimensione del file (formato senza durata scritta)." : "";
        if (threshold > 0 && d < threshold)
            return (WebStreamLevel.Warning, text + " · corto",
                $"Durata {text}: più corto di {WebStreamCheckerService.FormatMinutes(threshold)} (soglia nel menu con l'occhio)." + approx);
        return (WebStreamLevel.Ok, text, $"Durata {text}" + approx);
    }

    /// <summary>Colonna Durata di una cartella: "2 sotto 5 min" oppure il numero di video e la durata totale.</summary>
    private (WebStreamLevel Level, string Text, string Detail) SummarizeDuration(IReadOnlyList<WebStreamResult> inside)
    {
        var known = inside.Where(r => r.Duration != null && r.Level != WebStreamLevel.Unreadable
                                      && r.Size != Persistence.WebStreamCheckRepository.ChangedMarker).ToList();
        int damaged = inside.Count(r => r.ShortText == WebStreamCheckerService.DamagedText
                                        && r.Size != Persistence.WebStreamCheckRepository.ChangedMarker);
        var damagedText = damaged == 1 ? "1 danneggiato" : $"{damaged} danneggiati";
        if (known.Count == 0)
            return damaged > 0
                ? (WebStreamLevel.Bad, damagedText, $"{damagedText}: vanno riscaricati (o ricaricati) per intero.")
                : (WebStreamLevel.None, "", "");
        var total = known.Sum(r => r.Duration!.Value);
        var threshold = ShortThresholdSeconds;
        int shortCount = threshold > 0 ? known.Count(r => r.Duration < threshold) : 0;
        var detail = $"{known.Count} video con durata letta, in tutto {WebStreamCheckerService.FormatDuration(total)}"
                     + (threshold > 0 ? $"\nPiù corti di {WebStreamCheckerService.FormatMinutes(threshold)}: {shortCount}" : "")
                     + (damaged > 0 ? $"\nDanneggiati (da riscaricare): {damaged}" : "");
        if (damaged > 0) return (WebStreamLevel.Bad, damagedText, detail);
        return shortCount > 0
            ? (WebStreamLevel.Warning, $"{shortCount} sotto {WebStreamCheckerService.FormatMinutes(threshold)}", detail)
            : (WebStreamLevel.Ok, $"{known.Count} · {WebStreamCheckerService.FormatDuration(total)}", detail);
    }

    /// <summary>Riassunto di una cartella: "3 non idonei · 2 da verificare" oppure "12 video OK".</summary>
    private static (WebStreamLevel Level, string Info, string Detail) Summarize(IReadOnlyList<WebStreamResult> results,
        Func<WebStreamResult, WebStreamLevel> levelOf, string badWord, string badOne, string badMany, string okWord)
    {
        int ok = 0, warn = 0, bad = 0, unreadable = 0, changed = 0, damaged = 0;
        DateTime first = DateTime.MaxValue, last = DateTime.MinValue;
        foreach (var r in results)
        {
            var l = levelOf(r);
            if (l == WebStreamLevel.None) continue;
            if (r.CheckedAt > DateTime.MinValue)
            {
                if (r.CheckedAt < first) first = r.CheckedAt;
                if (r.CheckedAt > last) last = r.CheckedAt;
            }
            // Cambiato da fuori dopo il controllo (visto dall'allineamento): l'esito non vale più
            if (r.Size == Persistence.WebStreamCheckRepository.ChangedMarker) { changed++; continue; }
            // File danneggiato: conta a parte, sia in Web sia in Lingua
            if (r.ShortText == WebStreamCheckerService.DamagedText) { damaged++; continue; }
            switch (l)
            {
                case WebStreamLevel.Ok: ok++; break;
                case WebStreamLevel.Warning: warn++; break;
                case WebStreamLevel.Bad: bad++; break;
                case WebStreamLevel.Unreadable: unreadable++; break;
            }
        }
        int total = ok + warn + bad + unreadable + changed + damaged;
        if (total == 0) return (WebStreamLevel.None, "", "");

        var parts = new List<string>();
        if (damaged > 0) parts.Add($"{damaged} danneggiat{(damaged == 1 ? "o" : "i")}");
        if (bad > 0) parts.Add($"{bad} {badWord}{(bad == 1 ? badOne : badMany)}");
        if (warn > 0) parts.Add($"{warn} da verificare");
        if (changed > 0) parts.Add($"{changed} da ricontrollare");
        if (unreadable > 0) parts.Add($"{unreadable} non leggibil{(unreadable == 1 ? "e" : "i")}");

        var level = bad + damaged > 0 ? WebStreamLevel.Bad
            : warn > 0 ? WebStreamLevel.Warning
            : changed > 0 ? WebStreamLevel.Stale
            : unreadable > 0 ? WebStreamLevel.Unreadable
            : WebStreamLevel.Ok;
        var info = parts.Count == 0 ? $"{ok} {okWord}" : string.Join(" · ", parts);
        var when = first == DateTime.MaxValue ? ""
            : first.Date == last.Date ? $"\nControllati il {first:dd/MM/yyyy}"
            : $"\nControllati tra il {first:dd/MM/yyyy} e il {last:dd/MM/yyyy}";
        var detail = $"{total} video controllati in questa cartella e nelle sottocartelle:\n"
                     + (damaged > 0 ? $"  danneggiati (da riscaricare): {damaged}\n" : "")
                     + $"  {badWord}{(bad == 1 ? badOne : badMany)}: {bad}\n  da verificare: {warn}\n"
                     + $"  da ricontrollare (cambiati dopo il controllo): {changed}\n  non leggibili: {unreadable}\n  OK: {ok}" + when;
        return (level, info, detail);
    }

    // ---- Gli esiti seguono i file rinominati, spostati, eliminati dall'app ----

    private async Task WebStreamMovedAsync(string oldPath, string newPath)
    {
        if (RemoteConfig == null) return;
        if (_webChecker != null) await _webChecker.Repository.MoveAsync(RemoteConfig.ServerKey, oldPath, newPath);
        if (ZeroStore is { } zero) await zero.MoveAsync(RemoteConfig.ServerKey, oldPath, newPath);
    }

    private static Persistence.ZeroByteCheckRepository? ZeroStore =>
        App.Services?.GetService<Persistence.ZeroByteCheckRepository>();

    private Task WebStreamDeletedAsync(string path) => WebStreamDeletedAsync(path, RemoteConfig);

    private async Task WebStreamDeletedAsync(string path, SftpConfig? server)
    {
        if (server == null) return;
        if (_webChecker != null) await _webChecker.Repository.DeleteAsync(server.ServerKey, path);
        if (ZeroStore is { } zero) await zero.DeleteAsync(server.ServerKey, path);
    }
}
