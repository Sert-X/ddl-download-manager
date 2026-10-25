using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DownloadManager.Models;

namespace DownloadManager.Helpers;

/// <summary>
/// Tiene allineate le caselle di spunta degli episodi e la selezione della griglia, così
/// valgono le scorciatoie di Windows: Ctrl+clic aggiunge/toglie, Shift+clic seleziona
/// l'intervallo dall'ultimo elemento cliccato (sia sulla riga sia sulla casella).
/// </summary>
public static class GridCheckboxSelection
{
    /// <param name="setSyncing">Segnala ai gestori SelectionChanged esistenti che la selezione la sta cambiando il codice.</param>
    public static void Attach(DataGrid grid, Action<bool> setSyncing)
    {
        ISelectableItem? anchor = null;

        void SyncGridFromCheckboxes()
        {
            if (grid.ItemsSource is not IEnumerable items || grid.SelectedItems == null) return;
            setSyncing(true);
            try
            {
                grid.SelectedItems.Clear();
                foreach (var it in items)
                    if (it is ISelectableItem { IsSelected: true }) grid.SelectedItems.Add(it);
            }
            finally { setSyncing(false); }
        }

        grid.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            if (e.Source is not Visual v || !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
            if (v.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is not ISelectableItem item) return;

            var onCheckBox = v.FindAncestorOfType<CheckBox>(includeSelf: true) != null;
            var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

            if (onCheckBox && shift && anchor != null && grid.ItemsSource is IEnumerable source)
            {
                // Shift sulla casella: spunta tutto l'intervallo, senza togliere le altre spunte
                var list = source.Cast<object>().ToList();
                int a = list.IndexOf(anchor), b = list.IndexOf(item);
                if (a >= 0 && b >= 0)
                {
                    for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++)
                        if (list[i] is ISelectableItem s) s.IsSelected = true;
                    e.Handled = true;   // niente toggle della casella cliccata
                    SyncGridFromCheckboxes();
                    return;
                }
            }

            // Ctrl/Shift sulle righe li gestisce la griglia; qui si ricorda solo il punto di partenza
            if (!shift) anchor = item;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Dopo il clic su una casella la griglia mostra le stesse righe selezionate
        grid.AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is CheckBox)
                Dispatcher.UIThread.Post(SyncGridFromCheckboxes);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
