using CommunityToolkit.Mvvm.ComponentModel;
using DownloadManager.Services.Sftp;

namespace DownloadManager.Models;

public partial class SftpRemoteEntry : CheckedFileEntry
{
    public override string EntryPath => FullPath;
    public override bool EntryIsDirectory => IsDirectory;
    public override long EntrySize => Size;
    public override DateTime EntryModified => Modified;

    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    // Notificano la UI: sostituendo un file (es. upload con lo stesso nome) dimensione e data
    // cambiano sulla riga già mostrata; prima si vedevano solo uscendo e rientrando.
    private bool _isDirectory;
    public bool IsDirectory
    {
        get => _isDirectory;
        set { if (SetProperty(ref _isDirectory, value)) { OnPropertyChanged(nameof(SizeText)); OnPropertyChanged(nameof(Icon)); } }
    }

    private long _size;
    public long Size
    {
        get => _size;
        set { if (SetProperty(ref _size, value)) OnPropertyChanged(nameof(SizeText)); }
    }

    private DateTime _modified;
    public DateTime Modified
    {
        get => _modified;
        set => SetProperty(ref _modified, value);
    }

    public string SizeText => IsDirectory
        ? "-"
        : Size < 0
            ? "?"
            : FormatSize(Size);

    public string Icon => IsDirectory ? "📁" : "📄";

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "?";
        if (bytes == 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F2} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }
}
