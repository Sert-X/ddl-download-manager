using System.Diagnostics;
using System.Text.Json;
using DownloadManager.Helpers;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Download;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace DownloadManager.Services.Sftp;

/// <summary>Esito di una verifica. None = mai controllato, Stale = il file è cambiato dopo il controllo.</summary>
public enum WebStreamLevel { None = 0, Ok = 1, Warning = 2, Bad = 3, Unreadable = 4, Stale = 5 }

/// <summary>Quale controllo ha chiesto l'utente (la lettura del file produce comunque entrambi gli esiti).</summary>
public enum WebStreamCheckKind { Web, Language }

/// <param name="Size">Dimensione del file al momento del controllo.</param>
/// <param name="Modified">Data del file al momento del controllo (come la legge il browser SFTP).</param>
/// <param name="LangLevel">Esito del controllo lingua (None = mai controllata).</param>
/// <param name="RulesVersion">Versione delle regole con cui è stato calcolato l'esito.</param>
public sealed record WebStreamResult(
    string FullPath, WebStreamLevel Level, string ShortText, string Detail,
    long Size, DateTime Modified, DateTime CheckedAt,
    WebStreamLevel LangLevel = WebStreamLevel.None, string LangText = "", string LangDetail = "",
    int RulesVersion = WebStreamCheckerService.RulesVersion,
    double? Duration = null, bool DurationApprox = false)
{
    /// <summary>L'esito vale ancora per il file con questa dimensione e data.</summary>
    public bool Matches(long size, DateTime modified) =>
        size == Size && Math.Abs((modified - Modified).TotalSeconds) < 2;

    public WebStreamLevel LevelOf(WebStreamCheckKind kind) => kind == WebStreamCheckKind.Web ? Level : LangLevel;
}

/// <summary>
/// Controlla i video di una cartella remota (e sottocartelle): se si riproducono nei player web
/// (codec video e audio) e se hanno l'audio in italiano. Non scarica i video: legge solo
/// l'intestazione (MKV, AVI, TS...) o l'indice "moov" (MP4/MOV, anche se sta in fondo) con una
/// connessione SSH.NET a parte, e passa quei pochi MB all'ffprobe locale. Tre video alla volta.
/// Gli esiti vengono salvati nel database e i file già controllati (stessa dimensione e data)
/// non si rileggono.
/// </summary>
public sealed class WebStreamCheckerService
{
    /// <summary>2 = aggiunto il controllo della lingua; 3 = un'unica traccia "inglese" è "da verificare".</summary>
    public const int RulesVersion = 3;

