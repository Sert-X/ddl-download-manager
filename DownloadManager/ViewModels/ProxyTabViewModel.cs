using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Anime.AnimeWorld;
using DownloadManager.Services.Anime.AnimeSaturn;
using DownloadManager.Services.Domains;
using DownloadManager.Services.Proxy;
using DownloadManager.Services.Streaming.StreamingCommunity;

namespace DownloadManager.ViewModels;

public partial class ProxyTabViewModel : ViewModelBase
{
    private readonly ProxyService _proxyService;
    private readonly IAnimeWorldService _animeWorldService;
    private readonly IAnimeSaturnService _animeSaturnService;
    private readonly IStreamingCommunityService _streamingCommunityService;
    private readonly DomainResolver _domainResolver;
    private readonly SettingsService _settings;
    private readonly SharedState _shared;

    private CancellationTokenSource? _proxyTestCts;

    /// <summary>Tutti i proxy (dal DB).</summary>
    public ObservableCollection<ProxyConfig> Proxies { get; } = new();

    /// <summary>Proxy visibili nella lista per il provider selezionato.</summary>
    public ObservableCollection<ProxyConfig> VisibleProxies { get; } = new();

    [ObservableProperty] private ProxyConfig? _selectedProxy;
    [ObservableProperty] private bool _isProxyBusy;
    [ObservableProperty] private string _proxyStatusMessage = string.Empty;
    [ObservableProperty] private int _proxyTestConcurrency = 5;

    public ObservableCollection<string> TestProviders { get; } = new()
    {
        ProviderKeys.AnimeWorld,
        ProviderKeys.AnimeSaturn,
        ProviderKeys.StreamingCommunity,
        "httpbin (base)"
    };

    [ObservableProperty] private string _selectedTestProvider = ProviderKeys.AnimeWorld;

    // ==== Checkbox globali "usa proxy per..." ====
    public bool UseProxyAnimeWorld
    {
        get => _settings.Current.UseProxyForAnimeWorld;
        set
        {
            _settings.Current.UseProxyForAnimeWorld = value;
            _settings.Save();
            OnPropertyChanged();
            _shared.NotifyActiveProxyChanged();
        }
    }

    public bool UseProxyAnimeSaturn
    {
        get => _settings.Current.UseProxyForAnimeSaturn;
        set
        {
            _settings.Current.UseProxyForAnimeSaturn = value;
            _settings.Save();
            OnPropertyChanged();
            _shared.NotifyActiveProxyChanged();
        }
    }

    public bool UseProxyStreamingCommunity
    {
        get => _settings.Current.UseProxyForStreamingCommunity;
        set
        {
            _settings.Current.UseProxyForStreamingCommunity = value;
            _settings.Save();
            OnPropertyChanged();
            _shared.NotifyActiveProxyChanged();
        }
    }

    public bool UseProxyDownloads
    {
        get => _settings.Current.UseProxyForDownloads;
        set
        {
            _settings.Current.UseProxyForDownloads = value;
            _settings.Save();
            OnPropertyChanged();
            _shared.NotifyActiveProxyChanged();
        }
    }

    // ==== Abilitazione pulsanti ====
    public bool CanActivateForProvider
    {
        get
        {
            if (SelectedProxy == null) return false;
            if (SelectedTestProvider == "httpbin (base)") return false;
            return !ProxyService.IsEnabledFor(SelectedProxy, SelectedTestProvider);
        }
    }

    public bool CanDeactivateForProvider
    {
        get
        {
            if (SelectedTestProvider == "httpbin (base)") return false;
            return Proxies.Any(p => ProxyService.IsEnabledFor(p, SelectedTestProvider));
        }
    }

    public bool CanRotateForProvider => CanDeactivateForProvider;
    public string VisibleCountText => $"{VisibleProxies.Count} / {Proxies.Count}";

    public ProxyTabViewModel(
        ProxyService proxyService,
        IAnimeWorldService animeWorldService,
        IAnimeSaturnService animeSaturnService,
        IStreamingCommunityService streamingCommunityService,
        DomainResolver domainResolver,
        SettingsService settings,
        SharedState shared)
    {
        _proxyService = proxyService;
        _animeWorldService = animeWorldService;
        _animeSaturnService = animeSaturnService;
        _streamingCommunityService = streamingCommunityService;
        _domainResolver = domainResolver;
        _settings = settings;
        _shared = shared;

        _ = Task.Run(ProxyLoadAllAsync);
    }

