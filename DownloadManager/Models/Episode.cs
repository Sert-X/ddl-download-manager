using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class Episode : ObservableObject
{
    public string Number { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;

    [ObservableProperty]
    private bool _isSelected = true;   // selezionato di default
}