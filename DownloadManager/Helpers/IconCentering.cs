using Avalonia;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace DownloadManager.Helpers;

/// <summary>
/// Centra le icone (Path con classe "icon") nel loro riquadro.
///
/// Con Stretch="Uniform" Avalonia ridimensiona la geometria ma la disegna allineata
/// in alto a sinistra: le icone non quadrate (play, pausa, cartella, ...) risultano
/// spostate, cosa che si nota soprattutto dentro i pulsanti tondi. Qui si calcola lo
/// spazio avanzato e si trasla l'icona di metà, su entrambi gli assi.
/// </summary>
public static class IconCentering
{
    private const string IconClass = "icon";

    public static void Install()
    {
        Visual.BoundsProperty.Changed.AddClassHandler<Path>((path, _) => Center(path));
        Path.DataProperty.Changed.AddClassHandler<Path>((path, _) => Center(path));
    }

    private static void Center(Path path)
    {
        if (!path.Classes.Contains(IconClass) || path.Stretch != Stretch.Uniform) return;

        var geometry = path.Data?.Bounds ?? default;
        var box = path.Bounds.Size;
        if (box.Width <= 0 || box.Height <= 0 || (geometry.Width <= 0 && geometry.Height <= 0)) return;

        // Stessa scala che usa Uniform (una geometria piatta, es. il "meno", scala sull'altro asse)
        double scale = geometry.Width <= 0 ? box.Height / geometry.Height
                     : geometry.Height <= 0 ? box.Width / geometry.Width
                     : Math.Min(box.Width / geometry.Width, box.Height / geometry.Height);

        double dx = (box.Width - geometry.Width * scale) / 2;
        double dy = (box.Height - geometry.Height * scale) / 2;

        if (path.RenderTransform is TranslateTransform t && Math.Abs(t.X - dx) < 0.01 && Math.Abs(t.Y - dy) < 0.01)
            return;

        path.RenderTransform = new TranslateTransform(dx, dy);
    }
}
