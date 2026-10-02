namespace DownloadManager.Models;

public enum SftpSortMode
{
    Name,
    Size,
    Modified,
    Type
}

public record SortOption(string Label, SftpSortMode Mode);