using System.Diagnostics;
using System.Net;
using System.Net.Http;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Domains;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Proxy;

/// <summary>Esito di un'importazione di proxy.</summary>
public record ProxyImportResult(int Added, int AlreadyPresent, int AddedToLists, int KeptExcluded, int InvalidLines);

/// <summary>
/// Archivio proxy e liste per provider. Ogni provider ha la sua lista (con stato, esito e latenza)
/// e al massimo un proxy attivo.
/// </summary>
public class ProxyService
{
    private readonly ProxyConfigRepository _repository;
    private readonly DomainResolver _domains;
    private readonly ILogger<ProxyService> _logger;

    public ProxyService(ProxyConfigRepository repository, DomainResolver domains, ILogger<ProxyService> logger)
    {
        _repository = repository;
        _domains = domains;
        _logger = logger;
    }

    public List<ProxyConfig> All { get; } = new();

    public event EventHandler? ActiveProxyChanged;

    public async Task LoadAllAsync()
    {
        var list = await _repository.GetAllAsync();
        All.Clear();
        All.AddRange(list);

        // Fino alla 1.2.0 un test riuscito attivava il proxy anche se ce n'era già uno: ne resta uno per lista
        var changed = new List<ProxyConfig>();
        foreach (var key in ProxyConfigRepository.ListKeys)
        {
            var keep = GetActiveFor(key);
            if (keep == null) continue;
            foreach (var p in All.Where(p => p != keep && p.EntryFor(key)?.IsActive == true))
            {
                p.EntryFor(key)!.IsActive = false;
                changed.Add(p);
            }
        }
        if (changed.Count > 0) await SaveManyAsync(changed);
    }

    public async Task SaveAsync(ProxyConfig p) => await SaveManyAsync(new[] { p });

    /// <summary>Salva proxy e liste in un'unica transazione, fuori dal thread UI.</summary>
    public async Task SaveManyAsync(IEnumerable<ProxyConfig> proxies)
    {
        var list = proxies.Distinct().ToList();
        if (list.Count == 0) return;

        foreach (var p in list)
            if (!All.Contains(p)) All.Add(p);

        await Task.Run(() => _repository.SaveManyAsync(list));
    }

    public async Task DeleteAsync(ProxyConfig p) => await DeleteManyAsync(new[] { p });

    /// <summary>Elimina definitivamente dall'archivio (e da tutte le liste).</summary>
    public async Task DeleteManyAsync(IEnumerable<ProxyConfig> proxies)
    {
        var list = proxies.ToList();
        var ids = list.Where(p => p.Id > 0).Select(p => p.Id).ToList();
        if (ids.Count > 0) await Task.Run(() => _repository.DeleteManyAsync(ids));

        var set = list.ToHashSet();
        bool hadActive = list.Any(p => p.Lists.Values.Any(e => e.IsActive));
        All.RemoveAll(set.Contains);
        if (hadActive) ActiveProxyChanged?.Invoke(this, EventArgs.Empty);
    }

    // ============================================================
    //  STATO PER PROVIDER
    // ============================================================

    public static bool IsInList(ProxyConfig p, string providerKey) => p.EntryFor(providerKey)?.IsInList == true;

    public static bool IsEnabledFor(ProxyConfig p, string providerKey)
        => p.EntryFor(providerKey) is { IsActive: true, IsInList: true };

    public static void SetEnabledFor(ProxyConfig p, string providerKey, bool value)
    {
        if (!value)
        {
            if (p.EntryFor(providerKey) is { } e) e.IsActive = false;
            return;
        }
        var entry = p.GetOrAddEntry(providerKey);
        if (entry.Status == ProxyEntryStatus.Excluded) entry.Status = ProxyEntryStatus.Untested;
        entry.IsActive = true;
        entry.ManuallyDisabled = false;
    }

    /// <summary>Il proxy usato dal provider: quello attivo (se per errore sono più d'uno, il più sano).</summary>
    public ProxyConfig? GetActiveFor(string providerKey)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var candidates = All.Where(p => IsEnabledFor(p, providerKey)).ToList();
        if (candidates.Count == 0) return null;

