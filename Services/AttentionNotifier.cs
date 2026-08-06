using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using SessionDeck.Interop;

namespace SessionDeck.Services;

/// <summary>
/// Pushes "needs attention" out of the window and into the OS (feature 2026-07-20).
///
/// A blinking card border only works while the deck is visible. When the user has turned
/// off both the 📌 pin and the reserved zone, the deck is an ordinary window that anything
/// can cover — so the caller escalates to a taskbar overlay badge, a one-shot taskbar
/// flash, and a balloon. This class owns those three OS resources and nothing else; the
/// decision of *when* to escalate lives in MainWindow.
///
/// The tray icon exists only while something needs attention: it is added on the first
/// badge and removed on Clear(), so an idle deck leaves no icon behind. A balloon requires
/// a tray icon, which is why the two are managed together here.
/// </summary>
public sealed class AttentionNotifier : IDisposable
{
    private const uint TrayId = 1;

    private IntPtr _hwnd;
    private ITaskbarList3? _taskbar;
    private bool _trayAdded;
    private bool _balloonShown;
    private bool _flashing;
    private IntPtr _appIcon;        // tray icon; extracted from our own exe
    private IntPtr _badgeIcon;      // current overlay; owned and destroyed on replace
    private Color? _badgeColor;

    public void Attach(HwndSource source)
    {
        _hwnd = source.Handle;
        source.AddHook(WndProc);
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
        }
        catch (Exception)
        {
            // No shell taskbar (rare, e.g. a stripped session) — badge is simply skipped.
            // Catching COMException alone was not enough: the CLR maps well-known HRESULTs
            // to their own exception types before COMException ever gets a chance, and
            // HrInit returning E_NOTIMPL therefore arrives as NotImplementedException. It
            // escaped, and the whole app died on startup over an optional badge (seen on
            // Windows 11 26200, 06-08-2026, on both 0.9.5 and 0.9.6). Nothing here is worth
            // a crash, so the net is as wide as the feature is optional.
            _taskbar = null;
        }
    }

    /// <summary>Raised when the user clicks the tray icon or the balloon.</summary>
    public event Action? Activated;

    /// <summary>Show a dot badge in <paramref name="color"/> — the caller resolves it from the
    /// same StatusStyles map the card borders use, so badge and card always agree. Adds the
    /// tray icon on the first attention event. No-op when the badge is already that colour.</summary>
    public void SetBadge(Color color, string tooltip)
    {
        if (_hwnd == IntPtr.Zero) return;
        EnsureTray(tooltip);
        if (_badgeColor == color) return;
        _badgeColor = color;

        IntPtr icon = CreateDotIcon(color);
        _taskbar?.SetOverlayIcon(_hwnd, icon, tooltip);
        // The shell copies the icon, so ours can go as soon as it has been handed over.
        ReplaceBadgeIcon(icon);
    }

    /// <summary>One-shot orange taskbar flash. The caller only fires this for genuinely new
    /// attention, so there is no strobe guard here — a second session going orange while the
    /// first flash is still running should restart it, not be swallowed.</summary>
    public void Flash()
    {
        if (_hwnd == IntPtr.Zero) return;
        _flashing = true;
        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = _hwnd,
            dwFlags = NativeMethods.FLASHW_TRAY,
            uCount = 3,
            dwTimeout = 0,
        };
        NativeMethods.FlashWindowEx(ref fi);
    }

    public void Balloon(string title, string text)
    {
        if (!_trayAdded) return;
        var data = NewData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfo = Truncate(text, 255);
        data.szInfoTitle = Truncate(title, 63);
        data.dwInfoFlags = _appIcon != IntPtr.Zero ? NativeMethods.NIIF_USER : NativeMethods.NIIF_INFO;
        data.hBalloonIcon = _appIcon;
        _balloonShown = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    /// <summary>
    /// Withdraw a balloon that is still on screen or parked in the notification centre.
    /// Only Clear() calls this, i.e. only once *nothing* needs attention any more: the
    /// reason for a balloon can disappear without the deck being touched at all — answering
    /// the session's tab straight in VSCode auto-acknowledges it — and a notification that
    /// outlives its cause is worse than no notification (issue 2026-07-20).
    ///
    /// An empty szInfo with NIF_INFO is the documented way to cancel; it has to happen
    /// before NIM_DELETE, because once the icon is gone there is nothing left to modify.
    /// </summary>
    private void RetractBalloon()
    {
        if (!_balloonShown || !_trayAdded) return;
        _balloonShown = false;
        var data = NewData();
        data.uFlags = NativeMethods.NIF_INFO;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
    }

    /// <summary>Drop every OS-level signal: badge, flash and tray icon.</summary>
    public void Clear()
    {
        if (_hwnd == IntPtr.Zero) return;

        if (_badgeColor != null)
        {
            _badgeColor = null;
            try { _taskbar?.SetOverlayIcon(_hwnd, IntPtr.Zero, null); } catch (COMException) { }
            ReplaceBadgeIcon(IntPtr.Zero);
        }

        if (_flashing)
        {
            _flashing = false;
            var fi = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = _hwnd,
                dwFlags = NativeMethods.FLASHW_STOP,
            };
            NativeMethods.FlashWindowEx(ref fi);
        }

        if (_trayAdded)
        {
            RetractBalloon();
            var data = NewData();
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _trayAdded = false;
        }
    }

    public void Dispose()
    {
        Clear();
        if (_appIcon != IntPtr.Zero) { NativeMethods.DestroyIcon(_appIcon); _appIcon = IntPtr.Zero; }
        if (_taskbar != null) { Marshal.ReleaseComObject(_taskbar); _taskbar = null; }
    }

    // ---- tray plumbing ----

    private void EnsureTray(string tooltip)
    {
        if (_trayAdded) return;
        // A blank hIcon still registers, but leaves an empty slot in the tray — fall back to
        // a neutral dot if the exe carries no icon.
        if (_appIcon == IntPtr.Zero)
            _appIcon = LoadAppIcon() is var i && i != IntPtr.Zero ? i : CreateDotIcon(Colors.Gray);

        var data = NewData();
        data.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
        data.uCallbackMessage = NativeMethods.WM_TRAYCALLBACK;
        data.hIcon = _appIcon;
        data.szTip = Truncate(tooltip, 127);
        _trayAdded = NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
    }

    private NOTIFYICONDATA NewData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = TrayId,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_TRAYCALLBACK) return IntPtr.Zero;
        int mouse = (int)(lParam.ToInt64() & 0xFFFF);
        if (mouse is NativeMethods.WM_LBUTTONUP or NativeMethods.NIN_BALLOONUSERCLICK)
        {
            handled = true;
            Activated?.Invoke();
        }
        return IntPtr.Zero;
    }

    private static IntPtr LoadAppIcon()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return IntPtr.Zero;
        var small = new IntPtr[1];
        return NativeMethods.ExtractIconEx(exe, 0, null, small, 1) > 0 ? small[0] : IntPtr.Zero;
    }

    private void ReplaceBadgeIcon(IntPtr next)
    {
        if (_badgeIcon != IntPtr.Zero) NativeMethods.DestroyIcon(_badgeIcon);
        _badgeIcon = next;
    }

    // ---- badge rendering ----

    /// <summary>
    /// A 16×16 filled dot with a dark rim, built as a raw bitmap pair rather than an asset —
    /// the colour comes from config and cannot be a shipped .ico. Alpha is deliberately
    /// hard-edged (0 or 255) so the result does not depend on whether the shell treats the
    /// colour bitmap as premultiplied.
    /// </summary>
    private static IntPtr CreateDotIcon(Color color)
    {
        const int size = 16;
        const double radius = 7.0;
        const double rim = 5.6;
        double centre = (size - 1) / 2.0;

        var bgra = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - centre, dy = y - centre;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > radius) continue;                       // outside: left fully transparent
                bool inner = dist <= rim;
                int i = (y * size + x) * 4;
                bgra[i + 0] = inner ? color.B : (byte)0x20;
                bgra[i + 1] = inner ? color.G : (byte)0x20;
                bgra[i + 2] = inner ? color.R : (byte)0x20;
                bgra[i + 3] = 0xFF;
            }
        }

        // 1bpp AND mask, all zeros: "take the colour bitmap everywhere" — transparency is
        // carried by the alpha channel above. Rows are word-aligned, so 2 bytes per row.
        var mask = new byte[size / 8 * size];

        IntPtr hColor = NativeMethods.CreateBitmap(size, size, 1, 32, bgra);
        IntPtr hMask = NativeMethods.CreateBitmap(size, size, 1, 1, mask);
        try
        {
            var info = new ICONINFO { fIcon = true, hbmColor = hColor, hbmMask = hMask };
            return NativeMethods.CreateIconIndirect(ref info);
        }
        finally
        {
            NativeMethods.DeleteObject(hColor);
            NativeMethods.DeleteObject(hMask);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}
