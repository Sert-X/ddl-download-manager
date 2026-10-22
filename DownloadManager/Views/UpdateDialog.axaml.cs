using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using DownloadManager.Models;
using DownloadManager.ViewModels;

namespace DownloadManager.Views;

/// <summary>Cosa va scaricato per l'aggiornamento (per mostrare MB scaricati e rimanenti).</summary>
/// <param name="TotalBytes">Byte da scaricare (pacchetto completo o solo le parti cambiate).</param>
/// <param name="IsDelta">true se si scaricano solo le differenze dalla versione installata.</param>
/// <param name="PackagesDir">Cartella in cui Velopack scrive i pacchetti (null se non nota).</param>
/// <param name="FileNames">File che verranno scaricati (per misurare i byte veri).</param>
public record UpdateDownloadInfo(long TotalBytes, bool IsDelta, string? PackagesDir, IReadOnlyList<string> FileNames)
{
    /// <summary>Oltre questo numero di delta Velopack scarica il pacchetto completo (suo valore predefinito).</summary>
    private const int MaxDeltas = 10;

    /// <summary>Ricava le informazioni dall'aggiornamento trovato da Velopack.</summary>
    public static UpdateDownloadInfo From(Velopack.UpdateManager mgr, Velopack.UpdateInfo update)
    {
        var deltas = update.DeltasToTarget ?? Array.Empty<Velopack.VelopackAsset>();
        // Velopack usa le delta se ci sono (e non sono troppe), altrimenti il pacchetto completo
        bool useDeltas = deltas.Length > 0 && deltas.Length <= MaxDeltas && update.BaseRelease != null;
        var assets = useDeltas ? deltas : new[] { update.TargetFullRelease };

        // Installazione Velopack: ...\DDLDownloadManager\current\ (app) e ...\packages\ (download)
        string? packagesDir = null;
        try
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('\\', '/'));
            if (current.Parent != null) packagesDir = Path.Combine(current.Parent.FullName, "packages");
        }
        catch { }

        return new UpdateDownloadInfo(
            assets.Sum(a => a.Size),
            useDeltas,
            packagesDir,
            assets.Select(a => a.FileName).Where(n => !string.IsNullOrEmpty(n)).ToList());
    }
}

public partial class UpdateDialog : Window
{
    private enum Phase { Confirm, Downloading, ReadyToRestart, Error }

    private string _newVersion = "";
    private UpdateDownloadInfo _info = new(0, false, null, Array.Empty<string>());
    private string _changelog = "";
    private Func<Action<int>, CancellationToken, Task>? _downloadAction;
    private CancellationTokenSource? _downloadCts;

    // Avanzamento
    private int _percent;
    private int _baselinePercent = -1;
    private long _shownBytes;
    private DispatcherTimer? _bytesTimer;
    private readonly Queue<(DateTime Time, long Bytes)> _samples = new();
    private DateTime _downloadStart;

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Cancel;

    public UpdateDialog()
    {
        InitializeComponent();
        Closed += (_, _) => StopDownloadTracking(cancel: Choice == UpdateChoice.Cancel);
    }

    /// <summary>Mostra il dialog e gestisce l'intero flusso (conferma → download → riavvio).</summary>
    public static async Task<UpdateChoice> ShowAsync(
        Window owner,
        string newVersion,
        UpdateDownloadInfo info,
        string changelog,
        Func<Action<int>, CancellationToken, Task> downloadAction)
    {
        var dlg = new UpdateDialog
        {
            _newVersion = newVersion,
            _info = info,
            _changelog = changelog ?? "",
            _downloadAction = downloadAction
        };

        dlg.ShowPhase(Phase.Confirm);
        await dlg.ShowDialog(owner);
        return dlg.Choice;
    }

    // ============================================================
    //  GESTIONE FASI
    // ============================================================

    private void ShowPhase(Phase phase)
    {
        ConfirmPanel.IsVisible = phase == Phase.Confirm;
        DownloadPanel.IsVisible = phase == Phase.Downloading;
        ReadyPanel.IsVisible = phase == Phase.ReadyToRestart;
        ErrorPanel.IsVisible = phase == Phase.Error;

        ButtonsPanel.Children.Clear();

        switch (phase)
        {
            case Phase.Confirm:
                SubtitleText.Text = $"Versione {_newVersion} pronta per l'installazione";
                VersionInfoText.Text =
                    $"È disponibile una nuova versione di DDL Download Manager.\n" +
                    $"Nuova versione: {_newVersion}";
                DownloadSizeText.Text = _info.TotalBytes > 0
                    ? _info.IsDelta
                        ? $"Da scaricare: {SftpRemoteEntry.FormatSize(_info.TotalBytes)} (solo le parti cambiate)"
                        : $"Da scaricare: {SftpRemoteEntry.FormatSize(_info.TotalBytes)}"
                    : "";
                DownloadSizeText.IsVisible = _info.TotalBytes > 0;

                ChangelogText.Text = string.IsNullOrWhiteSpace(_changelog)
                    ? "Nessun dettaglio disponibile per questa versione."
                    : _changelog;

                AddButton("Più tardi", "ghost", () => { Choice = UpdateChoice.Cancel; Close(); });
                AddButton("Aggiorna ora", "accent", () => _ = StartDownloadAsync());
                break;

            case Phase.Downloading:
                SubtitleText.Text = $"Download della versione {_newVersion} in corso...";
                AddButton("Annulla download", "danger", () =>
                {
                    Choice = UpdateChoice.Cancel;
                    Close();   // Closed ferma il download
                });
                break;

            case Phase.ReadyToRestart:
                SubtitleText.Text = $"Versione {_newVersion} scaricata";
                AddButton("Al prossimo avvio", "ghost", () => { Choice = UpdateChoice.InstallLater; Close(); });
                AddButton("Riavvia ora", "success", () => { Choice = UpdateChoice.InstallNow; Close(); });
                break;

            case Phase.Error:
                SubtitleText.Text = "Aggiornamento non riuscito";
                AddButton("Chiudi", "ghost", () => { Choice = UpdateChoice.Cancel; Close(); });
                break;
        }
    }

