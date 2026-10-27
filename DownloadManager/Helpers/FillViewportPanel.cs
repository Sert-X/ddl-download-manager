using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DownloadManager.Helpers;

/// <summary>
/// Contenuto di uno ScrollViewer che riempie lo spazio visibile come una pagina normale
/// (le righe "*" con le tabelle si allargano fino in fondo), ma quando la finestra è troppo
/// bassa per le altezze minime del contenuto si allunga e la pagina scorre.
/// Dentro un normale ScrollViewer l'altezza è infinita: le tabelle non riempirebbero più la
/// pagina e leggerebbero tutte le righe insieme (lente con migliaia di file).
/// </summary>
public class FillViewportPanel : Panel
{
    private ScrollViewer? _scroll;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _scroll = this.FindAncestorOfType<ScrollViewer>();
        if (_scroll != null) _scroll.PropertyChanged += OnScrollPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scroll != null) _scroll.PropertyChanged -= OnScrollPropertyChanged;
        _scroll = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ScrollViewer.ViewportProperty) InvalidateMeasure();
    }

    private double ViewportHeight(Size available)
    {
        var h = _scroll?.Viewport.Height ?? 0;
        if (h > 0 && !double.IsInfinity(h)) return h;
        return double.IsInfinity(available.Height) ? 0 : available.Height;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double viewport = ViewportHeight(availableSize);
        double width = 0, height = viewport;
        foreach (var child in Children)
        {
            // Altezza minima del contenuto: misura "senza limiti", in cui le tabelle avvolte in
            // FillSlot dichiarano solo la loro altezza minima (non tutte le righe).
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            double need = Math.Max(viewport, child.DesiredSize.Height);
            // Poi la misura vera: tutto lo spazio visibile, o di più se non basta (la pagina scorre)
            child.Measure(new Size(availableSize.Width, need));
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, need);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children) child.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
