using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

/// <summary>
/// Una cartella sorgente per il merge. L'ordine nella lista definisce la priorità
/// (la prima ha priorità massima).
/// </summary>
public partial class MergeSourceFolder : ObservableObject
{
    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private int _priority;

    [ObservableProperty]
    private int _fileCount;

    public string DisplayName => string.IsNullOrEmpty(Path)
        ? "(nessuna cartella)"
        : System.IO.Path.GetFileName(Path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));

    partial void OnPathChanged(string value) => OnPropertyChanged(nameof(DisplayName));
}