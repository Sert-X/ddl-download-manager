using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace DownloadManager.Helpers;

public static class DragGuard
{
    /// <summary>
    /// Il drag dei file parte solo premendo su una riga: sulla barra di scorrimento (o sull'intestazione)
    /// il trascinamento deve muovere la barra, non avviare un drag &amp; drop con il cursore "vietato".
    /// </summary>
    public static bool IsOnRow(object? source)
    {
        if (source is not Visual v) return false;
        if (v.FindAncestorOfType<ScrollBar>(includeSelf: true) != null) return false;
        return v.FindAncestorOfType<DataGridRow>(includeSelf: true) != null;
    }
}
