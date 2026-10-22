using DownloadManager.Models;
using DownloadManager.Persistence;
using DownloadManager.Services.Sftp;
using Microsoft.Extensions.Logging;

namespace DownloadManager.Services.Videobox;

/// <summary>
/// Connessioni SFTP usate da Videobox Corrector, una per profilo (le sorgenti dello
/// stesso server la condividono). Il profilo 0 è la connessione della tab SFTP; gli
/// altri aprono una connessione dedicata, indipendente da quella della tab SFTP.
/// </summary>
public sealed class VideoboxSftpPool : IDisposable
{
    public const int SharedProfileId = 0;

    private readonly ISftpService _shared;
    private readonly SftpConfigRepository _profiles;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<VideoboxSftpPool> _logger;
    private readonly Dictionary<int, SftpWinScpService> _dedicated = new();
    private readonly Dictionary<int, string> _serverKeys = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public VideoboxSftpPool(ISftpService shared, SftpConfigRepository profiles, ILoggerFactory loggerFactory)
    {
        _shared = shared;
        _profiles = profiles;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<VideoboxSftpPool>();
    }

    /// <summary>La connessione della tab SFTP (le sue modifiche aggiornano l'albero).</summary>
    public ISftpService Shared => _shared;

    public async Task<List<SftpProfileOption>> GetProfileOptionsAsync()
    {
        var options = new List<SftpProfileOption> { new(SharedProfileId, "Connessione della tab SFTP") };
        try { options.AddRange((await _profiles.GetAllAsync()).Select(p => new SftpProfileOption(p.Id, p.Name))); }
        catch (Exception ex) { _logger.LogWarning(ex, "[VB-SFTP] Lettura profili fallita"); }
        return options;
    }

    public bool IsConnected(int profileId) => profileId == SharedProfileId
        ? _shared.IsConnected
        : _dedicated.TryGetValue(profileId, out var s) && s.IsConnected;

    /// <summary>
    /// Il profilo punta allo stesso server a cui è connessa la tab SFTP: le modifiche fatte
    /// lì (eliminazioni, rinomine, upload...) riguardano anche le sorgenti di questo profilo.
    /// </summary>
    public bool SharesServerWithTab(int profileId)
    {
        if (profileId == SharedProfileId) return true;
        var tab = _shared.ServerKey;
        return tab != null && _serverKeys.TryGetValue(profileId, out var key) && key == tab;
    }

    /// <summary>Connessione per il profilo, aperta se serve.</summary>
    public async Task<ISftpService> GetAsync(int profileId, CancellationToken ct = default)
    {
        if (profileId == SharedProfileId)
        {
            if (!_shared.IsConnected)
                throw new InvalidOperationException("la tab SFTP non è connessa");
            return _shared;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_dedicated.TryGetValue(profileId, out var existing) && existing.IsConnected) return existing;

            var config = await _profiles.GetAsync(profileId)
                         ?? throw new InvalidOperationException($"profilo SFTP {profileId} non trovato");

            existing?.Dispose();
            // Limitatore proprio: le connessioni di Videobox non tolgono slot ai trasferimenti
            var service = new SftpWinScpService(_loggerFactory.CreateLogger<SftpWinScpService>(), new SftpSessionLimiter(2));
            _logger.LogInformation("[VB-SFTP] Connessione al profilo '{Name}' ({Host})", config.Name, config.Host);
            await service.ConnectAsync(config, ct);
            _dedicated[profileId] = service;
            _serverKeys[profileId] = config.ServerKey;
            return service;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        foreach (var s in _dedicated.Values)
        {
            try { s.DisconnectAsync().Wait(TimeSpan.FromSeconds(3)); } catch { }
            try { s.Dispose(); } catch { }
        }
        _dedicated.Clear();
    }
}
