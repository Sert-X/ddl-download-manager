using Avalonia.Data.Converters;

namespace DownloadManager.Helpers;

/// <summary>
/// Tipo del messaggio di stato, per l'icona e il colore del banner (StatusBannerTheme).
/// I messaggi possono iniziare con ✅ / ⚠ / ❌: il simbolo sceglie il tipo e non viene mostrato,
/// al suo posto il banner usa le icone dell'app. Senza simbolo: "Errore..." è un errore, il resto informazione.
/// </summary>
public static class StatusBannerTone
{
    public enum Kind { Info, Success, Warning, Error }

    private static readonly string[] SuccessMarks = { "✅", "✔", "✓" };
    private static readonly string[] WarningMarks = { "⚠" };
    private static readonly string[] ErrorMarks = { "❌", "✖", "✗", "✘", "⛔" };

    public static Kind KindOf(string? text)
    {
        var t = text?.TrimStart() ?? "";
        if (SuccessMarks.Any(t.StartsWith)) return Kind.Success;
        if (WarningMarks.Any(t.StartsWith)) return Kind.Warning;
        if (ErrorMarks.Any(t.StartsWith)) return Kind.Error;
        if (t.StartsWith("Errore", StringComparison.OrdinalIgnoreCase)) return Kind.Error;
        return Kind.Info;
    }

    /// <summary>Testo senza il simbolo iniziale (e senza il selettore di variante U+FE0F).</summary>
    public static string Clean(string? text)
    {
        var t = text?.TrimStart() ?? "";
        foreach (var mark in SuccessMarks.Concat(WarningMarks).Concat(ErrorMarks))
        {
            if (!t.StartsWith(mark)) continue;
            return t[mark.Length..].TrimStart('️', ' ');
        }
        return t;
    }

    public static readonly IValueConverter Text = new FuncValueConverter<object?, string>(o => Clean(o?.ToString()));
    public static readonly IValueConverter IsInfo = new FuncValueConverter<object?, bool>(o => KindOf(o?.ToString()) == Kind.Info);
    public static readonly IValueConverter IsSuccess = new FuncValueConverter<object?, bool>(o => KindOf(o?.ToString()) == Kind.Success);
    public static readonly IValueConverter IsWarning = new FuncValueConverter<object?, bool>(o => KindOf(o?.ToString()) == Kind.Warning);
    public static readonly IValueConverter IsError = new FuncValueConverter<object?, bool>(o => KindOf(o?.ToString()) == Kind.Error);
}
