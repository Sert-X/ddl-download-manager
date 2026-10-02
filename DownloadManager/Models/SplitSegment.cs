using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

/// <summary>
/// Un segmento di suddivisione: quanti episodi finiscono in una sottocartella.
/// </summary>
public partial class SplitSegment : ObservableObject
{
    /// <summary>
    /// Indice progressivo (1-based), aggiornato dal ViewModel per la UI.
    /// </summary>
    [ObservableProperty]
    private int _index;

    [ObservableProperty]
    private int _episodeCount = 50;
}