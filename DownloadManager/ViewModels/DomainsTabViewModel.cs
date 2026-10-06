using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadManager.Models;
using DownloadManager.Services.Domains;
using DownloadManager.Services.Anime.AnimeWorld;
using DownloadManager.Services.Anime.AnimeSaturn;
using Microsoft.Extensions.Logging;

namespace DownloadManager.ViewModels;

/// <summary>
/// Sub-ViewModel per il tab "Domini". Permette di modificare l'URL base
/// dei provider anime, testare la raggiungibilità e resettare ai default.
/// </summary>
public partial class DomainsTabViewModel : ViewModelBase
{
    private readonly DomainResolver _resolver;
    private readonly DomainTester _tester;
    private readonly ILogger<DomainsTabViewModel> _logger;
    private readonly SharedState _shared;
    private readonly IAnimeWorldService _animeWorldService;
    private readonly IAnimeSaturnService _animeSaturnService;

    public ObservableCollection<DomainConfig> Domains { get; } = new();

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isTestingAll;

    private CancellationTokenSource? _testAllCts;

    public DomainsTabViewModel(
        DomainResolver resolver,
        DomainTester tester,
        IAnimeWorldService animeWorldService,
        IAnimeSaturnService animeSaturnService,
        ILogger<DomainsTabViewModel> logger,
        SharedState shared)
    {
        _resolver = resolver;
        _tester = tester;
         _animeWorldService = animeWorldService;
         _animeSaturnService = animeSaturnService;
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
        });

        // Notifica al VM principale quando lo stato "issues" cambia
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
        // Re-init del provider per applicare subito il nuovo dominio
        _ = Task.Run(async () =>
        {
            try
            {
                if (string.Equals(cfg.ProviderName, "AnimeWorld", StringComparison.OrdinalIgnoreCase))
                    await _animeWorldService.NotifyProxyChangedAsync();
                else if (string.Equals(cfg.ProviderName, "AnimeSaturn", StringComparison.OrdinalIgnoreCase))
                    await _animeSaturnService.NotifyProxyChangedAsync();
            }
            catch { }
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

    /// <summary>
    /// Chiamato da App.axaml.cs all'avvio. Esegue un test di tutti i domini
    /// in background senza bloccare la UI.
    /// </summary>
    public async Task TestAllOnStartupAsync()
    {
        await Task.Delay(2500);   // lascia respirare l'avvio

        if (Domains.Count == 0) return;

        _logger.LogInformation("[DOMAIN] Test automatico all'avvio...");
        await _tester.TestManyAsync(Domains.ToList(), concurrency: 4);
        UpdateHasIssues();

        int ok = Domains.Count(d => d.Status == DomainStatus.Ok);
        int ko = Domains.Count(d => d.Status == DomainStatus.Unreachable);

        _logger.LogInformation("[DOMAIN] Test automatico: {Ok} OK, {Ko} KO", ok, ko);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            StatusMessage = ko == 0
                ? $"✅ Tutti i {ok} domini raggiungibili."
                : $"⚠ {ok} OK, {ko} non raggiungibili.";
        });
    }
}