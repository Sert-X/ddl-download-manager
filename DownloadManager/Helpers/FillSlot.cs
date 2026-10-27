using System;
using Avalonia;
using Avalonia.Controls;

namespace DownloadManager.Helpers;

/// <summary>
/// Involucro per una tabella o lista che riempie lo spazio rimasto della pagina. Quando
/// <see cref="FillViewportPanel"/> chiede "di quanto spazio hai bisogno?" (altezza infinita)
/// risponde <see cref="MinFill"/> invece dell'altezza di tutte le righe: così la pagina si
/// allunga solo quanto serve perché la tabella resti utilizzabile, poi scorre.
/// In tutti gli altri casi si comporta come se non ci fosse.
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

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child == null) return default;
        if (double.IsInfinity(availableSize.Height))
        {
            Child.Measure(new Size(availableSize.Width, MinFill));
            return new Size(Child.DesiredSize.Width, MinFill);
        }
        // Nello spazio vero la tabella non scende sotto MinFill: se non ci sta, la pagina scorre
        Child.Measure(availableSize);
        return Child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