    // ============================================================
    //  PROPERTY CHANGED
    // ============================================================

    partial void OnSelectedProxyChanged(ProxyConfig? value)
    {
        OnPropertyChanged(nameof(CanActivateForProvider));
        OnPropertyChanged(nameof(CanDeactivateForProvider));
        OnPropertyChanged(nameof(CanRotateForProvider));
    }

    partial void OnSelectedTestProviderChanged(string value)
    {
        foreach (var proxy in Proxies)
        {
            proxy.TestResult = proxy.ProviderResults.TryGetValue(value, out var r) ? r : "";
        }

        RefreshVisibleProxies();
        RefreshActiveForSelectedProvider();

        OnPropertyChanged(nameof(CanActivateForProvider));
        OnPropertyChanged(nameof(CanDeactivateForProvider));
        OnPropertyChanged(nameof(CanRotateForProvider));

        // Log per capire cosa succede al cambio provider
        var totalRemoved = Proxies.Sum(p => p.RemovedFromProviders.Count);
        ProxyStatusMessage = $"Vista {value}. Totale item \"removed\": {totalRemoved}. Visibili: {VisibleProxies.Count}/{Proxies.Count}.";
    }

    private void RefreshVisibleProxies()
    {
        var key = SelectedTestProvider;
        if (string.IsNullOrEmpty(key)) return;

        VisibleProxies.Clear();

        foreach (var p in Proxies)
        {
            if (p.RemovedFromProviders.Contains(key)) continue;
            VisibleProxies.Add(p);
        }

        System.Diagnostics.Debug.WriteLine(
            $"[PROXY] Refresh key={key}, Proxies={Proxies.Count}, Visible={VisibleProxies.Count}");
        OnPropertyChanged(nameof(VisibleCountText));
    }
    private void RefreshActiveForSelectedProvider()
    {
        foreach (var p in Proxies)
            p.IsActiveForSelectedProvider = ProxyService.IsEnabledFor(p, SelectedTestProvider);
    }

    // ============================================================
    //  CARICAMENTO
    // ============================================================

    [RelayCommand]
    private async Task ProxyLoadAllAsync()
    {
        try
        {
            await _proxyService.LoadAllAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Proxies.Clear();
                foreach (var p in _proxyService.All)
                {
                    p.IsSelected = false;
                    Proxies.Add(p);
                }

                // Sincronizza TestResult con il provider corrente
                foreach (var p in Proxies)
                    p.TestResult = p.ProviderResults.TryGetValue(SelectedTestProvider, out var r)
                        ? r
                        : "";

                RefreshVisibleProxies();
                RefreshActiveForSelectedProvider();

                // ▼▼▼ Notifica dal thread UI (fix badge) ▼▼▼
                _shared.NotifyActiveProxyChanged();
            });

