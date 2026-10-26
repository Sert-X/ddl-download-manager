using Avalonia;
using Avalonia.Controls;

namespace DownloadManager.Helpers;

/// <summary>
/// Messaggio di stato che "segue" la pagina: in cima resta al suo posto dentro la pagina, come
/// sempre; quando lo si supera scorrendo, compare in alto una copia identica (l'Overlay), larga
/// quanto il contenuto (senza coprire la barra di scorrimento).
/// Uso: &lt;ScrollViewer h:StickyBanner.Overlay="{Binding #CopiaDelMessaggio}"&gt;
/// </summary>
public class StickyBanner : AvaloniaObject
{
    /// <summary>
    /// La copia compare appena si scorre: ha lo stesso spazio sopra (10 px) del messaggio in cima
    /// alla pagina, quindi il messaggio resta fermo nello stesso punto.
    /// </summary>
    private const double Threshold = 0.5;

    public static readonly AttachedProperty<Control?> OverlayProperty =
        AvaloniaProperty.RegisterAttached<StickyBanner, ScrollViewer, Control?>("Overlay");

    public static Control? GetOverlay(ScrollViewer sv) => sv.GetValue(OverlayProperty);
    public static void SetOverlay(ScrollViewer sv, Control? value) => sv.SetValue(OverlayProperty, value);

    static StickyBanner()
    {
        OverlayProperty.Changed.AddClassHandler<ScrollViewer>((sv, e) =>
        {
            if (e.OldValue == null && e.NewValue != null)
            {
                sv.ScrollChanged += (_, _) => Update(sv);
                sv.SizeChanged += (_, _) => Update(sv);
            }
            Update(sv);
        });
    }

    private static void Update(ScrollViewer sv)
    {
        if (GetOverlay(sv) is not { } overlay) return;
        overlay.IsVisible = sv.Offset.Y > Threshold;
        // Stessa larghezza del contenuto: si lascia libero lo spazio della barra di scorrimento
        var bar = Math.Max(0, sv.Bounds.Width - sv.Viewport.Width);
        if (Math.Abs(overlay.Margin.Right - bar) > 0.5)
            overlay.Margin = new Thickness(overlay.Margin.Left, overlay.Margin.Top, bar, overlay.Margin.Bottom);
    }
}
