namespace DownloadManager.Models;

/// <summary>Elemento di lista con casella di spunta (episodi anime e film/serie).</summary>
public interface ISelectableItem
{
    bool IsSelected { get; set; }
}