    private const int MaxDepth = 6;
    private const int MaxVideos = 50_000;
    private const int Parallel = 3;
    private const int HeadBytes = 2 * 1024 * 1024;
    private const int RetryHeadBytes = 16 * 1024 * 1024;
    private const int Mp4DataBytes = 1024 * 1024;
    private const long MaxMoovBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan PerFileTimeout = TimeSpan.FromSeconds(120);

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".ts", ".m2ts", ".mts", ".wmv", ".flv", ".mpg", ".mpeg" };

    private static readonly HashSet<string> Mp4Family = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };

    private readonly WebStreamCheckRepository _repo;
    private readonly FfmpegLocator _ffmpeg;
    private readonly SftpSessionLimiter _limiter;
    private readonly ILogger<WebStreamCheckerService> _logger;
    private CancellationTokenSource? _cts;

    public WebStreamCheckerService(WebStreamCheckRepository repo, FfmpegLocator ffmpeg,
        SftpSessionLimiter limiter, ILogger<WebStreamCheckerService> logger)
    {
        _repo = repo;
        _ffmpeg = ffmpeg;
        _limiter = limiter;
        _logger = logger;
    }

    public WebStreamCheckRepository Repository => _repo;

    public bool IsChecking { get; private set; }
    /// <summary>Avanzamento breve, es. "Web 125/4421".</summary>
    public string ProgressStatus { get; private set; } = string.Empty;
    /// <summary>Avanzamento per esteso, con i file in lettura (per il tooltip).</summary>
    public string ProgressDetail { get; private set; } = string.Empty;
    public WebStreamCheckKind CheckingKind { get; private set; }
    /// <summary>Server (o <see cref="LocalKey"/>) del controllo in corso.</summary>
    public string? CheckingServerKey { get; private set; }

    public event EventHandler? StateChanged;
    public event EventHandler<string>? Completed;
    /// <summary>Un video è stato controllato (l'esito è già salvato). Il primo argomento è il ServerKey.</summary>
    public event Action<string, WebStreamResult>? FileChecked;
    /// <summary>Un video inizia (true) o finisce (false) di essere letto: ServerKey, percorso.</summary>
    public event Action<string, string, bool>? FileReading;

    public void Stop()
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        ProgressStatus = "Interruzione...";
        ProgressDetail = "Interruzione in corso: si attende la fine dei video in lettura.";
        RaiseStateChanged();
    }

    /// <summary>Durata sotto la quale un video si segnala come corto (secondi; 0 = mai). La imposta l'interfaccia.</summary>
    public double ShortThresholdSeconds { get; set; }

    public static string CheckLabel(bool web, bool lang, bool duration)
    {
        var parts = new List<string>();
        if (web) parts.Add("Web");
        if (lang) parts.Add(parts.Count == 0 ? "Lingua" : "lingua");
        if (duration) parts.Add(parts.Count == 0 ? "Durata" : "durata");
        return string.Join("+", parts);
    }

    public static string CheckName(bool web, bool lang, bool duration)
    {
        var parts = new List<string>();
        if (web) parts.Add("codec");
        if (lang) parts.Add("lingua");
        if (duration) parts.Add("durata");
        if (parts.Count == 1) return web ? "Verifica streaming" : lang ? "Controllo lingua" : "Controllo durata";
        return "Verifica " + (parts.Count == 2 ? $"{parts[0]} e {parts[1]}" : $"{parts[0]}, {parts[1]} e {parts[2]}");
    }

    public static string FormatMinutes(double seconds) =>
        seconds % 60 == 0 ? $"{seconds / 60:0} min" : $"{seconds / 60:0.#} min";

    /// <summary>Durata leggibile: 1:23:45 oppure 23:41.</summary>
    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>Il video va letto per i controlli richiesti.</summary>
    private static bool NeedsRead(WebStreamResult? saved, SftpRemoteEntry v, bool web, bool lang, bool duration, bool recheckAll)
    {
        if (recheckAll || saved == null || !saved.Matches(v.Size, v.Modified)) return true;
        // Durata mai letta (esiti di prima di questa funzione) o file illeggibile l'ultima volta
        if (duration && (saved.Duration == null || saved.Level == WebStreamLevel.Unreadable)) return true;
        if (web && saved.Level is WebStreamLevel.None or WebStreamLevel.Unreadable) return true;
        if (lang)
        {
            if (saved.LangLevel is WebStreamLevel.None or WebStreamLevel.Unreadable) return true;
            // Regole cambiate (v3: un'unica traccia "inglese" non è più "Senza italiano"):
            // si rileggono solo gli esiti che la nuova regola può cambiare
            if (saved.RulesVersion < RulesVersion && saved.LangLevel == WebStreamLevel.Bad) return true;
        }
        return false;
    }

    /// <summary>
    /// Esito da salvare e mostrare: le parti non richieste restano quelle già salvate (se il file
    /// non è cambiato), altrimenti vuote.
    /// </summary>
    private static WebStreamResult Merge(WebStreamResult fresh, WebStreamResult? saved, bool web, bool lang, bool duration)
    {
        var keep = saved != null && saved.Matches(fresh.Size, fresh.Modified) ? saved : null;
        var r = fresh;
        if (!web)
            r = r with
            {
                Level = keep?.Level ?? WebStreamLevel.None,
                ShortText = keep?.ShortText ?? "",
                Detail = keep?.Detail ?? ""
            };
        if (!lang)
            r = r with
            {
                LangLevel = keep?.LangLevel ?? WebStreamLevel.None,
                LangText = keep?.LangText ?? "",
                LangDetail = keep?.LangDetail ?? "",
                // La lingua tenuta è stata calcolata con le regole di allora
                RulesVersion = keep?.RulesVersion ?? RulesVersion
            };
        // Durata non richiesta: si tiene quella già salvata (o nessuna), anche se la lettura la conosce
        if (!duration)
            r = r with
            {
                Duration = keep?.Duration,
                DurationApprox = keep?.DurationApprox ?? false
            };
        return r;
    }

    /// <param name="listing">Servizio SFTP del browser: le cartelle si leggono da lì, così dimensione e data coincidono con quelle mostrate.</param>
    /// <param name="web">Controlla i codec (colonna Web).</param>
    /// <param name="lang">Controlla la lingua dell'audio (colonna Lingua).</param>
    /// <param name="recheckAll">Rilegge anche i video già controllati.</param>
    /// <param name="targets">Solo questi file e cartelle (della cartella <paramref name="rootFolder"/>); null = tutta la cartella.</param>
    public Task RunCheckAsync(SftpConfig server, ISftpService listing, string rootFolder,
        bool web, bool lang, bool recheckAll = false, IReadOnlyList<SftpRemoteEntry>? targets = null, bool duration = false)
    {
        var connection = new SharedConnection(server, _limiter);
        return RunCoreAsync(server.ServerKey, server.Name, rootFolder, web, lang, duration, recheckAll, targets,
            (folder, ct) => listing.ListDirectoryAsync(folder, ct),
            (ffprobe, v, ct) => ProbeWithRetryAsync(connection, ffprobe, v, ct),
            connection);
    }

    /// <summary>Chiave degli esiti dei file sul PC (al posto del server).</summary>
    public const string LocalKey = "local";

    /// <summary>
    /// Stessa verifica sui file del PC: si leggono direttamente con ffprobe (che legge solo
    /// l'intestazione). Percorsi con "/" (es. "C:/Video/film.mkv"), come nel database.
    /// </summary>
    public Task RunLocalCheckAsync(string rootFolder, bool web, bool lang, bool recheckAll = false,
        IReadOnlyList<SftpRemoteEntry>? targets = null, bool duration = false)
        => RunCoreAsync(LocalKey, "PC", rootFolder.Replace('\\', '/'), web, lang, duration, recheckAll, targets,
            (folder, ct) => Task.Run(() => ListLocal(folder), ct),
            (ffprobe, v, ct) => ProbeLocalAsync(ffprobe, v, ct),
            null);

    /// <summary>Elenco di una cartella del PC come righe "remote" (percorsi con "/").</summary>
    public static List<SftpRemoteEntry> ListLocal(string folder)
    {
        var list = new List<SftpRemoteEntry>();
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
        foreach (var info in new DirectoryInfo(folder.Replace('/', Path.DirectorySeparatorChar)).EnumerateFileSystemInfos("*", options))
        {
            bool isDir = info is DirectoryInfo;
            list.Add(new SftpRemoteEntry
            {
                Name = info.Name,
                FullPath = info.FullName.Replace('\\', '/'),
                IsDirectory = isDir,
                Size = isDir ? 0 : ((FileInfo)info).Length,
                Modified = info.LastWriteTime
            });
        }
        return list;
    }

    private static async Task<WebStreamResult> ProbeLocalAsync(string ffprobe, SftpRemoteEntry v, CancellationToken ct)
    {
        try
        {
            var path = v.FullPath.Replace('/', Path.DirectorySeparatorChar);
            Container container;
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                container = await DetectContainerAsync(fs, ct);
            var streams = await RunFfprobeAsync(ffprobe, path, ct);
            return BuildResult(v, streams, container);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var detail = $"Non è stato possibile leggere il file: {ex.Message}";
            return new WebStreamResult(v.FullPath, WebStreamLevel.Unreadable, "Non leggibile", detail,
                v.Size, v.Modified, DateTime.Now, WebStreamLevel.Unreadable, "Non leggibile", detail);
        }
    }

    /// <summary>Nucleo della verifica: elenco dei video, lettura (3 alla volta), salvataggio, riepilogo.</summary>
    private async Task RunCoreAsync(string serverKey, string serverName, string rootFolder,
        bool web, bool lang, bool duration, bool recheckAll, IReadOnlyList<SftpRemoteEntry>? targets,
        Func<string, CancellationToken, Task<List<SftpRemoteEntry>>> listDir,
        Func<string, SftpRemoteEntry, CancellationToken, Task<WebStreamResult>> probe,
        IDisposable? resource)
    {
        if (IsChecking || (!web && !lang && !duration)) { resource?.Dispose(); return; }
        var label = CheckLabel(web, lang, duration);
        var what = CheckName(web, lang, duration);

        var ffmpegPath = _ffmpeg.GetFfmpegPath();
        var ffprobe = ffmpegPath == null ? null : Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
        if (ffprobe == null || !File.Exists(ffprobe))
        {
            resource?.Dispose();
            Completed?.Invoke(this, $"{what} non disponibile: ffprobe non trovato.");
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var root = string.IsNullOrWhiteSpace(rootFolder) ? "/" : rootFolder;
        CheckingServerKey = serverKey;

        IsChecking = true;
        CheckingKind = web || duration ? WebStreamCheckKind.Web : WebStreamCheckKind.Language;
        ProgressStatus = $"{label}: ricerca video...";
        ProgressDetail = $"{what}: ricerca dei video in {root}";
        RaiseStateChanged();
        _logger.LogInformation("[WEB-STREAM] {What} avviato su {Root} ({Server})", what, root, serverName);

        int checkedNow = 0, done = 0, skipped = 0;
        int webBad = 0, webWarn = 0, langBad = 0, langWarn = 0, unreadable = 0, shortCount = 0;
        var shortBelow = ShortThresholdSeconds;
        var countLock = new object();
        void CountResult(WebStreamResult r)
        {
            lock (countLock)
            {
                if (r.Level == WebStreamLevel.Unreadable || r.LangLevel == WebStreamLevel.Unreadable) { unreadable++; return; }
                if (web) { if (r.Level == WebStreamLevel.Bad) webBad++; else if (r.Level == WebStreamLevel.Warning) webWarn++; }
                if (lang) { if (r.LangLevel == WebStreamLevel.Bad) langBad++; else if (r.LangLevel == WebStreamLevel.Warning) langWarn++; }
                if (duration && shortBelow > 0 && r.Duration is { } d && d < shortBelow) shortCount++;
            }
        }

        bool complete = false;
        string message;

        try
        {
            // 1. Elenco dei video (dal browser SFTP)
            var videos = new List<SftpRemoteEntry>();
            bool truncated = false;
            if (targets == null)
                truncated = !await CollectVideosAsync(listDir, root, MaxDepth, videos, label, ct);
            else
            {
                foreach (var t in targets.Where(t => !t.IsDirectory && t.Size > 0 && VideoExtensions.Contains(Path.GetExtension(t.Name))))
                    videos.Add(t);
                foreach (var t in targets.Where(t => t.IsDirectory))
                    if (!await CollectVideosAsync(listDir, t.FullPath, MaxDepth, videos, label, ct)) { truncated = true; break; }
            }

            // 2. Esiti già salvati
            var saved = (await _repo.GetUnderAsync(serverKey, root))
                .ToDictionary(r => r.FullPath, StringComparer.Ordinal);

            var todo = videos.Where(v => NeedsRead(saved.GetValueOrDefault(v.FullPath), v, web, lang, duration, recheckAll)).ToList();
            skipped = videos.Count - todo.Count;

            // Esiti dei video già controllati, per il riepilogo finale
            foreach (var v in videos.Except(todo))
                if (saved.TryGetValue(v.FullPath, out var s)) CountResult(s);

            // 3. Lettura dei video nuovi o cambiati, tre alla volta (nell'ordine della lista)
            using var gate = new SemaphoreSlim(Parallel);
            var reading = new List<string>();
            void Progress()
            {
                string now;
                lock (reading) now = string.Join(", ", reading);
                int d = Volatile.Read(ref done);
                var first = now.Split(", ")[0];
                ProgressStatus = now.Length > 0 ? $"{label} {d}/{todo.Count} · {first}" : $"{label} {d}/{todo.Count}";
                ProgressDetail = $"{what}: {d} di {todo.Count} video" +
                                 (skipped > 0 ? $" ({skipped} già controllati, saltati)" : "") +
                                 (now.Length > 0 ? $"\nIn lettura: {now}" : "");
                RaiseStateChanged();
            }
            Progress();

            var tasks = todo.Select(async v =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    lock (reading) reading.Add(v.Name);
                    FileReading?.Invoke(serverKey, v.FullPath, true);
                    Progress();

                    WebStreamResult fresh;
                    try { fresh = await probe(ffprobe, v, ct); }
                    catch (ConnectFailedException)
                    {
                        // Senza connessione non ha senso continuare: si fermano anche gli altri
                        try { _cts?.Cancel(); } catch { }
                        throw;
                    }
                    // Fermata durante la lettura: l'esito potrebbe essere falsato, non si salva
                    ct.ThrowIfCancellationRequested();

                    var result = Merge(fresh, saved.GetValueOrDefault(v.FullPath), web, lang, duration);
                    await _repo.SaveAsync(serverKey, result);

                    Interlocked.Increment(ref checkedNow);
                    CountResult(result);
                    FileChecked?.Invoke(serverKey, result);
                }
                finally
                {
                    lock (reading) reading.Remove(v.Name);
                    FileReading?.Invoke(serverKey, v.FullPath, false);
                    Interlocked.Increment(ref done);
                    gate.Release();
                    if (!ct.IsCancellationRequested) Progress();
                }
            }).ToList();
            await Task.WhenAll(tasks);

            // 4. Esiti di video che non ci sono più (eliminati o spostati da fuori)
            if (!truncated)
            {
                var present = videos.Select(v => v.FullPath).ToHashSet(StringComparer.Ordinal);
                // Con una selezione si guarda solo dentro le cartelle scelte (gli altri file non si sono letti)
                bool Covered(string p) => targets == null || targets.Any(t => t.IsDirectory
                    ? p.StartsWith(t.FullPath.TrimEnd('/') + "/", StringComparison.Ordinal)
                    : p == t.FullPath);
                foreach (var gone in saved.Keys.Where(p => !present.Contains(p) && Covered(p)))
                    await _repo.DeleteAsync(serverKey, gone);
            }

            complete = true;
            var total = videos.Count;
            if (total == 0)
                message = targets == null
                    ? "Nessun video trovato in questa cartella e nelle sottocartelle."
                    : "Nessun video negli elementi selezionati.";
            else
            {
                var parts = new List<string>();
                if (webBad > 0) parts.Add($"{webBad} non adatti al web");
                if (langBad > 0) parts.Add($"{langBad} senza italiano");
                if (webWarn + langWarn > 0) parts.Add($"{webWarn + langWarn} da verificare");
                if (shortCount > 0) parts.Add($"{shortCount} più corti di {FormatMinutes(shortBelow)}");
                if (unreadable > 0) parts.Add($"{unreadable} non leggibili");
                var outcome = parts.Count == 0 ? "nessun problema" : string.Join(", ", parts);
                var news = skipped > 0 ? $" ({checkedNow} letti ora, {skipped} già controllati)" : "";
                message = $"{what}: {total} video{news}: {outcome}." +
                          (truncated ? $" Attenzione: superato il limite di {MaxVideos} video o {MaxDepth} livelli di cartelle, alcuni video sono stati esclusi." : "");
            }
        }
        catch (ConnectFailedException ex)
        {
            message = $"{what} non riuscito: connessione al server non riuscita ({ex.InnerException?.Message ?? ex.Message}). " +
                      $"Controlla i dati del server; {checkedNow} video letti prima dell'errore.";
        }
        catch (OperationCanceledException)
        {
            message = $"{what} interrotto: {checkedNow} video letti (gli esiti sono salvati, rilanciando si riparte da qui).";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WEB-STREAM] {What} non riuscito su {Root}", what, root);
            message = $"{what} non riuscito: {ex.Message}";
        }
        finally
        {
            resource?.Dispose();
            IsChecking = false;
            ProgressStatus = string.Empty;
            ProgressDetail = string.Empty;
            _cts?.Dispose();
            _cts = null;
            RaiseStateChanged();
        }

        _logger.LogInformation("[WEB-STREAM] {Message}{State}", message, complete ? "" : " (incompleto)");
        Completed?.Invoke(this, message);
    }

    /// <summary>False se si è fermata per un limite (profondità o numero di video).</summary>
    private async Task<bool> CollectVideosAsync(Func<string, CancellationToken, Task<List<SftpRemoteEntry>>> listDir, string folder, int depth,
        List<SftpRemoteEntry> videos, string label, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var folderName = folder.TrimEnd('/');
        folderName = folderName[(folderName.LastIndexOf('/') + 1)..];
        ProgressStatus = $"{label}: ricerca, {videos.Count} video · {(folderName.Length == 0 ? "/" : folderName)}";
        ProgressDetail = $"Ricerca dei video: {videos.Count} trovati\nCartella: {folder}";
        RaiseStateChanged();

        var entries = await listDir(folder, ct);
        bool complete = true;
        foreach (var e in entries.Where(e => !e.IsDirectory && e.Size > 0 && VideoExtensions.Contains(Path.GetExtension(e.Name))))
        {
            if (videos.Count >= MaxVideos) return false;
            videos.Add(e);
        }
        foreach (var dir in entries.Where(e => e.IsDirectory && !e.Name.StartsWith('.')))
        {
            if (depth <= 1) { complete = false; break; }
            if (!await CollectVideosAsync(listDir, dir.FullPath, depth - 1, videos, label, ct)) return false;
        }
        return complete;
    }

    // ============================================================
    //  CONNESSIONE (una sola, usata dai tre video in lettura)
    // ============================================================

    /// <summary>Il server non accetta la connessione SSH (dati sbagliati, server spento...).</summary>
    private sealed class ConnectFailedException(Exception inner) : Exception(inner.Message, inner);

    private sealed class SharedConnection : IDisposable
    {
        private readonly SftpConfig _cfg;
        private readonly SftpSessionLimiter _limiter;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private SftpClient? _client;
        private bool _holdsSlot;

        public SharedConnection(SftpConfig cfg, SftpSessionLimiter limiter)
        {
            _cfg = cfg;
            _limiter = limiter;
        }

        /// <summary>Connessione aperta; se è caduta (o è <paramref name="broken"/>) se ne apre un'altra.</summary>
        public async Task<SftpClient> GetAsync(CancellationToken ct, SftpClient? broken = null)
        {
            await _lock.WaitAsync(ct);
            try
            {
                if (_client != null && _client.IsConnected && !ReferenceEquals(_client, broken)) return _client;
                try { _client?.Dispose(); } catch { }
                _client = null;
                if (!_holdsSlot) { await _limiter.AcquireAsync(ct); _holdsSlot = true; }
                _client = await SshNetConnector.ConnectAsync(_cfg, ct);
                return _client;
            }
            finally { _lock.Release(); }
        }

        public void Dispose()
        {
            try { _client?.Dispose(); } catch { }
            _client = null;
            if (_holdsSlot) { _limiter.Release(); _holdsSlot = false; }
        }
    }

    // ============================================================
    //  LETTURA DEL FILE REMOTO
    // ============================================================

    /// <summary>Formato reale del file, letto dai primi byte (l'estensione può essere sbagliata).</summary>
    private enum Container { Mp4, Matroska, Other }

    /// <summary>
    /// Legge il video e calcola entrambi gli esiti. Se la connessione è caduta si riconnette e
    /// riprova una volta; se ffprobe non trova video o audio nei primi MB, riprova leggendone di più.
    /// Un file che non si riesce a leggere diventa "Non leggibile" (non ferma la verifica).
    /// </summary>
    private async Task<WebStreamResult> ProbeWithRetryAsync(SharedConnection connection, string ffprobe,
        SftpRemoteEntry v, CancellationToken ct)
    {
        SftpClient? client = null;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                try { client = await connection.GetAsync(ct, broken: attempt > 0 ? client : null); }
                catch (Exception ex) when (ex is not OperationCanceledException) { throw new ConnectFailedException(ex); }

                var (streams, container) = await ProbeRemoteAsync(client, ffprobe, v, HeadBytes, ct);

                bool partial = !streams.Any(s => s.Type == "video" && !s.IsAttachedPic) || !streams.Any(s => s.Type == "audio");
                if (partial && container != Container.Mp4 && v.Size > HeadBytes)
                {
                    // MKV con allegati (font, copertine) in testa: le tracce possono stare più avanti
                    try { (streams, container) = await ProbeRemoteAsync(client, ffprobe, v, RetryHeadBytes, ct); }
                    catch (InvalidDataException) when (streams.Count > 0) { /* si tiene quello che si è trovato */ }
                }
                if (streams.Count == 0) throw new InvalidDataException("nessuna traccia trovata");
                return BuildResult(v, streams, container);
            }
            // Fermata dall'utente: qualunque errore in lettura è dovuto alla chiusura, non al file
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (ConnectFailedException) { throw; }
            catch (Exception ex) when (attempt == 0 && client != null && !client.IsConnected)
            {
                _logger.LogInformation("[WEB-STREAM] Connessione caduta su {File}, riprovo: {Msg}", v.FullPath, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogInformation("[WEB-STREAM] {File} non leggibile: {Msg}", v.FullPath, ex.Message);
                var detail = $"Non è stato possibile leggere il file: {ex.Message}";
                return new WebStreamResult(v.FullPath, WebStreamLevel.Unreadable, "Non leggibile", detail,
                    v.Size, v.Modified, DateTime.Now, WebStreamLevel.Unreadable, "Non leggibile", detail);
            }
        }
    }

    /// <summary>Tracce di un file remoto (legge solo l'inizio o l'indice, come la verifica).</summary>
    public static async Task<List<ProbedStream>> ProbeRemoteStreamsAsync(SftpClient client, string ffprobe,
        string remotePath, long size, CancellationToken ct)
    {
        var entry = new SftpRemoteEntry { Name = Path.GetFileName(remotePath), FullPath = remotePath, Size = size };
        var (streams, container) = await ProbeRemoteAsync(client, ffprobe, entry, HeadBytes, ct);
        bool partial = !streams.Any(s => s.Type == "video" && !s.IsAttachedPic) || !streams.Any(s => s.Type == "audio");
        if (partial && container != Container.Mp4 && size > HeadBytes)
            (streams, _) = await ProbeRemoteAsync(client, ffprobe, entry, RetryHeadBytes, ct);
        return streams;
    }

    /// <summary>Tracce di un file sul PC (ffprobe legge solo quello che serve).</summary>
    public static Task<List<ProbedStream>> ProbeLocalStreamsAsync(string ffprobe, string localPath, CancellationToken ct)
        => RunFfprobeLocalAsync(ffprobe, localPath, ct);

    private static async Task<List<ProbedStream>> RunFfprobeLocalAsync(string ffprobe, string localPath, CancellationToken ct)
        => await RunFfprobeAsync(ffprobe, localPath, ct);

    private static WebStreamResult BuildResult(SftpRemoteEntry v, List<ProbedStream> streams, Container container)
    {
        var stamp = $"\nControllato il {DateTime.Now:dd/MM/yyyy HH:mm}";
        var ext = Path.GetExtension(v.Name);
        // Un MKV con estensione .mp4: Firefox e Safari non lo aprono
        var containerNote = container == Container.Matroska && Mp4Family.Contains(ext)
            ? $"MKV con estensione {ext.ToLowerInvariant()}"
            : null;
        var (level, shortText, detail) = WebStreamRules.Evaluate(streams, containerNote);
        var (langLevel, langText, langDetail) = LanguageRules.Evaluate(streams);
        var (dur, approx) = DurationOf(v, streams as ProbeOutput, container);
        return new WebStreamResult(v.FullPath, level, shortText, detail + stamp, v.Size, v.Modified, DateTime.Now,
            langLevel, langText, langDetail + stamp, Duration: dur, DurationApprox: approx);
    }

    /// <summary>Formati letti solo all'inizio, senza la durata scritta nel file (ffprobe la stima dal pezzo letto).</summary>
    private static readonly HashSet<string> StreamOnlyFormats = new(StringComparer.OrdinalIgnoreCase) { ".ts", ".m2ts", ".mts", ".mpg", ".mpeg" };

    /// <summary>
    /// Durata del video. MP4, MKV, AVI, WebM la scrivono nel file e ffprobe la legge anche dal pezzo
    /// scaricato. Per TS/MPEG non c'è: si stima dalla dimensione del file e dal bitrate (approssimata).
    /// </summary>
    private static (double? Seconds, bool Approx) DurationOf(SftpRemoteEntry v, ProbeOutput? probe, Container container)
    {
        if (probe == null) return (null, false);
        if (container == Container.Other && StreamOnlyFormats.Contains(Path.GetExtension(v.Name)))
            return probe.BitRate is > 0 && v.Size > 0 ? (v.Size * 8.0 / probe.BitRate.Value, true) : (probe.Duration, true);
        return (probe.Duration, false);
    }

    private static async Task<(ProbeOutput Streams, Container Container)> ProbeRemoteAsync(SftpClient client, string ffprobe,
        SftpRemoteEntry v, int headBytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerFileTimeout);
        var token = timeout.Token;

        var container = Container.Other;
        var temp = Path.Combine(Path.GetTempPath(), $"ddl-webcheck-{Guid.NewGuid():N}{Path.GetExtension(v.Name)}");
        try
        {
            await Task.Run(async () =>
            {
                using var remote = client.OpenRead(v.FullPath);
                // Annullando si chiude lo stream: sblocca una lettura ferma
                using var reg = token.Register(() => { try { remote.Dispose(); } catch { } });
                await using var local = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None);

                container = await DetectContainerAsync(remote, token);
                remote.Position = 0;
                if (container == Container.Mp4)
                    await CopyMp4IndexAsync(remote, local, token);
                else
                    await CopyBytesAsync(remote, local, headBytes, token);
            }, token);

            token.ThrowIfCancellationRequested();
            return (await RunFfprobeAsync(ffprobe, temp, token), container);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"lettura oltre {PerFileTimeout.TotalSeconds:F0} s");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static readonly HashSet<string> Mp4TopBoxes = new(StringComparer.Ordinal)
        { "ftyp", "moov", "mdat", "free", "skip", "wide", "pnot", "uuid", "styp" };

    private static async Task<Container> DetectContainerAsync(Stream remote, CancellationToken ct)
    {
        var head = new byte[8];
        int read = 0;
        while (read < head.Length)
        {
            int n = await remote.ReadAsync(head.AsMemory(read), ct);
            if (n <= 0) break;
            read += n;
        }
        if (read < 8) return Container.Other;
        if (head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3) return Container.Matroska;
        return Mp4TopBoxes.Contains(System.Text.Encoding.ASCII.GetString(head, 4, 4)) ? Container.Mp4 : Container.Other;
    }

    private static async Task CopyBytesAsync(Stream from, Stream to, long count, CancellationToken ct)
    {
        var buffer = new byte[256 * 1024];
        long left = count;
        while (left > 0)
        {
            int n = await from.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
            if (n <= 0) break;
            await to.WriteAsync(buffer.AsMemory(0, n), ct);
            left -= n;
        }
    }

    /// <summary>
    /// MP4/MOV: i codec stanno nel blocco "moov", che può essere all'inizio o in fondo al file.
    /// Si scorrono i blocchi principali ("atom") saltando i dati, poi si crea un MP4 ridotto:
    /// - moov all'inizio: basta l'inizio del file (moov + 1 MB di dati);
    /// - moov in fondo: inizio del file con il blocco dati "mdat" accorciato a 1 MB, seguito
    ///   dal moov. Le posizioni dei primi fotogrammi restano giuste, così ffprobe ne legge
    ///   davvero qualcuno (serve per riconoscere ad esempio l'H.264 a 10 bit).
    /// </summary>
    private static async Task CopyMp4IndexAsync(Stream remote, Stream local, CancellationToken ct)
    {
        long length = remote.Length;
        long pos = 0;
        var header = new byte[16];
        long moovPos = -1, moovSize = 0;
        long mdatPos = -1;
        int mdatHeaderLen = 8;

        for (int guard = 0; guard < 10000 && pos + 8 <= length; guard++)
        {
            ct.ThrowIfCancellationRequested();
            remote.Position = pos;
            await ReadExactlyAsync(remote, header, 8, ct);
            long size = ((long)header[0] << 24) | ((long)header[1] << 16) | ((long)header[2] << 8) | header[3];
            var type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
            int headerLen = 8;
            if (size == 1)
            {
                await ReadExactlyAsync(remote, header, 8, ct, offset: 8);
                size = 0;
                for (int i = 8; i < 16; i++) size = (size << 8) | header[i];
                headerLen = 16;
            }
            else if (size == 0) size = length - pos;   // ultimo blocco, fino alla fine
            if (size < headerLen) break;               // file rovinato

            if (type == "mdat" && mdatPos < 0) { mdatPos = pos; mdatHeaderLen = headerLen; }
            if (type == "moov") { moovPos = pos; moovSize = size; break; }
            pos += size;
        }

        if (moovPos < 0) throw new InvalidDataException("indice MP4 (moov) non trovato: file incompleto?");
        if (moovSize > MaxMoovBytes) throw new InvalidDataException("indice MP4 troppo grande");

        if (mdatPos < 0 || moovPos < mdatPos)
        {
            // moov in testa (faststart): inizio del file fino a 1 MB dopo il moov
            remote.Position = 0;
            await CopyBytesAsync(remote, local, Math.Min(length, moovPos + moovSize + Mp4DataBytes), ct);
            return;
        }

        // moov in fondo: [tutto fino a mdat][mdat accorciato][moov]
        long keepData = Math.Min(Mp4DataBytes, moovPos - mdatPos - mdatHeaderLen);
        remote.Position = 0;
        await CopyBytesAsync(remote, local, mdatPos, ct);

        var mdatHeader = new byte[mdatHeaderLen];
        long newSize = mdatHeaderLen + keepData;
        if (mdatHeaderLen == 16)
        {
            mdatHeader[3] = 1;
            for (int i = 0; i < 8; i++) mdatHeader[15 - i] = (byte)(newSize >> (8 * i));
        }
        else
        {
            for (int i = 0; i < 4; i++) mdatHeader[3 - i] = (byte)(newSize >> (8 * i));
        }
        System.Text.Encoding.ASCII.GetBytes("mdat").CopyTo(mdatHeader, 4);
        await local.WriteAsync(mdatHeader, ct);

        remote.Position = mdatPos + mdatHeaderLen;
        await CopyBytesAsync(remote, local, keepData, ct);

        remote.Position = moovPos;
        await CopyBytesAsync(remote, local, moovSize, ct);
    }

    private static async Task ReadExactlyAsync(Stream s, byte[] buffer, int count, CancellationToken ct, int offset = 0)
    {
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buffer.AsMemory(offset + read, count - read), ct);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static async Task<ProbeOutput> RunFfprobeAsync(string ffprobe, string file, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in new[]
                 {
                     "-v", "error", "-show_entries",
                     "stream=codec_type,codec_name,profile,pix_fmt,channels:stream_tags=language,title:stream_disposition=default,attached_pic,forced:format=duration,bit_rate",
                     "-of", "json", file
                 })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        ChildProcessJob.Add(proc);
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        try { await proc.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        // Il file è solo un pezzo dell'originale: ffprobe può lamentarsi dei dati mancanti
        // ma i codec li legge comunque. Conta quello che ha trovato.
        var streams = ParseStreams(await stdout);
        if (streams.Count == 0)
        {
            var err = (await stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            throw new InvalidDataException(string.IsNullOrEmpty(err) ? "nessuna traccia trovata" : err);
        }
        return streams;
    }

    private static ProbeOutput ParseStreams(string json)
    {
        var list = new ProbeOutput();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("format", out var fmt))
            {
                // ffprobe scrive i numeri come testo ("1424.512000")
                static double? Num(JsonElement e, string name) =>
                    e.TryGetProperty(name, out var v) && double.TryParse(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
                list.Duration = Num(fmt, "duration") is > 0 and var dur ? dur : null;
                list.BitRate = Num(fmt, "bit_rate") is > 0 and var br ? br : null;
            }
            if (!doc.RootElement.TryGetProperty("streams", out var arr)) return list;
            foreach (var s in arr.EnumerateArray())
            {
                string Str(string name) => s.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
                bool Flag(string name) => s.TryGetProperty("disposition", out var d) && d.TryGetProperty(name, out var f)
                                          && f.ValueKind == JsonValueKind.Number && f.GetInt32() == 1;
                string lang = "", title = "";
                if (s.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
                    foreach (var tag in tags.EnumerateObject())
                    {
                        if (tag.Name.Equals("language", StringComparison.OrdinalIgnoreCase)) lang = (tag.Value.GetString() ?? "").ToLowerInvariant();
                        else if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase)) title = tag.Value.GetString() ?? "";
                    }
                int channels = s.TryGetProperty("channels", out var ch) && ch.ValueKind == JsonValueKind.Number ? ch.GetInt32() : 0;
                list.Add(new ProbedStream(Str("codec_type"), Str("codec_name"), Str("profile"), Str("pix_fmt"),
                    lang, Flag("default"), Flag("attached_pic"), title, Flag("forced"), channels));
            }
        }
        catch (JsonException) { }
        return list;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Tracce lette da ffprobe, con durata e bitrate del file.</summary>
