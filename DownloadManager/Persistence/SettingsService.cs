using System.Text.Json;
using DownloadManager.Models;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Persistence;

public class SettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly string _settingsPath;
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

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_current, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_settingsPath, json);
            _logger.LogInformation("Impostazioni salvate in {Path}", _settingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Errore salvataggio impostazioni");
        }
    }
}