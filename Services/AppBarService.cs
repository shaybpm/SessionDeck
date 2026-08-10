using System.Windows.Interop;
using SessionDeck.Interop;
using SessionDeck.Models;

namespace SessionDeck.Services;

/// <summary>
/// Reserved Zone via the AppBar API: the main window docks to a monitor edge
/// and the OS shrinks the work area — maximized/snapped windows stay out, the mouse moves freely.
/// </summary>
public sealed class AppBarService
{
    private IntPtr _hwnd;
    private HwndSource? _source;
    private uint _callbackMsg;
    private bool _registered;
    private ZoneMode _mode = ZoneMode.Off;
    private MonitorEntry? _monitor;
    private RECT _savedBounds;
    private bool _hasSavedBounds;
    private bool _selfPositioning;
    private double _customFraction = 1.0 / 3;
    private RECT _appliedRect;
    private bool _hasAppliedRect;

    /// <summary>
    /// Pixels of work area the zone must always leave on its monitor.
    /// A reservation that takes the monitor whole is accepted by the shell and then spins its
    /// own work-area bookkeeping forever: measured 08-08-2026 with Zone=Full on a 1080x1920
    /// display, explorer.exe sat at 100-430% of a core on up to 1,004,675 soft page faults per
    /// second for as long as the app ran, and went quiet within a second of it exiting. The
    /// cliff is exactly at zero - the identical test leaving one pixel over is clean - so this
    /// only has to be non-zero. 16px is invisible in the zone and not sitting on the edge.
    /// Agenda item 4.13.18.
    /// </summary>
    private const int MinFreeWorkAreaPx = 16;

    private static bool SameRect(RECT a, RECT b)
        => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public void Attach(HwndSource source)
    {
        _source = source;
        _hwnd = source.Handle;
        _callbackMsg = NativeMethods.RegisterWindowMessage("SessionDeck_AppBarCallback");
        source.AddHook(WndProc);
    }

    public void Apply(ZoneMode mode, MonitorEntry monitor, double customFraction = 1.0 / 3)
    {
        if (_hwnd == IntPtr.Zero) return;
        _customFraction = Math.Clamp(customFraction, 0.05, 1.0);

        if (mode == ZoneMode.Off)
        {
            Remove();
            return;
        }

        // Remember where the window was before the first zone, so un-zoning puts it back.
        // _mode is still the PREVIOUS mode here, so this fires on Off → zoned and nowhere else.
        if (_mode == ZoneMode.Off) SaveWindowBounds();

        // A mode that reserves needs the appbar registration; one that does not must give it
        // back, or switching Half → Full would leave the old strip reserved forever.
        if (ModeNames.ReservesWorkArea(mode))
        {
            if (!_registered)
            {
                var abdNew = NewData();
                abdNew.uCallbackMessage = _callbackMsg;
                NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref abdNew);
                _registered = true;
            }
        }
        else Unregister();

