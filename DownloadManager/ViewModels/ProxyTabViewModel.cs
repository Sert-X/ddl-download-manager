using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Services.Anime;
using DownloadManager.Services.Anime.AnimeWorld;
using DownloadManager.Services.Proxy;

namespace DownloadManager.ViewModels;

/// <summary>
/// Sub-ViewModel per il tab "Proxy". Gestisce la lista proxy, i test,
/// l'import da file, l'attivazione. Il badge in top bar resta sul MainVM
/// e viene notificato via SharedState.ActiveProxyChanged.
/// </summary>
public partial class ProxyTabViewModel : ViewModelBase
{
    private readonly ProxyService _proxyService;
    private readonly IAnimeWorldService _animeWorldService;
    private readonly SharedState _shared;

    private CancellationTokenSource? _proxyTestCts;

    public ObservableCollection<ProxyConfig> Proxies { get; } = new();

    [ObservableProperty] private ProxyConfig? _selectedProxy;
    [ObservableProperty] private bool _isProxyBusy;
    [ObservableProperty] private string _proxyStatusMessage = string.Empty;
    [ObservableProperty] private int _proxyTestConcurrency = 5;

    public ProxyTabViewModel(
        ProxyService proxyService,
        IAnimeWorldService animeWorldService,
        SharedState shared)
    {
        _proxyService = proxyService;
        _animeWorldService = animeWorldService;
        _shared = shared;

        _ = Task.Run(ProxyLoadAllAsync);
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
            Proxies.Clear();
            foreach (var p in _proxyService.All)
            {
                p.IsSelected = false;
                Proxies.Add(p);
            }

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

            // Assicura che solo UNO sia abilitato
            if (SelectedProxy.IsEnabled)
            {
                foreach (var p in Proxies.Where(p => p != SelectedProxy))
                    p.IsEnabled = false;
            }

            await _proxyService.SaveAsync(SelectedProxy);

            ProxyStatusMessage = $"Proxy '{SelectedProxy.Name}' salvato.";
            ApplyActiveProxy();
            _ = Task.Run(async () =>
            {
                try { await _animeWorldService.NotifyProxyChangedAsync(); }
                catch { }
            });
        }
        catch (Exception ex) { ProxyStatusMessage = $"Errore: {ex.Message}"; }
        finally { IsProxyBusy = false; }
    }

    [RelayCommand]
    private async Task ProxyDeleteFailedAsync()
    {
        var failed = Proxies.Where(p => p.IsFailed || p.TestResult.StartsWith("❌")).ToList();
        if (failed.Count == 0)
        {
            ProxyStatusMessage = "Nessun proxy fallito da eliminare.";
            return;
        }

        IsProxyBusy = true;
        try
        {
            foreach (var p in failed)
            {
                try { await _proxyService.DeleteAsync(p); } catch { }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var p in failed) Proxies.Remove(p);
                if (SelectedProxy != null && failed.Contains(SelectedProxy))
                    SelectedProxy = Proxies.FirstOrDefault();
            });

            ProxyStatusMessage = $"{failed.Count} proxy falliti eliminati.";
            ApplyActiveProxy();
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
            SelectedProxy = Proxies.FirstOrDefault();
            ProxyStatusMessage = "Proxy eliminato.";
            ApplyActiveProxy();
        }
        catch (Exception ex) { ProxyStatusMessage = $"Errore: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task ProxyDeleteSelectedAsync()
    {
        var targets = Proxies.Where(p => p.IsSelected).ToList();
        if (targets.Count == 0) { ProxyStatusMessage = "Nessun proxy selezionato."; return; }

        IsProxyBusy = true;
        try
        {
            var gate = new SemaphoreSlim(8);
            int ok = 0, fail = 0;

            var tasks = targets.Select(async p =>
            {
                await gate.WaitAsync();
                try
                {
                    await _proxyService.DeleteAsync(p);
                    Interlocked.Increment(ref ok);
                }
                catch { Interlocked.Increment(ref fail); }
                finally { gate.Release(); }
            }).ToList();

            await Task.WhenAll(tasks);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var p in targets) Proxies.Remove(p);
                if (SelectedProxy != null && targets.Contains(SelectedProxy))
                    SelectedProxy = Proxies.FirstOrDefault();
            });

            ProxyStatusMessage = fail == 0
                ? $"{ok} proxy eliminati."
                : $"{ok} eliminati, {fail} falliti.";
            ApplyActiveProxy();
        }
        finally { IsProxyBusy = false; }
    }

    [RelayCommand]
    private async Task ProxyDeleteAllAsync()
    {
        if (Proxies.Count == 0) { ProxyStatusMessage = "Lista già vuota."; return; }

        var snapshot = Proxies.ToList();
        IsProxyBusy = true;
        try
        {
            var gate = new SemaphoreSlim(8);
            await Task.WhenAll(snapshot.Select(async p =>
            {
                await gate.WaitAsync();
                try { await _proxyService.DeleteAsync(p); } catch { }
                finally { gate.Release(); }
            }));

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Proxies.Clear();
                SelectedProxy = null;
            });

            ProxyStatusMessage = $"Eliminati {snapshot.Count} proxy.";
            ApplyActiveProxy();
        }
        finally { IsProxyBusy = false; }
    }

    // ============================================================
    //  SELEZIONE
    // ============================================================

    [RelayCommand]
    private void ProxySelectAll()
    {
        foreach (var p in Proxies) p.IsSelected = true;
        ProxyStatusMessage = $"Selezionati {Proxies.Count} proxy.";
    }

    [RelayCommand]
    private void ProxyDeselectAll()
    {
        foreach (var p in Proxies) p.IsSelected = false;
        ProxyStatusMessage = "Selezione azzerata.";
    }

    // ============================================================
    //  TEST
    // ============================================================

    [RelayCommand]
    private async Task ProxyTestAsync()
    {
        if (SelectedProxy == null) { ProxyStatusMessage = "Nessun proxy selezionato."; return; }
        await RunProxyTestAsync(SelectedProxy);
    }

    [RelayCommand]
    private async Task ProxyTestSelectedAsync()
    {
        var targets = Proxies.Where(p => p.IsSelected).ToList();
        if (targets.Count == 0) { ProxyStatusMessage = "Nessun proxy selezionato."; return; }

        await RunProxyTestBatchAsync(targets, "selezionati");
    }

    [RelayCommand]
    private async Task ProxyTestAllAsync()
    {
        if (Proxies.Count == 0) { ProxyStatusMessage = "Nessun proxy da testare."; return; }
        await RunProxyTestBatchAsync(Proxies.ToList(), "totali");
    }

    private async Task RunProxyTestAsync(ProxyConfig proxy)
    {
        try
        {
            proxy.IsTesting = true;
            proxy.TestResult = "Test in corso...";

            var (ok, msg, _) = await _proxyService.TestAsync(proxy);

            proxy.IsTesting = false;
            proxy.TestResult = ok ? $"✅ {msg}" : $"❌ {msg}";
            proxy.LastTested = DateTime.Now;

            if (ReferenceEquals(proxy, SelectedProxy))
                ProxyStatusMessage = proxy.TestResult;
        }
        catch (Exception ex)
        {
            proxy.IsTesting = false;
            proxy.TestResult = $"❌ {ex.Message}";
        }
    }

    private async Task RunProxyTestBatchAsync(List<ProxyConfig> targets, string label)
    {
        if (_proxyTestCts != null)
        {
            ProxyStatusMessage = "Test già in corso. Fermalo prima di avviarne un altro.";
            return;
        }

        _proxyTestCts = new CancellationTokenSource();
        var token = _proxyTestCts.Token;

        IsProxyBusy = true;
        try
        {
            int total = targets.Count;
            int completed = 0;
            int okCount = 0;
            int failCount = 0;
            var gate = new SemaphoreSlim(Math.Max(1, ProxyTestConcurrency));

            var tasks = targets.Select(async proxy =>
            {
                await gate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();

                    proxy.IsTesting = true;
                    proxy.TestResult = "Test in corso...";

                    var (ok, msg, _) = await _proxyService.TestAsync(proxy, token);

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        proxy.IsTesting = false;
                        proxy.TestResult = ok ? $"✅ {msg}" : $"❌ {msg}";
                        proxy.LastTested = DateTime.Now;

                        completed++;
                        if (ok) okCount++; else failCount++;

                        ProxyStatusMessage = $"Test {label}: {completed}/{total} — {okCount} ok, {failCount} ko";
                    });

                    try { await _proxyService.SaveAsync(proxy); } catch { }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    gate.Release();
                }
            }).ToList();

            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { }

            if (token.IsCancellationRequested)
                ProxyStatusMessage = $"Test {label} interrotto: {completed}/{total} completati.";
            else
                ProxyStatusMessage = $"Test {label} completato: {okCount}/{total} funzionanti.";
        }
        finally
        {
            _proxyTestCts?.Dispose();
            _proxyTestCts = null;
            IsProxyBusy = false;
        }
    }

    [RelayCommand]
    private void ProxyStopTest()
    {
        if (_proxyTestCts == null)
        {
            ProxyStatusMessage = "Nessun test in corso.";
            return;
        }

        _proxyTestCts.Cancel();
        ProxyStatusMessage = "Interruzione test...";
    }

    // ============================================================
    //  ATTIVAZIONE
    // ============================================================

    [RelayCommand]
    private async Task ProxyToggleAsync()
    {
        if (SelectedProxy == null) return;

        SelectedProxy.IsEnabled = !SelectedProxy.IsEnabled;

        if (SelectedProxy.IsEnabled)
        {
            foreach (var p in Proxies.Where(p => p != SelectedProxy))
                p.IsEnabled = false;
        }

        foreach (var p in Proxies)
            await _proxyService.SaveAsync(p);

        ApplyActiveProxy();
        _ = Task.Run(async () =>
        {
            try { await _animeWorldService.NotifyProxyChangedAsync(); }
            catch { }
        });

        ProxyStatusMessage = SelectedProxy.IsEnabled
            ? $"Proxy '{SelectedProxy.Name}' ATTIVO."
            : "Proxy disattivato.";
    }

    // ============================================================
    //  IMPORT
    // ============================================================

    public void ProxyImportFromString(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            ProxyStatusMessage = "File vuoto.";
            return;
        }

        var (parsed, errors) = DownloadManager.Services.Proxy.ProxyFileParser.Parse(content);

        if (parsed.Count == 0 && errors.Count == 0)
        {
            ProxyStatusMessage = "Nessun proxy trovato nel file.";
            return;
        }

        int added = 0;
        foreach (var p in parsed)
        {
            if (string.IsNullOrEmpty(p.Name)) p.Name = p.DisplayText;
            p.IsSelected = false;
            Proxies.Add(p);
            added++;
        }

        var msg = $"Importati {added} proxy.";
        if (errors.Count > 0)
            msg += $" {errors.Count} righe ignorate.";

        ProxyStatusMessage = msg;

        _ = Task.Run(async () =>
        {
            foreach (var p in parsed)
            {
                try { await _proxyService.SaveAsync(p); } catch { }
            }
        });
    }

    // ============================================================
    //  BADGE
    // ============================================================

    private void ApplyActiveProxy()
    {
        var active = Proxies.FirstOrDefault(p => p.IsEnabled);

        if (active != null)
            ProxyStatusMessage = $"Proxy attivo: {active.Name} ({active.DisplayText})";
        else
            ProxyStatusMessage = "Nessun proxy attivo. Connessione diretta.";

        // Notifica il MainVM (badge top bar)
        _shared.NotifyActiveProxyChanged();
    }
}