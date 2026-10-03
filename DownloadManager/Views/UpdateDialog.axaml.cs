using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DownloadManager.Models;
using DownloadManager.ViewModels;

namespace DownloadManager.Views;

public partial class UpdateDialog : Window
{
    private enum Phase { Confirm, Downloading, ReadyToRestart, Error }

    private Phase _phase = Phase.Confirm;
    private string _newVersion = "";
    private long _totalBytes;
    private Func<Action<int>, Task>? _downloadAction;
    private int _lastPercent;

    // Per calcolo velocità/ETA
    private DateTime _downloadStartTime;
    private DateTime _lastSampleTime;
    private long _lastSampleBytes;

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Cancel;

    public UpdateDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Mostra il dialog e gestisce l'intero flusso (conferma → download → riavvio).
    /// Ritorna la scelta finale dell'utente.
    /// </summary>
    public static async Task<UpdateChoice> ShowAsync(
        Window owner,
        string newVersion,
        long packageBytes,
        Func<Action<int>, Task> downloadAction)
    {
        var dlg = new UpdateDialog
        {
            _newVersion = newVersion,
            _totalBytes = packageBytes,
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
        _phase = phase;

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
                    $"È disponibile una nuova versione di DDL Download Manager.\n\n" +
                    $"Nuova versione: {_newVersion}\n" +
                    $"Dimensione pacchetto: {SftpRemoteEntry.FormatSize(_totalBytes)}";

                AddButton("Più tardi", "ghost", () => { Choice = UpdateChoice.Cancel; Close(); });
                AddButton("Aggiorna ora", "accent", () => _ = StartDownloadAsync());
                break;

            case Phase.Downloading:
                SubtitleText.Text = $"Download della versione {_newVersion} in corso...";
                AddButton("Annulla download", "danger", () =>
                {
                    Choice = UpdateChoice.Cancel;
                    Close();
                });
                break;

            case Phase.ReadyToRestart:
                SubtitleText.Text = $"Versione {_newVersion} scaricata";
                AddButton("Al prossimo avvio", "ghost", () =>
                {
                    Choice = UpdateChoice.InstallLater;
                    Close();
                });
                AddButton("Riavvia ora", "success", () =>
                {
                    Choice = UpdateChoice.InstallNow;
                    Close();
                });
                break;

            case Phase.Error:
                SubtitleText.Text = "Aggiornamento non riuscito";
                AddButton("Chiudi", "ghost", () => { Choice = UpdateChoice.Cancel; Close(); });
                break;
        }
    }

    private void AddButton(string text, string classes, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            MinWidth = 120
        };
        if (!string.IsNullOrEmpty(classes))
            btn.Classes.Add(classes);
        btn.Click += (s, e) => onClick();
        ButtonsPanel.Children.Add(btn);
    }

    // ============================================================
    //  DOWNLOAD
    // ============================================================

    private async Task StartDownloadAsync()
    {
        ShowPhase(Phase.Downloading);
        ResetProgress();

        try
        {
            if (_downloadAction != null)
                await _downloadAction(OnProgress);

            ShowPhase(Phase.ReadyToRestart);
        }
        catch (Exception ex)
        {
            ErrorText.Text = ex.ToString();
            ShowPhase(Phase.Error);
        }
    }

    private void ResetProgress()
    {
        ProgressBarCtrl.Value = 0;
        BytesText.Text = $"0 B / {SftpRemoteEntry.FormatSize(_totalBytes)}";
        SpeedText.Text = "-";
        EtaText.Text = "-";
        PercentText.Text = "0%";

        _lastPercent = 0;
        _downloadStartTime = DateTime.UtcNow;
        _lastSampleTime = DateTime.UtcNow;
        _lastSampleBytes = 0;
    }

    /// <summary>
    /// Callback chiamata da Velopack con la percentuale 0-100.
    /// Aggiorna la UI con percentuale, velocità e ETA.
    /// </summary>
    private void OnProgress(int percent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            ProgressBarCtrl.Value = percent;
            PercentText.Text = $"{percent}%";

            long downloadedBytes = (long)(_totalBytes * (percent / 100.0));
            BytesText.Text = $"{SftpRemoteEntry.FormatSize(downloadedBytes)} / {SftpRemoteEntry.FormatSize(_totalBytes)}";

            // Calcola velocità (misurata da inizio download, con campioni periodici)
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSampleTime).TotalSeconds;

            if (elapsed >= 0.3 && percent != _lastPercent) // aggiorna max ~3 volte al secondo
            {
                long deltaBytes = downloadedBytes - _lastSampleBytes;
                double speed = deltaBytes / Math.Max(0.001, elapsed);

                SpeedText.Text = speed > 0
                    ? $"{speed / 1024 / 1024:F2} MB/s"
                    : "-";

                if (speed > 0 && downloadedBytes < _totalBytes)
                {
                    long remaining = _totalBytes - downloadedBytes;
                    double etaSec = remaining / speed;
                    EtaText.Text = FormatEta(etaSec);
                }
                else if (percent >= 100)
                {
                    EtaText.Text = "0s";
                }

                _lastSampleTime = now;
                _lastSampleBytes = downloadedBytes;
                _lastPercent = percent;
            }
        });
    }

    private static string FormatEta(double seconds)
    {
        if (seconds < 1) return "<1s";
        if (seconds < 60) return $"{(int)seconds}s";
        if (seconds < 3600) return $"{(int)(seconds / 60)}m {(int)(seconds % 60)}s";
        return $"{(int)(seconds / 3600)}h {(int)((seconds % 3600) / 60)}m";
    }
}