    private void AddButton(string text, string classes, Action onClick)
    {
        var btn = new Button { Content = text, MinWidth = 120 };
        if (!string.IsNullOrEmpty(classes)) btn.Classes.Add(classes);
        btn.Click += (_, _) => onClick();
        ButtonsPanel.Children.Add(btn);
    }

    // ============================================================
    //  DOWNLOAD
    // ============================================================

    private async Task StartDownloadAsync()
    {
        ShowPhase(Phase.Downloading);
        ResetProgress();

        _downloadCts = new CancellationTokenSource();
        _downloadStart = DateTime.UtcNow;
        _bytesTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => UpdateBytes());
        _bytesTimer.Start();

        try
        {
            if (_downloadAction != null)
                await _downloadAction(OnProgress, _downloadCts.Token);

            StopDownloadTracking(cancel: false);
            ShowPhase(Phase.ReadyToRestart);
        }
        catch (OperationCanceledException) when (_downloadCts?.IsCancellationRequested == true)
        {
            // Annullato dall'utente: la finestra si sta già chiudendo
        }
        catch (Exception ex)
        {
            StopDownloadTracking(cancel: false);
            ErrorText.Text = ex.ToString();
            ShowPhase(Phase.Error);
        }
    }

    private void StopDownloadTracking(bool cancel)
    {
        _bytesTimer?.Stop();
        _bytesTimer = null;
        if (cancel) { try { _downloadCts?.Cancel(); } catch { } }
    }

    private void ResetProgress()
    {
        ProgressBarCtrl.Value = 0;
        PercentText.Text = "0%";
        BytesText.Text = _info.TotalBytes > 0 ? $"0 B di {SftpRemoteEntry.FormatSize(_info.TotalBytes)}" : "Preparazione...";
        RemainingText.Text = _info.TotalBytes > 0 ? SftpRemoteEntry.FormatSize(_info.TotalBytes) : "-";
        SpeedText.Text = "-";
        _percent = 0;
        _baselinePercent = -1;
        _shownBytes = 0;
        _samples.Clear();
    }

    /// <summary>Percentuale da Velopack (0-100): serve se i byte veri non si possono misurare.</summary>
    private void OnProgress(int percent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            percent = Math.Clamp(percent, 0, 100);
            // Velopack a volte parte da un valore > 0: lo si considera il punto zero
            if (_baselinePercent < 0) _baselinePercent = percent;
            var range = 100.0 - _baselinePercent;
            _percent = percent >= 100 ? 100 : range <= 0 ? 100 : (int)Math.Round((percent - _baselinePercent) * 100.0 / range);
            _percent = Math.Clamp(_percent, 0, 100);
            UpdateBytes();
        });
    }

    /// <summary>
    /// Byte scaricati: misurati sui file che Velopack sta scrivendo (veri), oppure stimati
    /// dalla percentuale. Da qui: scaricato, rimanente, velocità e tempo stimato.
    /// </summary>
    private void UpdateBytes()
    {
        long total = _info.TotalBytes;
        long measured = MeasureDownloadedBytes();
        long bytes = measured > 0 ? measured : total > 0 ? (long)(total * _percent / 100.0) : 0;
        if (total > 0) bytes = Math.Min(bytes, total);
        if (bytes < _shownBytes) bytes = _shownBytes;   // mai all'indietro
        _shownBytes = bytes;

        int percent = total > 0 ? (int)(bytes * 100 / total) : _percent;
        ProgressBarCtrl.Value = percent;
        PercentText.Text = $"{percent}%";

        // Velocità sugli ultimi ~3 secondi
        var now = DateTime.UtcNow;
        _samples.Enqueue((now, bytes));
        while (_samples.Count > 1 && (now - _samples.Peek().Time).TotalSeconds > 3) _samples.Dequeue();
        double speed = 0;
        if (_samples.Count > 1)
        {
            var first = _samples.Peek();
            var seconds = (now - first.Time).TotalSeconds;
            if (seconds > 0.4) speed = (bytes - first.Bytes) / seconds;
        }

        if (total > 0)
        {
            long remaining = Math.Max(0, total - bytes);
            BytesText.Text = $"{SftpRemoteEntry.FormatSize(bytes)} di {SftpRemoteEntry.FormatSize(total)}";
            RemainingText.Text = remaining == 0
                ? "Applicazione dell'aggiornamento..."
                : speed > 0
                    ? $"{SftpRemoteEntry.FormatSize(remaining)} · circa {TransferJobBase.FormatEta(remaining / speed)}"
                    : SftpRemoteEntry.FormatSize(remaining);
        }
        else
        {
            BytesText.Text = $"{percent}%";
            RemainingText.Text = "-";
        }

        SpeedText.Text = speed > 0 ? $"{speed / 1024 / 1024:F1} MB/s" : "-";
    }

    /// <summary>Somma dei file dell'aggiornamento già scritti nella cartella dei pacchetti (anche parziali).</summary>
    private long MeasureDownloadedBytes()
    {
        var dir = _info.PackagesDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || _info.FileNames.Count == 0) return 0;

        long sum = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (!_info.FileNames.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase))) continue;
                var fi = new FileInfo(file);
                if (fi.LastWriteTimeUtc < _downloadStart.AddSeconds(-5)) continue;   // file di prima
                sum += fi.Length;
            }
        }
        catch { return 0; }
        return sum;
    }
}
