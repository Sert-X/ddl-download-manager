using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DownloadManager.Helpers;

/// <summary>
/// Testo che scorre: passando col mouse su un testo tagliato con "…", il testo scorre
/// lentamente fino alla fine, si ferma un attimo e ricomincia, finché il mouse resta
/// sopra. Vale per tutti i TextBlock dell'app su una riga (il tooltip con il testo
/// completo resta).
///
/// Come funziona: durante l'animazione il TextBlock perde i "…" e viene spostato a
/// sinistra con una traslazione (che non tocca il layout); un ritaglio spostato della
/// stessa quantità lascia vedere solo la sua area originale. All'uscita del mouse si
/// rimettono i valori di prima.
/// </summary>
public static class MarqueeText
{
    private const double SpeedPixelsPerSecond = 45;
    private const int StartDelayMs = 500;
    private const int EndPauseMs = 1200;

    private static Marquee? _active;

    public static void Install()
    {
        InputElement.PointerEnteredEvent.AddClassHandler<TextBlock>((tb, _) => Start(tb));
        InputElement.PointerExitedEvent.AddClassHandler<TextBlock>((tb, _) => Stop(tb));
    }

    private static void Start(TextBlock tb)
    {
        if (_active?.Target == tb) return;
        _active?.Stop();
        _active = null;

        if (tb.TextTrimming == TextTrimming.None || tb.TextWrapping != TextWrapping.NoWrap) return;
        if (tb.Inlines is { Count: > 0 }) return;   // testo con più stili: si lascia il solo tooltip
        var text = tb.Text;
        if (string.IsNullOrEmpty(text)) return;

        try
        {
            if (!tb.TextLayout.TextLines.Any(l => l.HasCollapsed)) return;   // non è tagliato

            var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
            using var full = new TextLayout(text, typeface, tb.FontSize, null, letterSpacing: tb.LetterSpacing);
            var visible = tb.Bounds.Width - tb.Padding.Left - tb.Padding.Right;
            var distance = full.WidthIncludingTrailingWhitespace - visible + 2;
            if (distance < 4 || visible <= 0) return;

            _active = new Marquee(tb, distance);
        }
        catch
        {
            _active = null;   // layout non pronto: niente animazione
        }
    }

    private static void Stop(TextBlock tb)
    {
        if (_active?.Target != tb) return;
        _active.Stop();
        _active = null;
    }

    private sealed class Marquee
    {
        public TextBlock Target { get; }

        private readonly double _distance;
        private readonly DispatcherTimer _timer;
        private readonly double _width;
        private readonly TextTrimming _trimming;
        private readonly ITransform? _transform;
        private readonly Geometry? _clipGeometry;
        private readonly bool _clip;
        private readonly Size _size;
        private readonly TextAlignment _alignment;

        private int _phase;   // 0 = pausa iniziale, 1 = scorre, 2 = pausa alla fine
        private long _phaseStart = Environment.TickCount64;

        public Marquee(TextBlock target, double distance)
        {
            Target = target;
            _distance = distance;

            _width = target.Width;
            _trimming = target.TextTrimming;
            _transform = target.RenderTransform;
            _clipGeometry = target.Clip;
            _clip = target.ClipToBounds;
            _size = target.Bounds.Size;
            _alignment = target.TextAlignment;

            // Larghezza bloccata: senza "…" il testo chiederebbe più spazio al layout
            target.SetCurrentValue(Layoutable.WidthProperty, target.Bounds.Width);
            target.SetCurrentValue(TextBlock.TextTrimmingProperty, TextTrimming.None);
            target.SetCurrentValue(Visual.ClipToBoundsProperty, false);
            target.SetCurrentValue(TextBlock.TextAlignmentProperty, TextAlignment.Left);
            SetOffset(0);

            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
            _timer.Start();
        }

        private void Tick()
        {
            if (!Target.IsAttachedToVisualTree()) { Stop(); if (_active == this) _active = null; return; }

            var now = Environment.TickCount64;
            var elapsed = now - _phaseStart;
            switch (_phase)
            {
                case 0 when elapsed >= StartDelayMs:
                    _phase = 1; _phaseStart = now;
                    break;
                case 1:
                    var offset = Math.Min(_distance, elapsed / 1000.0 * SpeedPixelsPerSecond);
                    SetOffset(offset);
                    if (offset >= _distance) { _phase = 2; _phaseStart = now; }
                    break;
                case 2 when elapsed >= EndPauseMs:
                    SetOffset(0);
                    _phase = 0; _phaseStart = now;
                    break;
            }
        }

        private void SetOffset(double offset)
        {
            // Il ritaglio è nelle coordinate del TextBlock, quindi si sposta col testo:
            // lo si sposta di +offset perché resti fermo sull'area originale.
            Target.SetCurrentValue(Visual.RenderTransformProperty, new TranslateTransform(-offset, 0));
            Target.SetCurrentValue(Visual.ClipProperty, new RectangleGeometry(new Rect(offset, -2, _size.Width, _size.Height + 4)));
        }

        public void Stop()
        {
            _timer.Stop();
            Target.SetCurrentValue(Visual.RenderTransformProperty, _transform);
            Target.SetCurrentValue(Visual.ClipProperty, _clipGeometry);
            Target.SetCurrentValue(TextBlock.TextAlignmentProperty, _alignment);
            Target.SetCurrentValue(Visual.ClipToBoundsProperty, _clip);
            Target.SetCurrentValue(TextBlock.TextTrimmingProperty, _trimming);
            Target.SetCurrentValue(Layoutable.WidthProperty, _width);
        }
    }
}
