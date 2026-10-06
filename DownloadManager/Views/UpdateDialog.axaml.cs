using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DownloadManager.ViewModels;

namespace DownloadManager.Views;

public partial class UpdateDialog : Window
{
    private enum Phase { Confirm, Downloading, ReadyToRestart, Error }

    private Phase _phase = Phase.Confirm;
    private string _newVersion = "";
    private long _totalBytes;
    private string _changelog = "";
    private Func<Action<int>, Task>? _downloadAction;
    private int _lastPercent;
    private int _baselinePercent = -1;

    private DateTime _downloadStartTime;
    private DateTime _lastSampleTime;

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Cancel;

    public UpdateDialog()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Mostra il dialog e gestisce l'intero flusso (conferma → download → riavvio).
    /// </summary>
    public static async Task<UpdateChoice> ShowAsync(
        Window owner,
        string newVersion,
        long packageBytes,
        string changelog,
        Func<Action<int>, Task> downloadAction)
    {
        var dlg = new UpdateDialog
        {
            _newVersion = newVersion,
            _totalBytes = packageBytes,
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
                    $"È disponibile una nuova versione di DDL Download Manager.\n" +
                    $"Nuova versione: {_newVersion}";

                if (string.IsNullOrWhiteSpace(_changelog))
                {
                    ChangelogText.Text = "Nessun dettaglio disponibile per questa versione.";
                }
                else
                {
                    ChangelogText.Text = _changelog;
                }

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
        BytesText.Text = "Preparazione...";
        SpeedText.Text = "-";
        PercentText.Text = "";

        _lastPercent = 0;
        _baselinePercent = -1;
        _downloadStartTime = DateTime.UtcNow;
        _lastSampleTime = DateTime.UtcNow;
    }

    /// <summary>
    /// Callback chiamata da Velopack con la percentuale 0-100.
    /// Velopack non fornisce byte reali, solo percentuale del download da fare.
    /// Mostriamo % + velocità stimata in %/s.
    /// </summary>
    private void OnProgress(int percent)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            var now = DateTime.UtcNow;

            // Prima callback: registra la baseline e non calcolare velocità.
            // Velopack spesso parte da un valore > 0 (es. 70%) perché la
            // percentuale include lo staging interno; trattiamola come punto zero.
            if (_baselinePercent < 0)
            {
                _baselinePercent = percent;
                _lastPercent = percent;
                _lastSampleTime = now;

                ProgressBarCtrl.Value = percent;
                PercentText.Text = $"{percent}%";
                BytesText.Text = $"{percent}%";
                SpeedText.Text = "-";
                return;
            }

            // Proietta il progresso sull'intervallo [baseline, 100]
            // per mostrare 0-100% da dove ha davvero inizio il "download visibile".
            int adjusted;
            if (percent >= 100)
                adjusted = 100;
            else
            {
                var range = 100.0 - _baselinePercent;
                adjusted = range <= 0
                    ? 100
                    : (int)Math.Round((percent - _baselinePercent) * 100.0 / range);
                if (adjusted < 0) adjusted = 0;
                if (adjusted > 100) adjusted = 100;
            }

            ProgressBarCtrl.Value = adjusted;
            PercentText.Text = $"{adjusted}%";
            BytesText.Text = $"{percent}%";

            // Velocità: solo su callback consecutive con delta positivo reale
            var elapsed = (now - _lastSampleTime).TotalSeconds;
            if (elapsed >= 0.5 && percent > _lastPercent)
            {
                double deltaPercent = percent - _lastPercent;
                double speedPercent = deltaPercent / elapsed;

            if (speedPercent > 0 && _totalBytes > 0)
            {
                // Stima byte/s = (% / 100) × dimensione totale / secondi
                double bytesPerSecond = (speedPercent / 100.0) * _totalBytes;
                SpeedText.Text = $"{bytesPerSecond / 1024 / 1024:F1} MB/s";
            }
            else
            {
                SpeedText.Text = "-";
            }

                _lastSampleTime = now;
                _lastPercent = percent;
            }
        });
    }
}