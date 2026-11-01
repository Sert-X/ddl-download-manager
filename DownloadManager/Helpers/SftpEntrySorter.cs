using System.Collections.ObjectModel;
using DownloadManager.Models;

namespace DownloadManager.Helpers;

/// <summary>
/// Sort + sync per le liste SFTP (locale/remota).
/// Cartelle sempre prima dei file; ordinamento per nome/dimensione/data/tipo;
/// SyncCollection preserva l'identità degli oggetti (scroll + binding XAML).
/// </summary>
public static class SftpEntrySorter
{
    public static List<LocalFileEntry> SortLocalEntries(
        IEnumerable<LocalFileEntry> source, SftpSortMode mode, bool ascending)
    {
        var dirs = source.Where(e => e.IsDirectory).ToList();
        var files = source.Where(e => !e.IsDirectory).ToList();

        Comparison<LocalFileEntry> comp = mode switch
        {
            SftpSortMode.Size => (a, b) =>
            {
                int c = a.Size.CompareTo(b.Size);
                return c != 0 ? c : NaturalComparer.Compare(a.Name, b.Name);
            },
            SftpSortMode.Modified => (a, b) =>
            {
                int c = a.Modified.CompareTo(b.Modified);
                return c != 0 ? c : NaturalComparer.Compare(a.Name, b.Name);
            },
            SftpSortMode.Type => (a, b) =>
            {
                int c = NaturalComparer.Compare(Path.GetExtension(a.Name), Path.GetExtension(b.Name));
                return c != 0 ? c : NaturalComparer.Compare(a.Name, b.Name);
            },
            _ => (a, b) => NaturalComparer.Compare(a.Name, b.Name)
        };

        dirs.Sort(comp);
        files.Sort(comp);

        if (!ascending)
        {
            dirs.Reverse();
            files.Reverse();
        }

        return dirs.Concat(files).ToList();
    }

    public static List<SftpRemoteEntry> SortRemoteEntries(
        IEnumerable<SftpRemoteEntry> source, SftpSortMode mode, bool ascending)
    {
        var dirs = source.Where(e => e.IsDirectory).ToList();
        var files = source.Where(e => !e.IsDirectory).ToList();

        Comparison<SftpRemoteEntry> comp = mode switch
        {
            SftpSortMode.Size => (a, b) =>
            {
                int c = a.Size.CompareTo(b.Size);
                return c != 0 ? c : NaturalComparer.Compare(a.Name, b.Name);
            },
            SftpSortMode.Modified => (a, b) =>
            {
                int c = a.Modified.CompareTo(b.Modified);
                return c != 0 ? c : NaturalComparer.Compare(a.Name, b.Name);
            },
            SftpSortMode.Type => (a, b) =>
            {
                int c = NaturalComparer.Compare(Path.GetExtension(a.Name), Path.GetExtension(b.Name));
                return c != 0 ? c : NaturalComparer.Compare(a.Name, b.Name);
            },
            _ => (a, b) => NaturalComparer.Compare(a.Name, b.Name)
        };

        dirs.Sort(comp);
        files.Sort(comp);

        if (!ascending)
        {
            dirs.Reverse();
            files.Reverse();
        }

        return dirs.Concat(files).ToList();
    }

    public static void SyncCollection<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> source,
        Func<T, string> keySelector,
        Action<T, T>? updateExisting = null)
    {
        var sourceKeys = new HashSet<string>(source.Select(keySelector));

        // Tanti cambi (di solito un cambio di cartella): una sola sostituzione invece di migliaia
        // di inserimenti/rimozioni, che fanno sbagliare la barra di scorrimento alla DataGrid.
        if (target is BulkObservableCollection<T> bulk)
        {
            var targetKeys = new HashSet<string>(target.Select(keySelector));
            int kept = source.Count(s => targetKeys.Contains(keySelector(s)));
            int changes = (target.Count - kept) + (source.Count - kept);
            if (changes > 20)
            {
                // Le righe già mostrate restano gli stessi oggetti (aggiornati), le altre sono nuove
                var existing = target.ToDictionary(keySelector);
                var merged = source.Select(s =>
                {
                    if (!existing.TryGetValue(keySelector(s), out var old)) return s;
                    updateExisting?.Invoke(old, s);
                    return old;
                }).ToList();
                // Cartella diversa (quasi nulla in comune): si riparte dall'alto
                bulk.ReplaceAll(merged, scrollToTop: kept * 2 < source.Count);
                return;
            }
        }

        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!sourceKeys.Contains(keySelector(target[i])))
                target.RemoveAt(i);
        }

        for (int i = 0; i < source.Count; i++)
        {
            var key = keySelector(source[i]);

            int existingIndex = -1;
            for (int j = i; j < target.Count; j++)
            {
                if (keySelector(target[j]) == key) { existingIndex = j; break; }
            }

            if (existingIndex < 0)
            {
                target.Insert(i, source[i]);
            }
            else
            {
                if (updateExisting != null)
                    updateExisting(target[existingIndex], source[i]);

                if (existingIndex != i)
                    target.Move(existingIndex, i);
            }
        }
    }

    public static void UpdateRemoteEntryFromSource(SftpRemoteEntry target, SftpRemoteEntry source)
    {
        target.Size = source.Size;
        target.Modified = source.Modified;
        target.IsDirectory = source.IsDirectory;
        target.HasZeroByteIssue = source.HasZeroByteIssue;
        if (!string.IsNullOrEmpty(source.ContentsInfo)) target.ContentsInfo = source.ContentsInfo;
        target.CopyChecksFrom(source);
    }

    public static void UpdateLocalEntryFromSource(LocalFileEntry target, LocalFileEntry source)
    {
        target.Size = source.Size;
        target.Modified = source.Modified;
        target.IsDirectory = source.IsDirectory;
        target.HasZeroByteIssue = source.HasZeroByteIssue;
        target.CopyChecksFrom(source);
    }
}