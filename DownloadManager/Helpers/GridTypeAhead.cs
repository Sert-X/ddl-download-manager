using System.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DownloadManager.Helpers;

/// <summary>
/// Ricerca per lettera come in Esplora risorse: premendo "S" si va al primo elemento che inizia
/// per S, ripremendo "S" al successivo; digitando in fretta "sta" si va al primo che inizia per "sta".
/// </summary>
public static class GridTypeAhead
{
    private static readonly TimeSpan ResetAfter = TimeSpan.FromMilliseconds(900);

    public static void Attach(DataGrid grid, Func<object, string?> nameOf)
    {
        var buffer = "";
        var last = DateTime.MinValue;

        grid.AddHandler(InputElement.TextInputEvent, (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
            if (grid.ItemsSource is not IEnumerable source) return;
            var items = source.Cast<object>().ToList();
            if (items.Count == 0) return;

            var now = DateTime.UtcNow;
            buffer = now - last > ResetAfter ? e.Text : buffer + e.Text;
            last = now;

            // Stessa lettera ripetuta ("sss"): si scorre tra gli elementi con quell'iniziale
            bool cycling = buffer.Length > 1 && buffer.All(c => char.ToLowerInvariant(c) == char.ToLowerInvariant(buffer[0]));
            var prefix = cycling ? buffer[..1] : buffer;

            int current = grid.SelectedItem != null ? items.IndexOf(grid.SelectedItem) : -1;
            // Nuova ricerca o lettera ripetuta: si parte dall'elemento dopo quello selezionato
            int start = buffer.Length == 1 || cycling ? current + 1 : Math.Max(current, 0);

            for (int n = 0; n < items.Count; n++)
            {
                var item = items[(start + n) % items.Count];
                var name = nameOf(item);
                if (name == null || !name.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase)) continue;

                grid.SelectedItem = item;
                grid.ScrollIntoView(item, null);
                e.Handled = true;
                return;
            }
        }, RoutingStrategies.Tunnel);
    }
}
