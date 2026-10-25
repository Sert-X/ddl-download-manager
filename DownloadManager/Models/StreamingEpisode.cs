using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class StreamingEpisode : ObservableObject, ISelectableItem
{
    [ObservableProperty] private string _number = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _link = string.Empty;
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private int _seasonNumber;
    [ObservableProperty] private int _episodeId;
}