using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DownloadManager.Views;

public partial class BootstrapDialog : Window
{
    private readonly List<string> _logLines = new();
    private readonly TextBlock? _statusText;
    private readonly TextBlock? _progressDetailText;
    private readonly TextBlock? _logText;
    private readonly ProgressBar? _progressBar;

    public BootstrapDialog()
    {
        InitializeComponent();

        _statusText = this.FindControl<TextBlock>("StatusText");
        _progressDetailText = this.FindControl<TextBlock>("ProgressDetailText");
        _logText = this.FindControl<TextBlock>("LogText");
        _progressBar = this.FindControl<ProgressBar>("ProgressBar");
    }

    public void SetStatus(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_statusText != null) _statusText.Text = message;
            AppendLog(message);
        });
    }

    public void SetProgressDetail(string detail)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_progressDetailText != null) _progressDetailText.Text = detail;
        });
    }

    public void SetIndeterminate(bool indeterminate)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_progressBar != null)
                _progressBar.IsIndeterminate = indeterminate;
        });
    }

    public void SetProgress(double percentage)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_progressBar != null)
            {
                _progressBar.IsIndeterminate = false;
                _progressBar.Value = Math.Clamp(percentage, 0, 100);
            }
        });
    }

    public void AppendLog(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            _logLines.Add($"[{timestamp}] {line}");

            // Tieni solo le ultime 50 righe
            if (_logLines.Count > 50)
                _logLines.RemoveAt(0);

            if (_logText != null)
                _logText.Text = string.Join(Environment.NewLine, _logLines);
        });
    }

    /// <summary>
    /// Esegue il bootstrap: controlla e scarica Playwright se mancante.
    /// Ritorna true se tutto è OK, false se qualcosa è fallito (ma non bloccante).
    /// </summary>
    public async Task<bool> RunBootstrapAsync()
    {
        try
        {
            // === STEP 1: verifica Playwright ===
            SetStatus("Verifica installazione Playwright...");
            await Task.Delay(300);

            var browsersPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ms-playwright");

            bool chromiumExists = Directory.Exists(browsersPath) &&
                Directory.EnumerateDirectories(browsersPath, "chromium*").Any();

            if (chromiumExists)
            {
                AppendLog("Playwright già installato — skip.");
                SetStatus("Playwright già installato.");
                await Task.Delay(500);
                return true;
            }

            // === STEP 2: installa Playwright ===
            SetStatus("Download dei browser (Chromium ~150 MB)...");
            SetIndeterminate(true);
            SetProgressDetail("Questa operazione viene eseguita solo al primo avvio.");

            AppendLog("Avvio: Microsoft.Playwright.Program.Main install chromium");

            var exitCode = await Task.Run(() =>
                Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));

            if (exitCode != 0)
            {
                AppendLog($"ERRORE: exit code {exitCode}");
                SetStatus("Installazione browser fallita. L'app potrebbe non funzionare correttamente.");
                await Task.Delay(3000);
                return false;
            }

            AppendLog("Installazione completata con successo.");
            SetStatus("Installazione completata.");
            SetIndeterminate(false);
            SetProgress(100);

            // Verifica post-installazione
            bool nowExists = Directory.Exists(browsersPath) &&
                Directory.EnumerateDirectories(browsersPath, "chromium*").Any();

            if (nowExists)
            {
                AppendLog("Chromium installato correttamente.");
                await Task.Delay(800);
                return true;
            }
            else
            {
                AppendLog("ATTENZIONE: la cartella Chromium non è stata trovata dopo l'installazione.");
                await Task.Delay(2000);
                return false;
            }
        }
        catch (Exception ex)
        {
            AppendLog($"ECCEZIONE: {ex.Message}");
            SetStatus($"Errore durante la preparazione: {ex.Message}");
            await Task.Delay(3000);
            return false;
        }
    }
}