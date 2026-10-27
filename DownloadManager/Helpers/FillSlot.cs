using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DownloadManager.Helpers;

/// <summary>
/// Involucro per una tabella o lista che riempie lo spazio rimasto della pagina. Comunica a
/// <see cref="FillViewportPanel"/> quanto spazio ha ricevuto: se è meno di <see cref="MinFill"/>
/// la pagina si allunga (e scorre) perché la tabella resti utilizzabile. Per il resto si
/// comporta come se non ci fosse: la tabella si misura una volta sola, con lo spazio vero.
/// </summary>
public class FillSlot : Decorator
{
    public static readonly StyledProperty<double> MinFillProperty =
        AvaloniaProperty.Register<FillSlot, double>(nameof(MinFill), 220);

    /// <summary>Altezza minima della tabella prima che la pagina inizi a scorrere.</summary>
    public double MinFill
    {
        get => GetValue(MinFillProperty);
        set => SetValue(MinFillProperty, value);
    }

    /// <summary>Altezza ricevuta nell'ultima misura (NaN = non ancora misurata).</summary>
    internal double LastAvailableHeight { get; private set; } = double.NaN;

    private FillViewportPanel? _page;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _page = this.FindAncestorOfType<FillViewportPanel>();
        _page?.Register(this);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _page?.Unregister(this);
        _page = null;
        LastAvailableHeight = double.NaN;
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        LastAvailableHeight = availableSize.Height;
        if (Child == null) return default;
        // Senza un limite (fuori da FillViewportPanel, dentro un normale ScrollViewer) la
        // tabella leggerebbe tutte le righe: si ferma alla sua altezza minima.
        var size = double.IsInfinity(availableSize.Height) ? availableSize.WithHeight(MinFill) : availableSize;
        Child.Measure(size);
        return double.IsInfinity(availableSize.Height) ? Child.DesiredSize.WithHeight(MinFill) : Child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
