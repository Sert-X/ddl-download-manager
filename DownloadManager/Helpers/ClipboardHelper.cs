using System.Runtime.InteropServices;

namespace DownloadManager.Helpers;

/// <summary>
/// Copia testo negli appunti di Windows con le API native (CF_UNICODETEXT).
/// Prima si usava clip.exe, che però lasciava un carattere invisibile (BOM) in testa
/// al testo copiato: incollando un link, il link risultava "sporco".
/// </summary>
public static class ClipboardHelper
{
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public static Task SetTextAsync(string text) => Task.Run(() => SetText(text));

    private static void SetText(string text)
    {
        // Gli appunti possono essere momentaneamente occupati da un altro programma
        for (int attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            if (attempt >= 10) throw new InvalidOperationException("Appunti occupati da un altro programma.");
            Thread.Sleep(50);
        }

        try
        {
            EmptyClipboard();

            var bytes = (text.Length + 1) * 2;   // UTF-16 + terminatore
            var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
            if (handle == IntPtr.Zero) throw new OutOfMemoryException();

            var ptr = GlobalLock(handle);
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, ptr, text.Length);
                Marshal.WriteInt16(ptr, text.Length * 2, 0);
            }
            finally { GlobalUnlock(handle); }

            // Da qui la memoria appartiene agli appunti (non va liberata)
            if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
            {
                GlobalFree(handle);
                throw new InvalidOperationException("Impossibile scrivere negli appunti.");
            }
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr hMem);
}