        _mode = mode;
        _monitor = monitor;
        SetPosition();
    }

    /// <summary>Hand the work-area reservation back, staying zoned. The window keeps its place
    /// and its move/resize lock; only the shell's bookkeeping is released.</summary>
    private void Unregister()
    {
        if (!_registered) return;
        var abd = NewData();
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref abd);
        _registered = false;
        _hasAppliedRect = false;   // a fresh registration must post its reservation again
    }

    public void Remove()
    {
        bool wasZoned = _mode != ZoneMode.Off;
        _mode = ZoneMode.Off;
        Unregister();
        if (!wasZoned) return;
        if (_hasSavedBounds)
        {
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero,
                _savedBounds.Left, _savedBounds.Top, _savedBounds.Width, _savedBounds.Height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void SetPosition()
    {
        if (_monitor is null) return;
        bool reserves = ModeNames.ReservesWorkArea(_mode);
        // A reserving mode lays out on the monitor and lets ABM_QUERYPOS trim it for the
        // taskbar and any other appbar. A non-reserving one never gets that answer, so it
        // lays out on the work area itself — which is what keeps Full off the taskbar.
        RECT mon = reserves ? _monitor.Bounds : CurrentWorkArea();
        bool rightEdge = _mode is ZoneMode.HalfRight or ZoneMode.QuarterRight or ZoneMode.CustomRight;
        uint edge = rightEdge ? NativeMethods.ABE_RIGHT : NativeMethods.ABE_LEFT;
        int width = _mode switch
        {
            ZoneMode.Full => mon.Width,
            ZoneMode.QuarterLeft or ZoneMode.QuarterRight => mon.Width / 4,
            ZoneMode.CustomLeft or ZoneMode.CustomRight =>
                (int)Math.Round(mon.Width * _customFraction),
            _ => mon.Width / 2,
        };

        // Never reserve a zone narrower than the window's MinWidth: below it the
        // toolbar (incl. the zone combo itself) gets clipped and the user cannot
        // un-zone from the UI (bug 2026-07-22 — 13% custom zone buried the controls).
        // And never wider than the monitor less MinFreeWorkAreaPx. See that constant for
        // what a zero-work-area reservation does to the shell. A mode that reserves nothing
        // is not bound by either: Full may take the whole monitor because it leaves the work
        // area alone (see ModeNames.ReservesWorkArea).
        if (reserves)
        {
            int maxWidth = Math.Max(1, mon.Width - MinFreeWorkAreaPx);
            width = Math.Clamp(width, Math.Min(MinZoneWidthPx(), maxWidth), maxWidth);
        }

        var abd = NewData();
        abd.uEdge = edge;
        abd.rc = new RECT { Left = mon.Left, Top = mon.Top, Right = mon.Right, Bottom = mon.Bottom };
        if (rightEdge) abd.rc.Left = mon.Right - width;
        else abd.rc.Right = mon.Left + width;

        if (reserves)
        {
            NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref abd);
            // QUERYPOS may trim for the taskbar/other appbars; re-assert our width from the granted edge.
            if (edge == NativeMethods.ABE_LEFT) abd.rc.Right = Math.Min(abd.rc.Left + width, mon.Right);
            else abd.rc.Left = Math.Max(abd.rc.Right - width, mon.Left);

            // Re-announce the reservation only when it actually moved. Every ABM_SETPOS makes
            // the shell recompute and answer with ABN_POSCHANGED, which lands in WndProc and
            // calls this method straight back. Agenda item 4.13.18.
            if (!_hasAppliedRect || !SameRect(_appliedRect, abd.rc))
            {
                _appliedRect = abd.rc;
                _hasAppliedRect = true;
                NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref abd);
            }
        }

        // Win10/11 windows carry invisible resize borders: the visible (DWM) frame is
        // inset a few px from the window rect on the left/right/bottom, so placing the
        // window rect exactly on the zone leaves visible gaps. Inflate by the inset so
        // the VISIBLE frame fills the zone — the same compensation the OS applies to
        // maximized windows. The appbar reservation itself stays abd.rc, so neighbors
        // still align to the zone edge and only the transparent border overlaps them.
        RECT inset = GetInvisibleFrameInset();
        int x = abd.rc.Left - inset.Left;
        int y = abd.rc.Top - inset.Top;
        int cx = abd.rc.Width + inset.Left + inset.Right;
        int cy = abd.rc.Height + inset.Top + inset.Bottom;

        // Move it only when it is not already exactly there. Repositioning a registered appbar
        // window is itself enough to make the shell recompute and notify us again, measured at
        // 250 callbacks per second on 08-08-2026, with no ABM_SETPOS involved at all. The
        // comparison is against where the window actually IS, not against what we last asked
        // for: anything else that moves or resizes it must still get snapped back, which is the
        // entire point of the zone. Skipping the snap outright regressed exactly that.
        if (NativeMethods.GetWindowRect(_hwnd, out RECT cur) &&
            cur.Left == x && cur.Top == y && cur.Width == cx && cur.Height == cy) return;

        _selfPositioning = true;
        try
        {
            NativeMethods.SetWindowPos(_hwnd, IntPtr.Zero, x, y, cx, cy,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
        finally { _selfPositioning = false; }
    }

    /// <summary>The zone monitor's work area, read live rather than from the snapshot handed to
    /// Apply: that snapshot may have been taken while our own reservation was still shrinking
    /// the very area we are about to lay out on. Called only on the non-reserving path, and
    /// only after ABM_REMOVE has returned, so the shell has already given the space back.</summary>
    private RECT CurrentWorkArea()
    {
        if (_monitor is null) return default;
        var live = MonitorService.GetMonitors().FirstOrDefault(m => m.Device == _monitor.Device);
        return live?.WorkArea ?? _monitor.WorkArea;
    }

    /// <summary>The window's MinWidth (DIP → device px at its current DPI); 50 when unset.</summary>
    private int MinZoneWidthPx()
    {
        if (_source?.RootVisual is System.Windows.Window w &&
            !double.IsNaN(w.MinWidth) && w.MinWidth > 0)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
            return (int)Math.Ceiling(w.MinWidth * dpi.DpiScaleX);
        }
        return 50;
    }

    /// <summary>Per-side inset of the visible (DWM extended-frame) bounds within the
    /// window rect — i.e. the invisible resize-border thickness. Zero on failure.</summary>
    private RECT GetInvisibleFrameInset()
    {
        if (NativeMethods.GetWindowRect(_hwnd, out RECT wr) &&
            NativeMethods.DwmGetWindowAttribute(_hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out RECT fr, System.Runtime.InteropServices.Marshal.SizeOf<RECT>()) == 0)
        {
            return new RECT
            {
                Left = Math.Max(0, fr.Left - wr.Left),
                Top = Math.Max(0, fr.Top - wr.Top),
                Right = Math.Max(0, wr.Right - fr.Right),
                Bottom = Math.Max(0, wr.Bottom - fr.Bottom),
            };
        }
        return default;
    }

    private void SaveWindowBounds()
    {
        if (_source?.RootVisual is System.Windows.Window w)
        {
            // Convert DIPs to device px via the window's current DPI.
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w);
            _savedBounds = new RECT
            {
                Left = (int)(w.Left * dpi.DpiScaleX),
                Top = (int)(w.Top * dpi.DpiScaleY),
                Right = (int)((w.Left + w.ActualWidth) * dpi.DpiScaleX),
                Bottom = (int)((w.Top + w.ActualHeight) * dpi.DpiScaleY),
            };
            _hasSavedBounds = true;
        }
    }

    private APPBARDATA NewData() => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(),
        hWnd = _hwnd,
    };

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_registered && msg == (int)_callbackMsg && wParam.ToInt64() == NativeMethods.ABN_POSCHANGED)
        {
            SetPosition();
            handled = true;
        }
        else if (_mode != ZoneMode.Off && msg == NativeMethods.WM_DPICHANGED)
        {
            // Landing on a monitor whose DPI differs from the one the window was last on makes
            // WPF re-apply the window's DIP size at the new scale, which on a 125% display
            // inflates a correctly-placed zone by exactly a quarter (measured 10-08-2026:
            // 1936x1029 asked for, 2418x1284 on screen, a 1920x1080 monitor overflowed on both
            // axes). Snap back once WPF has finished, hence the dispatcher hop rather than a
            // call from inside the message. A reserving zone used to be rescued from this by
            // the shell's own ABN_POSCHANGED landing right behind it; a zone that reserves
            // nothing gets no such callback and stayed inflated.
            _source?.Dispatcher.BeginInvoke(new Action(SetPosition),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else if (_mode != ZoneMode.Off && msg == NativeMethods.WM_SYSCOMMAND)
        {
            // While zoned the window is locked in place: swallow caption-drag, border-resize
            // and caption double-click maximize. Minimize/restore stay allowed.
            long cmd = wParam.ToInt64() & 0xFFF0;
            if (cmd is NativeMethods.SC_MOVE or NativeMethods.SC_SIZE or NativeMethods.SC_MAXIMIZE)
                handled = true;
        }
        else if (_mode != ZoneMode.Off && !_selfPositioning && msg == NativeMethods.WM_WINDOWPOSCHANGING)
        {
            // Hard lock against programmatic moves (Win+Shift+Arrow, snap, etc.) — only our own
            // SetPosition (guarded by _selfPositioning) may reposition the window.
            // Minimize (x/y = -32000) and restore-from-minimized are left alone.
            if (!NativeMethods.IsIconic(hwnd))
            {
                var wp = System.Runtime.InteropServices.Marshal.PtrToStructure<WINDOWPOS>(lParam);
                if (wp.x != -32000 || wp.y != -32000)
                {
                    wp.flags |= NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE;
                    System.Runtime.InteropServices.Marshal.StructureToPtr(wp, lParam, false);
                }
            }
        }
        return IntPtr.Zero;
    }
}
