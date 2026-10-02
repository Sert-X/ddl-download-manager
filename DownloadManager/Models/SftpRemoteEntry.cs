using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class SftpRemoteEntry : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;

    [ObservableProperty] private bool _isDirectory;
    [ObservableProperty] private long _size;
    [ObservableProperty] private DateTime _modified;
    [ObservableProperty] private bool _hasZeroByteIssue;

    public string SizeText => IsDirectory
        ? "-"
        : Size < 0
            ? "?"
            : FormatSize(Size);

    public string Icon => IsDirectory ? "📁" : "📄";

    public string ZeroByteTooltip => HasZeroByteIssue
        ? IsDirectory
            ? "Questa cartella contiene almeno un file da 0 byte"
            : "File da 0 byte"
        : string.Empty;

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "?";
        if (bytes == 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F2} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }

    partial void OnSizeChanged(long value)
    {
        OnPropertyChanged(nameof(SizeText));
    }

    partial void OnIsDirectoryChanged(bool value)
    {
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(Icon));
    }

    partial void OnHasZeroByteIssueChanged(bool value)
    {
        OnPropertyChanged(nameof(ZeroByteTooltip));
    }
}