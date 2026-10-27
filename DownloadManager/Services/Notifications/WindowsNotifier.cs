using System.Runtime.InteropServices;

namespace DownloadManager.Services.Notifications;

/// <summary>
/// Notifiche di Windows tramite l'area di notifica: un'icona temporanea con un fumetto, che
/// Windows 10/11 mostra come una normale notifica (e la lascia nel Centro notifiche). Non
/// richiede pacchetti né registrazioni dell'app. L'icona sparisce da sola dopo qualche secondo.
/// </summary>
public sealed class WindowsNotifier : IDisposable
{
    public enum Kind { Info = 1, Warning = 2, Error = 3 }

    private readonly Func<IntPtr> _hwnd;
    private IntPtr _icon;
    private bool _added;
    private CancellationTokenSource? _hideCts;
    private const int IconId = 0x4444;

    public WindowsNotifier(Func<IntPtr> hwnd) => _hwnd = hwnd;

    public void Show(string title, string text, Kind kind = Kind.Info)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var hwnd = _hwnd();
            if (hwnd == IntPtr.Zero) return;
            if (_icon == IntPtr.Zero && Environment.ProcessPath is { } exe)
            {
                ExtractIconEx(exe, 0, out _icon, out var small, 1);   // icona grande: nitida nella notifica
                if (small != IntPtr.Zero) DestroyIcon(small);
            }

            var data = NewData(hwnd);
            data.uFlags = NIF_ICON | NIF_TIP;
            data.hIcon = _icon;
            data.szTip = "DDL Download Manager";
            if (!_added) _added = Shell_NotifyIcon(NIM_ADD, ref data);

            data.uFlags = NIF_INFO | NIF_ICON | NIF_TIP;
            data.szInfoTitle = Trim(title, 63);
            data.szInfo = Trim(text, 255);
            data.dwInfoFlags = _icon != IntPtr.Zero ? NIIF_USER | NIIF_LARGE_ICON : (int)kind;
            data.hBalloonIcon = _icon;
            Shell_NotifyIcon(NIM_MODIFY, ref data);

            // L'icona serve solo a far partire la notifica: dopo un po' si toglie
            _hideCts?.Cancel();
            var cts = _hideCts = new CancellationTokenSource();
            _ = Task.Delay(TimeSpan.FromSeconds(12), cts.Token).ContinueWith(t => { if (!t.IsCanceled) Remove(); }, TaskScheduler.Default);
        }
        catch { /* le notifiche non devono mai far fallire nulla */ }
    }

    private void Remove()
    {
        if (!_added) return;
        try
        {
            var data = NewData(_hwnd());
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        catch { }
        _added = false;
    }

    public void Dispose()
    {
        _hideCts?.Cancel();
        Remove();
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static NOTIFYICONDATA NewData(IntPtr hwnd) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = hwnd,
        uID = IconId,
        szTip = "", szInfo = "", szInfoTitle = ""
    };

    // ---------------- Win32 ----------------
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    private const int NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10;
    private const int NIIF_USER = 0x4, NIIF_LARGE_ICON = 0x20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersionOrTimeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