        return candidates
            .Where(p => p.ConsecutiveFailures < 3)
            .Where(p => p.LastFailedUtc == null || p.LastFailedUtc < cutoff)
            .OrderBy(p => p.ConsecutiveFailures)
            .FirstOrDefault() ?? candidates.First();
    }

    /// <summary>Rende <paramref name="next"/> l'unico proxy attivo della lista. Restituisce i proxy cambiati.</summary>
    public List<ProxyConfig> MakeOnlyActive(ProxyConfig next, string providerKey)
    {
        var changed = All.Where(p => p != next && p.EntryFor(providerKey)?.IsActive == true).ToList();
        foreach (var p in changed) p.EntryFor(providerKey)!.IsActive = false;
        SetEnabledFor(next, providerKey, true);
        changed.Add(next);
        return changed;
    }

    // ============================================================
    //  ROTAZIONE
    // ============================================================

    /// <summary>
    /// Passa al proxy successivo della lista: prima i funzionanti dal più veloce, poi quelli da testare.
    /// Mai gli esclusi o i falliti.
    /// </summary>
    public async Task<ProxyConfig?> RotateNextForAsync(string providerKey)
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var current = GetActiveFor(providerKey);

        bool Healthy(ProxyConfig p) => p.ConsecutiveFailures == 0 && (p.LastFailedUtc == null || p.LastFailedUtc < cutoff);

        var inList = All.Where(p => p != current && p.EntryFor(providerKey) is { } e && e.IsInList).ToList();
        var next = inList
                       .Where(p => p.EntryFor(providerKey)!.Status == ProxyEntryStatus.Ok && Healthy(p))
                       .OrderBy(p => p.EntryFor(providerKey)!.LatencyMs ?? int.MaxValue)
                       .FirstOrDefault()
                   ?? inList.FirstOrDefault(p => p.EntryFor(providerKey)!.Status == ProxyEntryStatus.Untested && Healthy(p));

        if (next == null)
        {
            _logger.LogWarning("[PROXY] Nessun proxy di riserva per {P}", providerKey);
            return null;
        }

        var changed = MakeOnlyActive(next, providerKey);
        try { await SaveManyAsync(changed); }
        catch (Exception ex) { _logger.LogWarning(ex, "[PROXY] Salvataggio rotazione fallito"); }

        _logger.LogWarning("[PROXY] Rotazione {P} → {Name}", providerKey, next.Name);
        ActiveProxyChanged?.Invoke(this, EventArgs.Empty);
        return next;
    }

    /// <summary>Errore di rete durante l'uso: segna il proxy e, se era quello attivo, passa al successivo.</summary>
    public async Task MarkFailedAndRotateAsync(ProxyConfig proxy, string providerKey)
    {
        if (proxy == null) return;

        proxy.ConsecutiveFailures++;
        proxy.LastFailedUtc = DateTime.UtcNow;
        try { await SaveAsync(proxy); }
        catch (Exception ex) { _logger.LogWarning(ex, "[PROXY] Salvataggio di {Name} fallito", proxy.Name); }

        _logger.LogWarning("[PROXY] {Name} fallito su {P} ({N})", proxy.Name, providerKey, proxy.ConsecutiveFailures);

        if (IsEnabledFor(proxy, providerKey))
            await RotateNextForAsync(providerKey);
    }

    // ============================================================
    //  TEST
    // ============================================================

    /// <summary>Pagina usata per provare un proxy su un provider.</summary>
    public string TestUrlFor(string providerKey) => providerKey switch
    {
        ProviderKeys.AnimeWorld => _domains.GetBaseUrl("AnimeWorld"),
        ProviderKeys.AnimeSaturn => _domains.GetBaseUrl("AnimeSaturn"),
        ProviderKeys.StreamingCommunity => _domains.GetBaseUrl("StreamingCommunity") + "/it",
        ProviderKeys.Cb01 => _domains.GetBaseUrl(DomainResolver.Maxstream) + "/",
        // Download diretti: un endpoint neutro e leggerissimo
        _ => "https://www.google.com/generate_204",
    };

    public async Task<(bool Ok, string Message, int? LatencyMs)> TestForProviderAsync(
        ProxyConfig p, string providerKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.Host))
            return (false, "Host vuoto", null);

        var sw = Stopwatch.StartNew();
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = BuildWebProxy(p),
                UseProxy = true,
                AutomaticDecompression = DecompressionMethods.All
            };

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
            AddPreemptiveProxyAuth(http, p);

            if (providerKey == ProviderKeys.Cb01)
                return await TestCb01Async(http, sw, ct);

            using var resp = await http.GetAsync(TestUrlFor(providerKey), HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)resp.StatusCode;
            var ms = (int)sw.ElapsedMilliseconds;

            if (status == 407) return (false, "407 — credenziali proxy rifiutate", null);
            if (status >= 400) return (false, $"HTTP {status}", null);
            return (true, $"OK · {ms} ms", ms);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException) { return (false, "Timeout (20 s)", null); }
        catch (Exception ex) { return (false, ex.GetBaseException().Message, null); }
    }

    /// <summary>
    /// CB01 · Maxstream: chi blocca i proxy è Maxstream (Cloudflare rifiuta gli IP di datacenter e VPN).
    /// Si prova quello che fa l'app: home di Maxstream, un login (con credenziali finte: l'account
    /// dell'utente non viene usato) e la pagina di CB01 per la ricerca. Il tempo è quello della home.
    /// </summary>
    private async Task<(bool Ok, string Message, int? LatencyMs)> TestCb01Async(HttpClient http, Stopwatch sw, CancellationToken ct)
    {
        var ms = _domains.GetBaseUrl(DomainResolver.Maxstream).TrimEnd('/') + "/";

        var (homeStatus, homeBody) = await ReadAsync(http.GetAsync(ms, ct));
        var latency = (int)sw.ElapsedMilliseconds;
        if (homeStatus == 407) return (false, "407 — credenziali proxy rifiutate", null);
        if (CloudflareVerdict(homeStatus, homeBody) is { } homeBlock) return (false, $"Maxstream: {homeBlock}", null);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["op"] = "login", ["redirect"] = "", ["login"] = "verifica_proxy", ["password"] = "x"
        });
        var (loginStatus, loginBody) = await ReadAsync(http.PostAsync(ms, form, ct));
        if (CloudflareVerdict(loginStatus, loginBody) is { } loginBlock) return (false, $"Maxstream (login): {loginBlock}", null);

        var cb = _domains.GetBaseUrl(DomainResolver.Cb01).TrimEnd('/') + "/";
        var (cbStatus, cbBody) = await ReadAsync(http.GetAsync(cb, ct));
        if (CloudflareVerdict(cbStatus, cbBody) is { } cbBlock) return (false, $"CB01: {cbBlock}", null);

        return (true, $"OK · {latency} ms · Maxstream e CB01 raggiungibili", latency);

        static async Task<(int Status, string Body)> ReadAsync(Task<HttpResponseMessage> send)
        {
            using var r = await send;
            var body = await r.Content.ReadAsStringAsync();
            return ((int)r.StatusCode, body.Length > 200_000 ? body[..200_000] : body);
        }
    }

    /// <summary>Null se la pagina è passata; altrimenti il motivo (blocco o verifica di Cloudflare, errore HTTP).</summary>
    private static string? CloudflareVerdict(int status, string body)
    {
        if (body.Contains("error code: 1005") || body.Contains("you have been blocked", StringComparison.OrdinalIgnoreCase))
            return "bloccato da Cloudflare (IP da datacenter/VPN)";
        if (body.Contains("Just a moment") || body.Contains("Ci siamo quasi") || body.Contains("cf-chl"))
            return "verifica anti-bot di Cloudflare";
        if (status == 403) return "accesso negato (HTTP 403)";
        if (status >= 400) return $"HTTP {status}";
        return null;
    }

    /// <summary>
    /// Registra l'esito nella lista del provider. Falliti: esclusi se richiesto (e tolti dall'attivo).
    /// Riusciti: se la lista non ha un proxy attivo e non è stato disattivato a mano, diventa quello attivo.
    /// Restituisce true se è cambiato il proxy attivo.
    /// </summary>
    public bool ApplyTestResult(ProxyConfig p, string providerKey, bool ok, string message, int? latencyMs, bool autoExclude)
    {
        var entry = p.GetOrAddEntry(providerKey);
        entry.LastResult = message;
        entry.LastTestedUtc = DateTime.UtcNow;
        entry.LatencyMs = ok ? latencyMs : null;

        if (!ok)
        {
            bool wasActive = entry.IsActive;
            entry.Status = autoExclude ? ProxyEntryStatus.Excluded : ProxyEntryStatus.Failed;
            entry.IsActive = false;
            return wasActive;
        }

        entry.Status = ProxyEntryStatus.Ok;
        p.ConsecutiveFailures = 0;
        p.LastFailedUtc = null;

        if (!entry.ManuallyDisabled && GetActiveFor(providerKey) == null)
        {
            MakeOnlyActive(p, providerKey);
            return true;
        }
        return false;
    }

    public void RaiseActiveChanged() => ActiveProxyChanged?.Invoke(this, EventArgs.Empty);

    // ============================================================
    //  LISTE: IMPORTAZIONE, ESCLUSIONE, PULIZIA
    // ============================================================

    /// <summary>
    /// Aggiunge i proxy all'archivio e alle liste scelte. Un proxy già presente (stesso host, porta e utente)
    /// non viene duplicato: si aggiunge solo alle liste che non ha. Se in una lista era stato escluso,
    /// resta escluso.
    /// </summary>
    public async Task<ProxyImportResult> ImportAsync(IEnumerable<ProxyConfig> parsed, IReadOnlyCollection<string> providers, int invalidLines)
    {
        var byKey = All.GroupBy(p => p.IdentityKey).ToDictionary(g => g.Key, g => g.First());
        int added = 0, existing = 0, addedToLists = 0, keptExcluded = 0;
        var toSave = new List<ProxyConfig>();

        foreach (var p in parsed)
        {
            if (byKey.TryGetValue(p.IdentityKey, out var known))
            {
                existing++;
                bool changed = false;
                foreach (var key in providers)
                {
                    var entry = known.EntryFor(key);
                    if (entry == null) { known.GetOrAddEntry(key); addedToLists++; changed = true; }
                    else if (entry.Status == ProxyEntryStatus.Excluded) keptExcluded++;
                }
                if (changed) toSave.Add(known);
                continue;
            }

            if (string.IsNullOrWhiteSpace(p.Name) || p.Name == "Proxy") p.Name = p.DisplayText;
            foreach (var key in providers) p.GetOrAddEntry(key);
            byKey[p.IdentityKey] = p;
            toSave.Add(p);
            added++;
            addedToLists += providers.Count;
        }

        await SaveManyAsync(toSave);
        return new ProxyImportResult(added, existing, addedToLists, keptExcluded, invalidLines);
    }

    /// <summary>Esclude dalla lista: non si testa più e non torna reimportando.</summary>
    public async Task<int> ExcludeAsync(IEnumerable<ProxyConfig> proxies, string providerKey)
    {
        var changed = new List<ProxyConfig>();
        bool activeChanged = false;
        foreach (var p in proxies)
        {
            if (p.EntryFor(providerKey) is not { IsInList: true } e) continue;
            activeChanged |= e.IsActive;
            e.Status = ProxyEntryStatus.Excluded;
            e.IsActive = false;
            changed.Add(p);
        }
        await SaveManyAsync(changed);
        if (activeChanged) RaiseActiveChanged();
        return changed.Count;
    }

    /// <summary>Riporta in lista come "da testare".</summary>
    public async Task<int> RestoreAsync(IEnumerable<ProxyConfig> proxies, string providerKey)
    {
        var changed = new List<ProxyConfig>();
        foreach (var p in proxies)
        {
            if (p.EntryFor(providerKey) is not { Status: ProxyEntryStatus.Excluded } e) continue;
            e.Status = ProxyEntryStatus.Untested;
            e.LastResult = "";
            e.LatencyMs = null;
            changed.Add(p);
        }
        await SaveManyAsync(changed);
        return changed.Count;
    }

    /// <summary>Toglie da tutte le liste: il proxy resta solo nell'archivio.</summary>
    public async Task<int> RemoveFromAllListsAsync(IEnumerable<ProxyConfig> proxies)
    {
        var changed = proxies.Where(p => p.Lists.Count > 0).ToList();
        bool activeChanged = changed.Any(p => p.Lists.Values.Any(e => e.IsActive));
        foreach (var p in changed) p.Lists.Clear();
        await SaveManyAsync(changed);
        if (activeChanged) RaiseActiveChanged();
        return changed.Count;
    }

    /// <summary>Aggiunge o toglie il proxy da una lista (dal pannello dettagli).</summary>
    public async Task SetMembershipAsync(ProxyConfig p, string providerKey, bool inList)
    {
        if (inList)
        {
            var e = p.GetOrAddEntry(providerKey);
            if (e.Status == ProxyEntryStatus.Excluded) e.Status = ProxyEntryStatus.Untested;
        }
        else if (p.Lists.Remove(providerKey, out var removed) && removed.IsActive)
        {
            RaiseActiveChanged();
        }
        if (p.Id > 0) await SaveAsync(p);
    }

    /// <summary>Proxy che non servono a nessuno: in almeno una lista e ovunque falliti o esclusi.</summary>
    public List<ProxyConfig> FindUseless() => All
        .Where(p => p.Lists.Count > 0 && p.Lists.Values.All(e => e.Status is ProxyEntryStatus.Failed or ProxyEntryStatus.Excluded))
        .ToList();

    // ============================================================
    //  HTTP
    // ============================================================

    /// <summary>
    /// Header Proxy-Authorization (Basic) già nella prima richiesta: alcuni proxy non fanno
    /// il challenge-response e vogliono le credenziali subito.
    /// </summary>
    private static void AddPreemptiveProxyAuth(HttpClient http, ProxyConfig p)
    {
        if (p.Auth != ProxyAuthType.UserPassword) return;
        if (string.IsNullOrEmpty(p.Username)) return;

        var raw = $"{p.Username}:{p.Password}";
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));
        http.DefaultRequestHeaders.TryAddWithoutValidation("Proxy-Authorization", $"Basic {b64}");
    }

    public static IWebProxy BuildWebProxy(ProxyConfig p)
    {
        var scheme = p.Protocol == ProxyProtocol.Socks5 ? "socks5" : "http";
        var proxy = new WebProxy(new Uri($"{scheme}://{p.Host}:{p.Port}"));
        if (p.Auth == ProxyAuthType.UserPassword)
            proxy.Credentials = new NetworkCredential(p.Username, p.Password);
        return proxy;
    }
}
