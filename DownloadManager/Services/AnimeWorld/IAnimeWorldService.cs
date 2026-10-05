using DownloadManager.Models;

namespace DownloadManager.Services.AnimeWorld;

/// <summary>
/// Interfaccia specifica di AnimeWorld. Estende IAnimeProvider per
/// compatibilità futura, ma mantiene il nome storico per non rompere
/// DI/binding esistenti.
/// </summary>
public interface IAnimeWorldService : IAnimeProvider
{
    // Nessun membro aggiuntivo: tutta la firma è in IAnimeProvider.
}