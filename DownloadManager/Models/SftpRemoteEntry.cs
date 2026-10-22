using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadManager.Models;

public partial class SftpRemoteEntry : ObservableObject
{
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

    [ObservableProperty] private bool _hasZeroByteIssue;

    /// <summary>
    /// Riassunto del contenuto per le cartelle, popolato durante il check 🔍:
    /// "N cartelle, M file · ultima mod: gg/mm/aaaa hh:mm"
    /// Per i file non viene popolato (resta vuoto).
    /// </summary>
    [ObservableProperty] private string _contentsInfo = string.Empty;

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

    /// <summary>
    /// Tooltip completo: combina info contenuto + eventuale warning 0-byte.
    /// </summary>
    public string FullTooltip
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(ContentsInfo)) parts.Add(ContentsInfo);
            if (!string.IsNullOrEmpty(ZeroByteTooltip)) parts.Add(ZeroByteTooltip);
            return string.Join("\n", parts);
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "?";
        if (bytes == 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F2} MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F2} GB";
    }

    partial void OnHasZeroByteIssueChanged(bool value)
    {
        OnPropertyChanged(nameof(ZeroByteTooltip));
        OnPropertyChanged(nameof(FullTooltip));
    }

    partial void OnContentsInfoChanged(string value)
    {
        OnPropertyChanged(nameof(FullTooltip));
    }
}