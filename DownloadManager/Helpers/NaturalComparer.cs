namespace DownloadManager.Helpers;

/// <summary>
/// Confronto "natural": 2 &lt; 10 (non "10" &lt; "2" come in string.Compare).
/// Case-insensitive sul testo, ordina per valore sui blocchi numerici.
/// </summary>
public static class NaturalComparer
{
    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a == null) return -1;
        if (b == null) return 1;

        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i];
            char cb = b[j];

            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                int startA = i, startB = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;

                var numA = a.Substring(startA, i - startA);
                var numB = b.Substring(startB, j - startB);

                var trimmedA = numA.TrimStart('0');
                var trimmedB = numB.TrimStart('0');
                if (trimmedA.Length == 0) trimmedA = "0";
                if (trimmedB.Length == 0) trimmedB = "0";

                if (trimmedA.Length != trimmedB.Length)
                    return trimmedA.Length.CompareTo(trimmedB.Length);

                int cmp = string.CompareOrdinal(trimmedA, trimmedB);
                if (cmp != 0) return cmp;
            }
            else
            {
                int cmp = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
                if (cmp != 0) return cmp;
                i++;
                j++;
            }
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }
}