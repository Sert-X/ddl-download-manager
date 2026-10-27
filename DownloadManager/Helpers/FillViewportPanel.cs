using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace DownloadManager.Helpers;

/// <summary>
/// Contenuto di uno ScrollViewer che riempie lo spazio visibile come una pagina normale
/// (le righe "*" con le tabelle si allargano fino in fondo), ma quando la finestra è troppo
/// bassa per le tabelle (<see cref="FillSlot"/>) si allunga quanto serve e la pagina scorre.
///
/// Il contenuto si misura una sola volta e sempre con la stessa altezza finché non cambia
/// qualcosa: Avalonia così riusa i calcoli già fatti e aggiorna solo ciò che cambia. (La prima
/// versione misurava la pagina due volte con altezze diverse a ogni aggiornamento: ogni
/// avanzamento di un trasferimento ricalcolava tutta la pagina e le tabelle, ~28 ms l'uno.)
/// </summary>
public class FillViewportPanel : Panel
{
    private ScrollViewer? _scroll;
    private readonly List<FillSlot> _slots = new();
    /// <summary>Altezza data al contenuto nell'ultima misura (≥ altezza visibile).</summary>
    private double _height;

    internal void Register(FillSlot slot) { if (!_slots.Contains(slot)) _slots.Add(slot); }
    internal void Unregister(FillSlot slot) => _slots.Remove(slot);

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

    private double _lastViewport;

    private void OnScrollPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != ScrollViewer.ViewportProperty) return;
        var h = _scroll?.Viewport.Height ?? 0;
        // Solo se cambia davvero l'altezza visibile (ridimensionamento della finestra)
        if (Math.Abs(h - _lastViewport) > 0.5) InvalidateMeasure();
    }

    private double ViewportHeight(Size available)
    {
        var h = _scroll?.Viewport.Height ?? 0;
        if (h > 0 && !double.IsInfinity(h)) return h;
        return double.IsInfinity(available.Height) ? 0 : available.Height;
    }

    /// <summary>
    /// Quanto manca (positivo) o avanza (negativo) alle tabelle visibili rispetto alla loro
    /// altezza minima. Vale la tabella più "stretta".
    /// </summary>
    private double SlotBalance()
    {
        double balance = double.NegativeInfinity;
        foreach (var slot in _slots)
        {
            if (!slot.IsEffectivelyVisible || double.IsNaN(slot.LastAvailableHeight)) continue;
            balance = Math.Max(balance, slot.MinFill - slot.LastAvailableHeight);
        }
        return balance;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double viewport = ViewportHeight(availableSize);
        _lastViewport = viewport;
        // Si parte dall'altezza dell'ultima volta: se nulla è cambiato, la misura è già pronta
        double height = Math.Max(viewport, _height);
        double width = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, height));

            // Tabelle troppo basse: la pagina si allunga quanto manca. Spazio in avanzo (si è
            // chiusa una guida, la finestra è cresciuta): si accorcia, ma mai sotto il visibile.
            for (int i = 0; i < 3; i++)
            {
                double balance = SlotBalance();
                if (double.IsNegativeInfinity(balance)) break;
                double next = balance > 0.5 ? height + balance
                            : balance < -0.5 && height > viewport ? Math.Max(viewport, height + balance)
                            : height;
                if (Math.Abs(next - height) < 0.5) break;
                height = next;
                child.Measure(new Size(availableSize.Width, height));
            }
            width = Math.Max(width, child.DesiredSize.Width);
        }
        _height = height;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children) child.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
