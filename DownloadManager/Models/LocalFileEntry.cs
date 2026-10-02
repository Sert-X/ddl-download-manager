using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class LocalFileEntry : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;

    [ObservableProperty] private bool _isDirectory;
    [ObservableProperty] private long _size;
    [ObservableProperty] private DateTime _modified;

    // Property mai usata in locale, ma presente per uniformità con SftpRemoteEntry
    public bool HasZeroByteIssue { get; set; } = false;

    public string SizeText => IsDirectory
        ? "-"
        : Size >= 1024L * 1024 * 1024
            ? $"{Size / 1024.0 / 1024 / 1024:F2} GB"
            : Size >= 1024 * 1024
                ? $"{Size / 1024.0 / 1024:F2} MB"
                : $"{Size / 1024.0:F2} KB";

    public string Icon => IsDirectory ? "📁" : GetFileIcon();

    private string GetFileIcon()
    {
        var ext = Path.GetExtension(Name).ToLowerInvariant();
        return ext switch
        {
            ".mp4" or ".mkv" or ".avi" or ".mov" or ".webm" => "🎬",
            ".mp3" or ".flac" or ".wav" => "🎵",
            ".jpg" or ".jpeg" or ".png" or ".gif" => "🖼",
            ".zip" or ".rar" or ".7z" => "📦",
            ".pdf" => "📕",
            ".txt" or ".log" => "📄",
            _ => "📄"
        };
    }

    partial void OnSizeChanged(long value) => OnPropertyChanged(nameof(SizeText));
    partial void OnIsDirectoryChanged(bool value)
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Icon));
    }
}