public sealed class ProbeOutput : List<ProbedStream>
{
    public double? Duration { get; set; }
    public double? BitRate { get; set; }
}

public sealed record ProbedStream(string Type, string Codec, string Profile, string PixFmt, string Language,
    bool IsDefault, bool IsAttachedPic, string Title = "", bool IsForced = false, int Channels = 0);

/// <summary>Regole: cosa riproducono i browser (Chrome, Firefox, Edge, Safari).</summary>
public static class WebStreamRules
{
    private static readonly HashSet<string> WebVideo = new(StringComparer.OrdinalIgnoreCase) { "h264", "vp8", "vp9", "av1" };

    /// <param name="containerNote">Problema del contenitore (es. MKV con estensione .mp4), se c'è.</param>
    public static (WebStreamLevel Level, string Short, string Detail) Evaluate(IReadOnlyList<ProbedStream> streams, string? containerNote = null)
    {
        var level = WebStreamLevel.Ok;
        var problems = new List<string>();
        void Flag(WebStreamLevel l, string text)
        {
            if (l > level) level = l;
            problems.Add(text);
        }

        // ---- Video ----
        var video = streams.FirstOrDefault(s => s.Type == "video" && !s.IsAttachedPic);
        if (video != null)
        {
            var codec = video.Codec.ToLowerInvariant();
            if (codec == "h264" && IsHighBitDepthH264(video))
                Flag(WebStreamLevel.Bad, video.Profile.Contains("4:4:4") ? "Video H.264 4:4:4"
                    : video.Profile.Contains("4:2:2") ? "Video H.264 4:2:2"
                    : "Video H.264 10-bit");
            else if (codec == "hevc")
                Flag(WebStreamLevel.Warning, "Video HEVC");
            else if (!WebVideo.Contains(codec))
                Flag(WebStreamLevel.Bad, $"Video {Pretty(codec)}");
        }

        // ---- Audio: il browser suona la traccia predefinita (o la prima) ----
        var audio = streams.Where(s => s.Type == "audio").ToList();
        if (audio.Count > 0)
        {
            var main = LanguageRules.MainTrack(audio);
            if (!Mp4Remuxer.WebFriendlyAudio.Contains(main.Codec))
                Flag(WebStreamLevel.Bad, $"Audio {Pretty(main.Codec)} (muto)");
            else
            {
                // La traccia principale va: le altre non web si segnalano solo come avviso
                var others = audio.Select((a, i) => (a, i))
                    .Where(x => !ReferenceEquals(x.a, main) && !Mp4Remuxer.WebFriendlyAudio.Contains(x.a.Codec))
                    .ToList();
                if (others.Count == 1)
                    Flag(WebStreamLevel.Warning, $"{others[0].i + 1}ª traccia {Pretty(others[0].a.Codec)}");
                else if (others.Count > 1)
                    Flag(WebStreamLevel.Warning, $"{others.Count} tracce audio non web");
            }
        }

        // ---- Contenitore ----
        if (containerNote != null) Flag(WebStreamLevel.Warning, containerNote);

        var shortText = problems.Count == 0 ? "OK" : string.Join(" · ", problems);

        // ---- Dettaglio per il tooltip ----
        var lines = new List<string>();
        lines.Add(video == null
            ? "Video: nessuno"
            : $"Video: {Pretty(video.Codec)}{(string.IsNullOrEmpty(video.Profile) ? "" : $" ({video.Profile})")}");
        if (audio.Count == 0) lines.Add("Audio: nessuna traccia");
        else
        {
            lines.Add("Audio:");
            for (int i = 0; i < audio.Count; i++)
            {
                var a = audio[i];
                var ok = Mp4Remuxer.WebFriendlyAudio.Contains(a.Codec) ? "ok per il web" : "non riprodotto dai browser";
                var lang = LanguageRules.IsUndefined(a.Language) ? "" : $" {LanguageRules.Name(a.Language).ToLowerInvariant()}";
                lines.Add($"  {i + 1}) {Pretty(a.Codec)}{lang}{(a.IsDefault ? " (predefinita)" : "")}: {ok}");
            }
        }
        if (video?.Codec.Equals("hevc", StringComparison.OrdinalIgnoreCase) == true)
            lines.Add("HEVC: va su Safari e su Chrome/Edge con accelerazione hardware, non su Firefox.");
        if (containerNote != null)
            lines.Add("Il file è in realtà un MKV: Chrome di solito lo apre, Firefox e Safari no. Va convertito in MP4 vero.");

        return (level, shortText, string.Join("\n", lines));
    }

