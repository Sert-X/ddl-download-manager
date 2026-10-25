using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Domains;
using DownloadManager.Services.Anime.AnimeWorld;
using DownloadManager.Services.Anime.AnimeSaturn;
using DownloadManager.Services.Streaming.StreamingCommunity;
using Microsoft.Extensions.Logging;

namespace DownloadManager.ViewModels;

/// <summary>
/// Sub-ViewModel per il tab "Domini". Permette di modificare l'URL base
/// dei provider (anime + streaming), testare la raggiungibilità e resettare ai default.
/// I test usano il proxy attivo per ciascun provider (se configurato).
/// </summary>
public partial class DomainsTabViewModel : ViewModelBase
{
    private readonly DomainResolver _resolver;
    private readonly DomainTester _tester;
    private readonly ILogger<DomainsTabViewModel> _logger;
    private readonly SharedState _shared;
    private readonly IAnimeWorldService _animeWorldService;
    private readonly IAnimeSaturnService _animeSaturnService;
    private readonly IStreamingCommunityService _streamingCommunityService;
    private readonly SettingsService? _settings;

    /// <summary>Scelte per il ricontrollo periodico (minuti; 0 = solo all'avvio).</summary>
    public static IReadOnlyList<int> CheckIntervals { get; } = new[] { 0, 5, 10, 15, 30, 60 };
    public IReadOnlyList<string> CheckIntervalLabels { get; } =
        CheckIntervals.Select(m => m == 0 ? "Solo all'avvio" : $"Ogni {m} minuti").ToList();

