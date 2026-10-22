using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DownloadManager.Helpers;

/// <summary>
/// Testi troncati con "…": passandoci sopra col mouse un tooltip mostra il testo completo.
/// Il tooltip compare solo se il testo è DAVVERO tagliato (si guarda il layout della
/// riga), e non tocca i TextBlock che hanno già un tooltip loro.
/// </summary>
public static class TrimmedTextToolTip
{
    /// <summary>TextBlock il cui tooltip è gestito da qui (per non sovrascrivere quelli scritti a mano).</summary>
    private static readonly ConditionalWeakTable<TextBlock, object> Managed = new();

    public static void Install()
    {
        Visual.BoundsProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Update(tb));
        TextBlock.TextProperty.Changed.AddClassHandler<TextBlock>((tb, _) => Update(tb));
    }

    private static void Update(TextBlock tb)
    {
        if (tb.TextTrimming == TextTrimming.None) return;

        bool managed = Managed.TryGetValue(tb, out _);
        if (!managed && ToolTip.GetTip(tb) != null) return;   // tooltip scritto a mano: non si tocca

        bool trimmed;
        try { trimmed = tb.TextLayout.TextLines.Any(l => l.HasCollapsed); }
        catch { return; }   // layout non ancora pronto

        if (trimmed)
        {
            if (!Equals(ToolTip.GetTip(tb), tb.Text)) ToolTip.SetTip(tb, tb.Text);
            if (!managed) Managed.Add(tb, new object());
        }
        else if (managed && ToolTip.GetTip(tb) != null)
        {
            ToolTip.SetTip(tb, null);
        }
    }
}