            ProxyStatusMessage = Proxies.Count == 0
                ? "Nessun proxy configurato."
                : $"{Proxies.Count} proxy disponibili.";
        }
        catch (Exception ex) { ProxyStatusMessage = $"Errore: {ex.Message}"; }
    }

    // ============================================================
    //  CRUD
    // ============================================================

    [RelayCommand]
    private void ProxyNew()
    {
        SelectedProxy = null;
        var p = new ProxyConfig { Name = "Nuovo proxy", IsSelected = false };
        Proxies.Add(p);
        VisibleProxies.Add(p);
        SelectedProxy = p;
        ProxyStatusMessage = "Nuovo proxy aggiunto. Compila i campi e premi 💾.";
    }

    [RelayCommand]
    private async Task ProxySaveAsync()
    {
        if (SelectedProxy == null) return;

        try
        {
            IsProxyBusy = true;
            await _proxyService.SaveAsync(SelectedProxy);
            ProxyStatusMessage = $"Proxy '{SelectedProxy.Name}' salvato.";
            _shared.NotifyActiveProxyChanged();
            NotifyAllProvidersInBackground();
        }
        catch (Exception ex) { ProxyStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsProxyBusy = false; }
    }

    [RelayCommand]
    private async Task ProxyDeleteFailedAsync()
    {
        var failed = Proxies.Where(p => p.IsFailed || p.TestResult.StartsWith("❌")).ToList();
        if (failed.Count == 0) { ProxyStatusMessage = "Nessun proxy fallito."; return; }

        IsProxyBusy = true;
        try
        {
            await _proxyService.DeleteManyAsync(failed);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RemoveFromLists(failed);
                if (SelectedProxy != null && failed.Contains(SelectedProxy))
                    SelectedProxy = VisibleProxies.FirstOrDefault();
            });

            ProxyStatusMessage = $"{failed.Count} proxy falliti eliminati.";
        }
        finally { IsProxyBusy = false; }
    }

    [RelayCommand]
    private async Task ProxyDeleteAsync()
    {
        if (SelectedProxy == null) return;
        try
        {
            await _proxyService.DeleteAsync(SelectedProxy);
            Proxies.Remove(SelectedProxy);
            VisibleProxies.Remove(SelectedProxy);
            SelectedProxy = VisibleProxies.FirstOrDefault();
            ProxyStatusMessage = "Proxy eliminato.";
        }
        catch (Exception ex) { ProxyStatusMessage = $"Errore: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ProxyDeleteSelectedAsync()
    {
        var targets = Proxies.Where(p => p.IsSelected).ToList();
        if (targets.Count == 0) { ProxyStatusMessage = "Nessun selezionato."; return; }

        IsProxyBusy = true;
        try
        {
            await _proxyService.DeleteManyAsync(targets);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                RemoveFromLists(targets);
                if (SelectedProxy != null && targets.Contains(SelectedProxy))
                    SelectedProxy = VisibleProxies.FirstOrDefault();
            });
            ProxyStatusMessage = $"{targets.Count} proxy eliminati.";
        }
        finally { IsProxyBusy = false; }
    }

    [RelayCommand]
    private async Task ProxyDeleteAllAsync()
    {
        if (Proxies.Count == 0) return;
        var snapshot = Proxies.ToList();

        IsProxyBusy = true;
        try
        {
            await _proxyService.DeleteManyAsync(snapshot);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Proxies.Clear();
                VisibleProxies.Clear();
                SelectedProxy = null;
            });
            ProxyStatusMessage = $"Eliminati {snapshot.Count} proxy.";
        }
        finally { IsProxyBusy = false; }
    }

    // ============================================================
    //  RIMUOVI DALLA LISTA CORRENTE (senza toccare il DB)
    // ============================================================

    [RelayCommand]
    private async Task ProxyRemoveFromCurrentListAsync()
    {
        if (SelectedTestProvider == "httpbin (base)")
        {
            ProxyStatusMessage = "Seleziona un provider.";
            return;
        }

        var targets = VisibleProxies.Where(p => p.IsSelected).ToList();
        if (targets.Count == 0)
        {
            ProxyStatusMessage = "Nessun proxy selezionato.";
            return;
        }

        var key = SelectedTestProvider;

        foreach (var p in targets)
        {
            p.ProviderResults.Remove(key);
            ProxyService.SetEnabledFor(p, key, false);
            ProxyService.SetManuallyDisabledFor(p, key, false);
            p.RemovedFromProviders.Add(key);
            if (key == SelectedTestProvider) p.TestResult = "";
        }
        await _proxyService.SaveManyAsync(targets);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RefreshVisibleProxies();
            RefreshActiveForSelectedProvider();
        });

        _shared.NotifyActiveProxyChanged();
        NotifyAllProvidersInBackground();

        OnPropertyChanged(nameof(CanActivateForProvider));
        OnPropertyChanged(nameof(CanDeactivateForProvider));
        OnPropertyChanged(nameof(CanRotateForProvider));

        ProxyStatusMessage = $"Rimossi {targets.Count} proxy dalla lista {key}. Visibili: {VisibleProxies.Count}/{Proxies.Count}.";
    }

    [RelayCommand]
    private async Task ProxyClearFailedFromCurrentListAsync()
    {
        if (SelectedTestProvider == "httpbin (base)")
        {
            ProxyStatusMessage = "Seleziona un provider.";
            return;
        }

        var key = SelectedTestProvider;

        var failed = VisibleProxies
            .Where(p => p.ProviderResults.TryGetValue(key, out var r) && r.StartsWith("❌"))
            .ToList();

        if (failed.Count == 0)
        {
            ProxyStatusMessage = $"Nessun proxy fallito per {key}.";
            return;
        }

        foreach (var p in failed)
        {
            p.ProviderResults.Remove(key);
            ProxyService.SetEnabledFor(p, key, false);
            ProxyService.SetManuallyDisabledFor(p, key, false);
            p.RemovedFromProviders.Add(key);
            if (key == SelectedTestProvider) p.TestResult = "";
        }
        await _proxyService.SaveManyAsync(failed);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RefreshVisibleProxies();
            RefreshActiveForSelectedProvider();
        });

        _shared.NotifyActiveProxyChanged();
        NotifyAllProvidersInBackground();

        OnPropertyChanged(nameof(CanActivateForProvider));
        OnPropertyChanged(nameof(CanDeactivateForProvider));
        OnPropertyChanged(nameof(CanRotateForProvider));

        ProxyStatusMessage = $"Rimossi {failed.Count} proxy falliti dalla lista {key}.";
    }

    // ============================================================
    //  SELEZIONE
    // ============================================================

    [RelayCommand]
    private void ProxySelectAll()
    {
        foreach (var p in VisibleProxies) p.IsSelected = true;
        ProxyStatusMessage = $"Selezionati {VisibleProxies.Count}.";
    }

    [RelayCommand]
    private void ProxyDeselectAll()
    {
        foreach (var p in VisibleProxies) p.IsSelected = false;
        ProxyStatusMessage = "Selezione azzerata.";
    }

    // ============================================================
    //  TEST
    // ============================================================

    [RelayCommand]
    private async Task ProxyTestSelectedAsync()
    {
        var targets = VisibleProxies.Where(p => p.IsSelected).ToList();
        if (targets.Count == 0) { ProxyStatusMessage = "Nessun selezionato."; return; }
        await RunProxyTestBatchAsync(targets, "selezionati");
    }

    [RelayCommand]
    private async Task ProxyTestAllAsync()
    {
        if (VisibleProxies.Count == 0) return;
        await RunProxyTestBatchAsync(VisibleProxies.ToList(), "totali");
    }

    private async Task RunProxyTestBatchAsync(List<ProxyConfig> targets, string label)
    {
        if (_proxyTestCts != null)
        {
            ProxyStatusMessage = "Test già in corso.";
            return;
        }

        _proxyTestCts = new CancellationTokenSource();
        var token = _proxyTestCts.Token;

        IsProxyBusy = true;
        try
        {
            int total = targets.Count;
            int completed = 0, okCount = 0, failCount = 0;
            var gate = new SemaphoreSlim(Math.Max(1, ProxyTestConcurrency));
            string providerChoice = SelectedTestProvider;

            var tasks = targets.Select(async proxy =>
            {
                await gate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    proxy.IsTesting = true;
                    proxy.TestResult = "Test in corso...";

                    var (ok, msg, _) = await TestSingleAsync(proxy, providerChoice, token);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var fullResult = ok ? $"✅ {msg}" : $"❌ {msg}";
                        proxy.IsTesting = false;
                        proxy.LastTested = DateTime.Now;
                        proxy.SetProviderResult(providerChoice, fullResult);

                        if (providerChoice == SelectedTestProvider)
                            proxy.TestResult = fullResult;

                        completed++;
                        if (ok) okCount++; else failCount++;

                        ProxyStatusMessage = $"Test {label} [{providerChoice}]: {completed}/{total} — {okCount} ok, {failCount} ko";
                    });

                    try { await _proxyService.SaveAsync(proxy); } catch { }

                    if (providerChoice != "httpbin (base)")
                    {
                        await _proxyService.ApplyAutoSelectAfterTestAsync(proxy, providerChoice, ok);
                    }
                }
                catch (OperationCanceledException) { }
                finally { gate.Release(); }
            }).ToList();

            try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }

            if (!token.IsCancellationRequested)
            {
                ProxyStatusMessage = $"Test {label} completato: {okCount}/{total} funzionanti.";
                NotifyAllProvidersInBackground();
                RefreshActiveForSelectedProvider();

                OnPropertyChanged(nameof(CanActivateForProvider));
                OnPropertyChanged(nameof(CanDeactivateForProvider));
                OnPropertyChanged(nameof(CanRotateForProvider));
            }
            else
            {
                ProxyStatusMessage = $"Test interrotto: {completed}/{total}.";
            }
        }
        finally
        {
            _proxyTestCts?.Dispose();
            _proxyTestCts = null;
            IsProxyBusy = false;
        }
    }

    private async Task<(bool ok, string msg, string? ip)> TestSingleAsync(
        ProxyConfig proxy, string provider, CancellationToken ct)
    {
        if (provider == "httpbin (base)")
            return await _proxyService.TestAsync(proxy, ct);

        if (provider == ProviderKeys.AnimeWorld)
        {
            var url = _domainResolver.GetBaseUrl("AnimeWorld");
            var (ok, msg) = await _proxyService.TestProviderAsync(proxy, "AnimeWorld", url, ct);
            return (ok, msg, null);
        }

        if (provider == ProviderKeys.AnimeSaturn)
        {
            var url = _domainResolver.GetBaseUrl("AnimeSaturn");
            var (ok, msg) = await _proxyService.TestProviderAsync(proxy, "AnimeSaturn", url, ct);
            return (ok, msg, null);
        }

        if (provider == ProviderKeys.StreamingCommunity)
        {
            var url = _domainResolver.GetBaseUrl("StreamingCommunity") + "/it";
            var (ok, msg) = await _proxyService.TestProviderAsync(proxy, "StreamingCommunity", url, ct);
            return (ok, msg, null);
        }

        return (false, "Provider sconosciuto", null);
    }

    [RelayCommand]
    private void ProxyStopTest()
    {
        if (_proxyTestCts == null) return;
        _proxyTestCts.Cancel();
        ProxyStatusMessage = "Interruzione test...";
    }

    // ============================================================
    //  ATTIVAZIONE / ROTAZIONE (per provider)
    // ============================================================

    [RelayCommand]
    private async Task ProxyActivateAsync()
    {
        if (SelectedProxy == null) return;
        if (SelectedTestProvider == "httpbin (base)")
        {
            ProxyStatusMessage = "Seleziona un provider per attivare il proxy.";
            return;
        }

        var key = SelectedTestProvider;

        // Se il proxy era stato rimosso dalla lista corrente, lo ripristino
        if (SelectedProxy.RemovedFromProviders.Contains(key))
        {
            SelectedProxy.RemovedFromProviders.Remove(key);
            RefreshVisibleProxies();
            RefreshActiveForSelectedProvider();
        }

        // Test automatico se manca un ✅ per questo provider
        if (!SelectedProxy.HasTestOkFor(key))
        {
            ProxyStatusMessage = $"Test automatico su {key}...";
            var (ok, msg, _) = await TestSingleAsync(SelectedProxy, key, CancellationToken.None);
            SelectedProxy.SetProviderResult(key, ok ? $"✅ {msg}" : $"❌ {msg}");
            if (key == SelectedTestProvider)
                SelectedProxy.TestResult = ok ? $"✅ {msg}" : $"❌ {msg}";

            if (!ok)
            {
                ProxyStatusMessage = $"❌ Il proxy non funziona su {key}: {msg}";
                await _proxyService.SaveAsync(SelectedProxy);
                return;
            }
        }

        // Si salvano solo i proxy effettivamente cambiati, in un'unica transazione.
        var changed = Proxies.Where(p => p != SelectedProxy && ProxyService.IsEnabledFor(p, key)).ToList();
        foreach (var p in changed) ProxyService.SetEnabledFor(p, key, false);
        ProxyService.SetEnabledFor(SelectedProxy, key, true);
        ProxyService.ClearManualDisabled(SelectedProxy, key);
        changed.Add(SelectedProxy);

        await _proxyService.SaveManyAsync(changed);

        _shared.NotifyActiveProxyChanged();
        NotifyAllProvidersInBackground();
        RefreshActiveForSelectedProvider();

        OnPropertyChanged(nameof(CanActivateForProvider));
        OnPropertyChanged(nameof(CanDeactivateForProvider));
        OnPropertyChanged(nameof(CanRotateForProvider));

        ProxyStatusMessage = $"Proxy '{SelectedProxy.Name}' ATTIVO per {key}.";
    }

    [RelayCommand]
    private async Task ProxyDeactivateAsync()
    {
        if (SelectedTestProvider == "httpbin (base)")
        {
            ProxyStatusMessage = "Seleziona un provider.";
            return;
        }

        var key = SelectedTestProvider;
        var active = Proxies.Where(p => ProxyService.IsEnabledFor(p, key)).ToList();
        bool changed = active.Count > 0;

        foreach (var p in active)
        {
            ProxyService.SetEnabledFor(p, key, false);
            ProxyService.SetManuallyDisabledFor(p, key, true);
        }
        await _proxyService.SaveManyAsync(active);

        _shared.NotifyActiveProxyChanged();
        NotifyAllProvidersInBackground();

        RefreshActiveForSelectedProvider();

        OnPropertyChanged(nameof(CanActivateForProvider));
        OnPropertyChanged(nameof(CanDeactivateForProvider));
        OnPropertyChanged(nameof(CanRotateForProvider));

        ProxyStatusMessage = changed
            ? $"Proxy disattivato per {key}."
            : $"Nessun proxy attivo per {key}.";
    }

    [RelayCommand]
    private async Task ProxyRotateNextAsync()
    {
        if (SelectedTestProvider == "httpbin (base)")
        {
            ProxyStatusMessage = "Seleziona un provider.";
            return;
        }

        try
        {
            IsProxyBusy = true;
            var next = await _proxyService.RotateNextForAsync(SelectedTestProvider);

            if (next == null)
            {
                ProxyStatusMessage = $"Nessun proxy di riserva per {SelectedTestProvider}.";
                return;
            }

            _shared.NotifyActiveProxyChanged();
            NotifyAllProvidersInBackground();

            RefreshActiveForSelectedProvider();

            OnPropertyChanged(nameof(CanActivateForProvider));
            OnPropertyChanged(nameof(CanDeactivateForProvider));
            OnPropertyChanged(nameof(CanRotateForProvider));

            ProxyStatusMessage = $"Ruotato su: {next.Name} ({next.Host}:{next.Port}) per {SelectedTestProvider}";
        }
        finally { IsProxyBusy = false; }
    }

    // ============================================================
    //  IMPORT
    // ============================================================

    public void ProxyImportFromString(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) { ProxyStatusMessage = "File vuoto."; return; }

        var (parsed, errors) = DownloadManager.Services.Proxy.ProxyFileParser.Parse(content);

        if (parsed.Count == 0 && errors.Count == 0) { ProxyStatusMessage = "Nessun proxy trovato."; return; }

        int added = 0;
        foreach (var p in parsed)
        {
            if (string.IsNullOrEmpty(p.Name)) p.Name = p.DisplayText;
            p.IsSelected = false;
            Proxies.Add(p);
            VisibleProxies.Add(p);
            added++;
        }

        var msg = $"Importati {added} proxy.";
        if (errors.Count > 0) msg += $" {errors.Count} righe ignorate.";
        ProxyStatusMessage = msg;

        _ = SaveImportedAsync(parsed);
    }

    // ============================================================
    //  HELPER
    // ============================================================

    /// <summary>
    /// Chiude i browser dei provider (ripartono col nuovo proxy alla prossima richiesta).
    /// È lento, quindi gira in background per non bloccare la UI.
    /// </summary>
    private void NotifyAllProvidersInBackground() => _ = Task.Run(NotifyAllProvidersAsync);

    private async Task SaveImportedAsync(List<ProxyConfig> imported)
    {
        try { await _proxyService.SaveManyAsync(imported); }
        catch (Exception ex) { ProxyStatusMessage = $"Errore salvataggio import: {ex.Message}"; }
    }

    private void RemoveFromLists(ICollection<ProxyConfig> removed)
    {
        var set = removed.ToHashSet();
        for (int i = Proxies.Count - 1; i >= 0; i--) if (set.Contains(Proxies[i])) Proxies.RemoveAt(i);
        for (int i = VisibleProxies.Count - 1; i >= 0; i--) if (set.Contains(VisibleProxies[i])) VisibleProxies.RemoveAt(i);
        OnPropertyChanged(nameof(VisibleCountText));
    }

    private async Task NotifyAllProvidersAsync()
    {
        try { await _animeWorldService.NotifyProxyChangedAsync(); } catch { }
        try { await _animeSaturnService.NotifyProxyChangedAsync(); } catch { }
        try { await _streamingCommunityService.NotifyProxyChangedAsync(); } catch { }
    }
    [RelayCommand]
    private async Task ProxyResetFiltersAsync()
    {
        var affected = Proxies.Where(p => p.RemovedFromProviders.Count > 0).ToList();

        foreach (var p in affected) p.RemovedFromProviders.Clear();
        await _proxyService.SaveManyAsync(affected);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            RefreshVisibleProxies();
            RefreshActiveForSelectedProvider();
        });

        ProxyStatusMessage = $"Filtri rimossi: {affected.Count} proxy sono di nuovo visibili.";
    }
}