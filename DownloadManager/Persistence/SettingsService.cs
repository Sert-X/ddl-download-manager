using System.Text.Json;
using DownloadManager.Models;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence;

public class SettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsPath;
    private readonly object _saveLock = new();
    private AppSettings _current = new();

    public SettingsService(ILogger<SettingsService> logger)
    {
        _logger = logger;

        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DDLDownloadManager");
        Directory.CreateDirectory(appDataDir);

        _settingsPath = Path.Combine(appDataDir, "settings.json");
        Load();
    }

    public AppSettings Current => _current;

    public void Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                _current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                _logger.LogInformation("Impostazioni caricate da {Path}", _settingsPath);
            }
            else
            {
                _current = new AppSettings();
                Save();
            }

            // Default: se BaseDownloadFolder è vuota, usa Downloads dell'utente.
            if (string.IsNullOrWhiteSpace(_current.BaseDownloadFolder))
            {
                _current.BaseDownloadFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "Downloads",
                    "AnimeWorld");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore caricamento impostazioni");
            _current = new AppSettings();
        }
    }

    /// <summary>
    /// Salvataggio atomico: scrive su file .tmp con fsync, poi Move-with-overwrite.
    /// Se l'app crasha a metà scrittura, il vecchio settings.json resta intatto.
    /// Serializzato con lock per evitare corse su Save() concorrenti.
    /// </summary>
    public void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var json = JsonSerializer.Serialize(_current, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                var tmpPath = _settingsPath + ".tmp";

                // Scrittura su .tmp con flush-to-disk (fsync) per garantire
                // che i byte siano davvero sul disco prima del move.
                using (var fs = new FileStream(
                    tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(fs))
                {
                    writer.Write(json);
                    writer.Flush();
                    fs.Flush(flushToDisk: true);
                }

                // File.Move con overwrite: su Windows è un'operazione atomica
                // (rename nella stessa partizione). O il file vecchio, o il nuovo.
                File.Move(tmpPath, _settingsPath, overwrite: true);

                _logger.LogInformation("Impostazioni salvate in {Path}", _settingsPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore salvataggio impostazioni");
            }
        }
    }
}