    /// <summary>H.264 a 10 bit (o 4:2:2 / 4:4:4): i browser non lo decodificano (frequente negli anime).</summary>
    private static bool IsHighBitDepthH264(ProbedStream v) =>
        v.Profile.Contains("10", StringComparison.Ordinal) ||
        v.Profile.Contains("4:2:2", StringComparison.Ordinal) ||
        v.Profile.Contains("4:4:4", StringComparison.Ordinal) ||
        v.PixFmt.Contains("10", StringComparison.Ordinal);

    public static string Pretty(string codec) => codec.ToLowerInvariant() switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "vp8" => "VP8",
        "vp9" => "VP9",
        "mpeg4" => "MPEG-4 (Xvid)",
        "msmpeg4v3" or "msmpeg4v2" => "DivX 3",
        "mpeg2video" => "MPEG-2",
        "mpeg1video" => "MPEG-1",
        "vc1" => "VC-1",
        "wmv3" or "wmv2" or "wmv1" => "WMV",
        "aac" => "AAC",
        "mp3" => "MP3",
        "opus" => "Opus",
        "ac3" => "AC3",
        "eac3" => "E-AC3",
        "dts" => "DTS",
        "truehd" => "TrueHD",
        "flac" => "FLAC",
        "vorbis" => "Vorbis",
        "mp2" => "MP2",
        "wmav2" or "wmav1" or "wmapro" => "WMA",
        var c when c.StartsWith("pcm_") => "PCM",
        "" => "sconosciuto",
        var c => c.ToUpperInvariant()
    };
}

