using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DownloadManager.Helpers;

/// <summary>
/// ObservableCollection che può sostituire tutto il contenuto con una sola notifica.
///
/// Serve alle tabelle dei file: la DataGrid di Avalonia, se riceve migliaia di inserimenti e
/// rimozioni uno alla volta (cambio di cartella), sbaglia l'altezza totale della lista e la barra
/// di scorrimento "impazzisce" (provato: 290 righe calcolate come 2 milioni di pixel, e il
/// valore cresce a ogni cambio di cartella). Con una sola notifica "ricarica" il calcolo è giusto.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <param name="scrollToTop">Prima si svuota la tabella, così riparte dall'alto (cartella nuova).</param>
    public void ReplaceAll(IEnumerable<T> items, bool scrollToTop)
    {
        CheckReentrancy();
        Items.Clear();
        if (scrollToTop)
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