    public int CheckIntervalIndex
    {
        get
        {
            var i = CheckIntervals.ToList().IndexOf(_settings?.Current.DomainCheckIntervalMinutes ?? 10);
            return i < 0 ? 2 : i;
        }
        set
        {
            if (_settings == null || value < 0 || value >= CheckIntervals.Count) return;
            _settings.Current.DomainCheckIntervalMinutes = CheckIntervals[value];
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NextCheckText));
            _intervalChanged?.Cancel();   // riparte subito col nuovo intervallo
        }
    }

    [ObservableProperty] private DateTime? _lastAutoCheck;
    public string NextCheckText
    {
        get
        {
            var min = _settings?.Current.DomainCheckIntervalMinutes ?? 10;
            if (min <= 0) return "Controllo automatico solo all'avvio.";
            var last = LastAutoCheck is { } t ? $"ultimo alle {t:HH:mm}, " : "";
            return $"Controllo automatico: {last}prossimo tra al massimo {min} minuti.";
        }
    }
    partial void OnLastAutoCheckChanged(DateTime? value) => OnPropertyChanged(nameof(NextCheckText));

    private CancellationTokenSource? _intervalChanged;

    public ObservableCollection<DomainConfig> Domains { get; } = new();

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isTestingAll;

    private CancellationTokenSource? _testAllCts;

    public DomainsTabViewModel(
        DomainResolver resolver,
        DomainTester tester,
        IAnimeWorldService animeWorldService,
        IAnimeSaturnService animeSaturnService,
        IStreamingCommunityService streamingCommunityService,
        ILogger<DomainsTabViewModel> logger,
        SharedState shared,
        SettingsService? settings = null)
    {
        _settings = settings;
        _resolver = resolver;
        _tester = tester;
        _animeWorldService = animeWorldService;
        _animeSaturnService = animeSaturnService;
        _streamingCommunityService = streamingCommunityService;
        _logger = logger;
        _shared = shared;

        LoadDomains();

        // Test automatico all'avvio (fire-and-forget, non blocca)
        _ = Task.Run(async () =>
        {
            try
            {
                await TestAllOnStartupAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DOMAIN] Test startup fallito");
            }
            await PeriodicCheckLoopAsync();
        });

        UpdateHasIssues();
    }

    // ============================================================
    //  CARICAMENTO
    // ============================================================

    private void LoadDomains()
    {
        Domains.Clear();
        foreach (var provider in _resolver.KnownProviders)
        {
            var currentUrl = _resolver.GetBaseUrl(provider);
            var defaultUrl = _resolver.GetDefaultUrl(provider);

            var cfg = new DomainConfig
            {
                ProviderName = provider,
                Url = currentUrl,
                DefaultUrl = defaultUrl,
                Status = DomainStatus.Unknown,
                StatusMessage = "Mai testato"
            };

            cfg.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DomainConfig.Status))
                    UpdateHasIssues();
            };

            Domains.Add(cfg);
        }

        StatusMessage = $"{Domains.Count} provider configurati.";
    }

    private void UpdateHasIssues()
    {
        var hasIssues = Domains.Any(d => d.Status == DomainStatus.Unreachable);

        Dispatcher.UIThread.Post(() =>
        {
            _shared.HasDomainIssues = hasIssues;
        });
    }

    // ============================================================
    //  TEST
    // ============================================================

    [RelayCommand]
    private async Task TestSingleAsync(DomainConfig? cfg)
    {
        if (cfg == null) return;

        // Invalida la cache: il proxy attivo potrebbe essere cambiato
        _tester.InvalidateCache();

        StatusMessage = $"Test {cfg.ProviderName}...";
        await _tester.TestAsync(cfg);
        StatusMessage = $"{cfg.ProviderName}: {cfg.StatusMessage}";
        UpdateHasIssues();
    }

    [RelayCommand]
    private async Task TestAllAsync()
    {
        if (IsTestingAll) return;
        if (Domains.Count == 0) return;

        // Invalida la cache: il proxy attivo potrebbe essere cambiato
        _tester.InvalidateCache();

        _testAllCts = new CancellationTokenSource();
        var token = _testAllCts.Token;

        IsTestingAll = true;
        StatusMessage = $"Test {Domains.Count} provider...";

        try
        {
            await _tester.TestManyAsync(Domains.ToList(), concurrency: 4, token);

            int ok = Domains.Count(d => d.Status == DomainStatus.Ok);
            int ko = Domains.Count(d => d.Status == DomainStatus.Unreachable);

            StatusMessage = ko == 0
                ? $"✅ Tutti i {ok} domini raggiungibili."
                : $"⚠ {ok} OK, {ko} non raggiungibili.";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Test annullato.";
        }
        finally
        {
            IsTestingAll = false;
            _testAllCts?.Dispose();
            _testAllCts = null;
            UpdateHasIssues();
        }
    }

    [RelayCommand]
    private void StopTestAll()
    {
        _testAllCts?.Cancel();
        StatusMessage = "Interruzione test...";
    }

    // ============================================================
    //  SALVATAGGIO / RESET
    // ============================================================

    [RelayCommand]
    private void SaveDomain(DomainConfig? cfg)
    {
        if (cfg == null) return;

        var url = (cfg.Url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            StatusMessage = $"{cfg.ProviderName}: URL vuoto, uso default.";
            _resolver.SetBaseUrl(cfg.ProviderName, "");
            cfg.Url = _resolver.GetBaseUrl(cfg.ProviderName);
            return;
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = $"{cfg.ProviderName}: URL deve iniziare con http:// o https://";
            return;
        }

        _resolver.SetBaseUrl(cfg.ProviderName, url);
        cfg.Url = _resolver.GetBaseUrl(cfg.ProviderName);

        StatusMessage = $"{cfg.ProviderName}: URL salvato ({cfg.Url})";

        // Notifica il provider per applicare subito il nuovo dominio
        _ = Task.Run(async () =>
        {
            try
            {
                if (string.Equals(cfg.ProviderName, "AnimeWorld", StringComparison.OrdinalIgnoreCase))
                    await _animeWorldService.NotifyProxyChangedAsync();
                else if (string.Equals(cfg.ProviderName, "AnimeSaturn", StringComparison.OrdinalIgnoreCase))
                    await _animeSaturnService.NotifyProxyChangedAsync();
                else if (string.Equals(cfg.ProviderName, "StreamingCommunity", StringComparison.OrdinalIgnoreCase))
                    await _streamingCommunityService.NotifyProxyChangedAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[DOMAIN] Errore notifica cambio dominio per {Provider}", cfg.ProviderName);
            }
        });

        _logger.LogInformation("[DOMAIN] {Provider}: salvato {Url}", cfg.ProviderName, cfg.Url);
    }

    [RelayCommand]
    private void ResetDomain(DomainConfig? cfg)
    {
        if (cfg == null) return;

        _resolver.SetBaseUrl(cfg.ProviderName, "");
        cfg.Url = _resolver.GetBaseUrl(cfg.ProviderName);
        cfg.Status = DomainStatus.Unknown;
        cfg.StatusMessage = "Mai testato";

        StatusMessage = $"{cfg.ProviderName}: reset al default ({cfg.Url})";
    }

    // ============================================================
    //  TEST AUTOMATICO ALL'AVVIO
    // ============================================================

    /// <summary>Ricontrolla i domini ogni N minuti (impostazione), finché l'app è aperta.</summary>
    private async Task PeriodicCheckLoopAsync()
    {
        while (true)
        {
            var minutes = _settings?.Current.DomainCheckIntervalMinutes ?? 10;
            _intervalChanged = new CancellationTokenSource();
            try
            {
                // Spento: si attende solo un cambio di impostazione
                await Task.Delay(minutes > 0 ? TimeSpan.FromMinutes(minutes) : Timeout.InfiniteTimeSpan, _intervalChanged.Token);
            }
            catch (OperationCanceledException) { continue; }   // intervallo cambiato: si ricomincia a contare

            if (IsTestingAll || Domains.Count == 0) continue;
            try
            {
                var before = Domains.ToDictionary(d => d, d => d.Status);
                _tester.InvalidateCache();
                await _tester.TestManyAsync(Domains.ToList(), concurrency: 4);
                UpdateHasIssues();

                var changed = Domains.Where(d => before.TryGetValue(d, out var old) && old != d.Status).ToList();
                int ko = Domains.Count(d => d.Status == DomainStatus.Unreachable);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    LastAutoCheck = DateTime.Now;
                    if (changed.Count > 0)
                        StatusMessage = ko == 0
                            ? $"Controllo automatico {DateTime.Now:HH:mm}: tutti i domini di nuovo raggiungibili."
                            : $"Controllo automatico {DateTime.Now:HH:mm}: {ko} domini non raggiungibili ({string.Join(", ", Domains.Where(d => d.Status == DomainStatus.Unreachable).Select(d => d.ProviderName))}).";
                });
                if (changed.Count > 0)
                    _logger.LogInformation("[DOMAIN] Controllo periodico: cambiati {Changed}, {Ko} non raggiungibili",
                        string.Join(", ", changed.Select(d => $"{d.ProviderName}={d.Status}")), ko);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[DOMAIN] Controllo periodico fallito"); }
        }
    }

    public async Task TestAllOnStartupAsync()
    {
        await Task.Delay(2500);

        if (Domains.Count == 0) return;

        // Invalida la cache: al primo avvio il proxy potrebbe essere già attivo
        _tester.InvalidateCache();

        _logger.LogInformation("[DOMAIN] Test automatico all'avvio...");
        await _tester.TestManyAsync(Domains.ToList(), concurrency: 4);
        UpdateHasIssues();

        int ok = Domains.Count(d => d.Status == DomainStatus.Ok);
        int ko = Domains.Count(d => d.Status == DomainStatus.Unreachable);

        _logger.LogInformation("[DOMAIN] Test automatico: {Ok} OK, {Ko} KO", ok, ko);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            LastAutoCheck = DateTime.Now;
            StatusMessage = ko == 0
                ? $"✅ Tutti i {ok} domini raggiungibili."
                : $"⚠ {ok} OK, {ko} non raggiungibili.";
        });
    }
}