/// <summary>
/// Regole sulla lingua: serve almeno una traccia audio in italiano, meglio se è la prima (il player
/// web suona quella). Contano solo i dati delle tracce (codice lingua e titolo della traccia),
/// non il nome del file, che può ingannare.
/// </summary>
public static class LanguageRules
{
    /// <summary>Traccia che suona il player: quella predefinita, altrimenti la prima.</summary>
    public static ProbedStream MainTrack(IReadOnlyList<ProbedStream> audio) => audio.FirstOrDefault(a => a.IsDefault) ?? audio[0];

    public static bool IsUndefined(string language) => string.IsNullOrWhiteSpace(language) || language is "und" or "unk" or "mis" or "zxx";

    public static (WebStreamLevel Level, string Short, string Detail) Evaluate(IReadOnlyList<ProbedStream> streams)
    {
        var audio = streams.Where(s => s.Type == "audio").ToList();
        if (audio.Count == 0)
            return (WebStreamLevel.Warning, "Nessuna traccia audio", "Il file non ha tracce audio.");

        var main = MainTrack(audio);
        var italian = audio.Where(a => AudioTracks.IsItalian(a.Language, a.Title)).ToList();

        WebStreamLevel level;
        string shortText;
        if (italian.Count > 0)
        {
            if (AudioTracks.IsItalian(main.Language, main.Title))
            {
                level = WebStreamLevel.Ok;
                int others = audio.Count - 1;
                shortText = others == 0 ? "ITA" : others == 1 ? "ITA + 1 altra" : $"ITA + {others} altre";
            }
            else
            {
                level = WebStreamLevel.Warning;
                shortText = "ITA non è la 1ª traccia";
            }
        }
        else if (audio.Any(a => IsUndefined(a.Language)))
        {
            level = WebStreamLevel.Warning;
            shortText = "Lingua non indicata";
        }
        else if (audio.Count == 1 && audio[0].Language is "eng" or "en")
        {
            // Molti programmi di conversione segnano "inglese" di default: con un'unica traccia
            // non ci si può fidare (succede spesso con file in italiano)
            level = WebStreamLevel.Warning;
            shortText = "Lingua da verificare";
        }
        else
        {
            level = WebStreamLevel.Bad;
            shortText = "Senza italiano";
        }

        var lines = new List<string> { "Tracce audio:" };
        for (int i = 0; i < audio.Count; i++)
        {
            var a = audio[i];
            var name = IsUndefined(a.Language) ? "lingua non indicata" : Name(a.Language);
            if (AudioTracks.IsItalian(a.Language, a.Title) && IsUndefined(a.Language)) name = "Italiano (dal titolo della traccia)";
            var title = string.IsNullOrWhiteSpace(a.Title) ? "" : $" \"{a.Title.Trim()}\"";
            lines.Add($"  {i + 1}) {name}{title}{(ReferenceEquals(a, main) ? " (suona nel player web)" : "")}");
        }
        if (level == WebStreamLevel.Warning && shortText == "ITA non è la 1ª traccia")
            lines.Add("Nel player web si sentirebbe un'altra lingua: l'italiano va messo come prima traccia.");
        else if (shortText == "Lingua non indicata")
            lines.Add("Il file non dice di che lingua è l'audio: va ascoltato per saperlo.");
        else if (shortText == "Lingua da verificare")
            lines.Add("L'unica traccia è segnata come Inglese, ma molti programmi di conversione la segnano così\n" +
                      "anche quando l'audio è in un'altra lingua: va ascoltata per esserne sicuri.");

        return (level, shortText, string.Join("\n", lines));
    }

