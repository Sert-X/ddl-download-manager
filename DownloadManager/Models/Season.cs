using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class Season : ObservableObject
{
    [ObservableProperty] private int _number;
    [ObservableProperty] private string _title = string.Empty;
    public ObservableCollection<StreamingEpisode> Episodes { get; set; } = new();

    public string DisplayName => string.IsNullOrEmpty(Title) ? $"Stagione {Number}" : Title;
}