    /// <summary>Nome per esteso della lingua (ISO 639-2 / 639-1); i codici sconosciuti restano come sono.</summary>
    public static string Name(string code) => code.ToLowerInvariant() switch
    {
        "ita" or "it" => "Italiano",
        "eng" or "en" => "Inglese",
        "ger" or "deu" or "de" => "Tedesco",
        "fre" or "fra" or "fr" => "Francese",
        "spa" or "es" => "Spagnolo",
        "por" or "pt" => "Portoghese",
        "jpn" or "ja" => "Giapponese",
        "chi" or "zho" or "zh" => "Cinese",
        "kor" or "ko" => "Coreano",
        "rus" or "ru" => "Russo",
        "ukr" or "uk" => "Ucraino",
        "pol" or "pl" => "Polacco",
        "dut" or "nld" or "nl" => "Olandese",
        "swe" or "sv" => "Svedese",
        "nor" or "nob" or "no" => "Norvegese",
        "dan" or "da" => "Danese",
        "fin" or "fi" => "Finlandese",
        "cze" or "ces" or "cs" => "Ceco",
        "hun" or "hu" => "Ungherese",
        "gre" or "ell" or "el" => "Greco",
        "tur" or "tr" => "Turco",
        "ara" or "ar" => "Arabo",
        "heb" or "he" => "Ebraico",
        "hin" or "hi" => "Hindi",
        "tha" or "th" => "Thai",
        "rum" or "ron" or "ro" => "Rumeno",
        "hrv" or "hr" => "Croato",
        "srp" or "sr" => "Serbo",
        "slv" or "sl" => "Sloveno",
        "alb" or "sqi" or "sq" => "Albanese",
        "cat" or "ca" => "Catalano",
        "lat" or "la" => "Latino",
        var c => c.ToUpperInvariant()
    };
}
