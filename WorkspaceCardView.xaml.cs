using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SessionDeck.Interop;
using SessionDeck.ViewModels;

namespace SessionDeck;

/// <summary>
/// One workspace card: chrome (Peacock-colored border, header, session cards) drawn by WPF;
/// the live preview of the bound VSCode window is drawn by the DWM compositor into
/// ThumbArea's client rect (zero-CPU, no injection).
/// </summary>
public partial class WorkspaceCardView : UserControl
{
    private IntPtr _thumb;
    private IntPtr _thumbSource;
    private RECT _lastDest;
    private RECT _lastSrc;
    private bool _lastVisible;
    private ScrollViewer? _scroll;
    private WorkspaceViewModel? _vm;

    private WorkspaceViewModel? Vm => DataContext as WorkspaceViewModel;
    private MainWindow? Owner => Window.GetWindow(this) as MainWindow;

    public WorkspaceCardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => { LayoutUpdated += OnLayoutUpdated; RefreshThumbnail(); SyncExpandGlyph(); };
        Unloaded += (_, _) => { LayoutUpdated -= OnLayoutUpdated; UnregisterThumbnail(); _scroll = null; };
        // Collapsing the card (search filter / hide) doesn't unload it — without this the
        // DWM thumbnail keeps compositing at its old rect over the deck (bug 2026-07-19).
        IsVisibleChanged += (_, _) => RefreshThumbnail();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = Vm;
        if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        RefreshThumbnail();
        SyncExpandGlyph();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WorkspaceViewModel.Hwnd) or nameof(WorkspaceViewModel.State))
            RefreshThumbnail();
        else if (e.PropertyName is nameof(WorkspaceViewModel.Expanded))
            SyncExpandGlyph();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => RefreshThumbnail();

    // ---- DWM thumbnail (same approach as stage A/B tiles) ----

    private void RefreshThumbnail()
    {
        var vm = Vm;
        if (!IsLoaded || !IsVisible || vm == null || vm.State != BindState.Connected ||
            vm.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(vm.Hwnd))
        {
            UnregisterThumbnail();
            return;
        }

        if (PresentationSource.FromVisual(this) is not HwndSource source)
            return;

        if (_thumb == IntPtr.Zero || _thumbSource != vm.Hwnd)
        {
            UnregisterThumbnail();
            if (NativeMethods.DwmRegisterThumbnail(source.Handle, vm.Hwnd, out _thumb) != 0)
            {
                _thumb = IntPtr.Zero;
                return;
            }
            _thumbSource = vm.Hwnd;
        }

        RECT dest = ComputeDestRect(source.Handle, out SIZE srcSize);

        // The DWM composites the thumbnail over the whole window surface, ignoring WPF
        // clipping — a scrolled card would paint over the toolbar/status bar (bug
        // 2026-07-21). Clamp the destination to the hosting ScrollViewer's viewport and
        // crop the source proportionally so the preview is cut, not squeezed.
        RECT clip = ComputeViewportRect(source.Handle);
        var shown = new RECT
        {
            Left = Math.Max(dest.Left, clip.Left),
            Top = Math.Max(dest.Top, clip.Top),
            Right = Math.Min(dest.Right, clip.Right),
            Bottom = Math.Min(dest.Bottom, clip.Bottom),
        };
        bool visible = shown.Width > 0 && shown.Height > 0;

        RECT src = default;
        bool hasSrc = visible && srcSize.Cx > 0 && srcSize.Cy > 0 && dest.Width > 0 && dest.Height > 0;
        if (hasSrc)
        {
            src.Left = (shown.Left - dest.Left) * srcSize.Cx / dest.Width;
            src.Top = (shown.Top - dest.Top) * srcSize.Cy / dest.Height;
            src.Right = srcSize.Cx - (dest.Right - shown.Right) * srcSize.Cx / dest.Width;
            src.Bottom = srcSize.Cy - (dest.Bottom - shown.Bottom) * srcSize.Cy / dest.Height;
        }

        if (visible == _lastVisible && SameRect(shown, _lastDest) && SameRect(src, _lastSrc))
            return;
        _lastDest = shown;
        _lastSrc = src;
        _lastVisible = visible;

        // Hiding must not ride along with a degenerate rect: once the card is fully
        // scrolled out, `shown` is inverted (Bottom < Top) and DWM rejects the WHOLE
        // update — fVisible=false included — freezing the thumbnail at its last rect
        // over whatever scrolled into its place (sticky-preview bug 2026-07-22).
        var props = visible
            ? new DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = NativeMethods.DWM_TNP_RECTDESTINATION | NativeMethods.DWM_TNP_VISIBLE | NativeMethods.DWM_TNP_OPACITY
                          | (hasSrc ? NativeMethods.DWM_TNP_RECTSOURCE : 0),
                rcDestination = shown,
                rcSource = src,
                fVisible = true,
                opacity = 255,
            }
            : new DWM_THUMBNAIL_PROPERTIES { dwFlags = NativeMethods.DWM_TNP_VISIBLE, fVisible = false };
        NativeMethods.DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    /// <summary>The hosting ScrollViewer's client area in the main window's client
    /// coordinates (device px) — the region a thumbnail is allowed to occupy.</summary>
    private RECT ComputeViewportRect(IntPtr mainHwnd)
    {
        _scroll ??= FindAncestorScroll(this);
        if (_scroll == null) // not inside a ScrollViewer — no clipping needed
            return new RECT { Left = int.MinValue / 2, Top = int.MinValue / 2, Right = int.MaxValue / 2, Bottom = int.MaxValue / 2 };

        Point tl = _scroll.PointToScreen(new Point(0, 0));
        Point br = _scroll.PointToScreen(new Point(_scroll.ActualWidth, _scroll.ActualHeight));
        var p1 = new POINT { X = (int)tl.X, Y = (int)tl.Y };
        var p2 = new POINT { X = (int)br.X, Y = (int)br.Y };
        NativeMethods.ScreenToClient(mainHwnd, ref p1);
        NativeMethods.ScreenToClient(mainHwnd, ref p2);
        return new RECT { Left = p1.X, Top = p1.Y, Right = p2.X, Bottom = p2.Y };
    }

    private static ScrollViewer? FindAncestorScroll(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ScrollViewer s) return s;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private static bool SameRect(in RECT a, in RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>ThumbArea rect in the main window's client coordinates (device px), letterboxed
    /// to the source window's aspect ratio.</summary>
    private RECT ComputeDestRect(IntPtr mainHwnd, out SIZE srcSize)
    {
        Point tl = ThumbArea.PointToScreen(new Point(0, 0));
        Point br = ThumbArea.PointToScreen(new Point(ThumbArea.ActualWidth, ThumbArea.ActualHeight));

        var p1 = new POINT { X = (int)tl.X, Y = (int)tl.Y };
        var p2 = new POINT { X = (int)br.X, Y = (int)br.Y };
        NativeMethods.ScreenToClient(mainHwnd, ref p1);
        NativeMethods.ScreenToClient(mainHwnd, ref p2);

        srcSize = default;
        int dw = p2.X - p1.X, dh = p2.Y - p1.Y;
        if (dw > 0 && dh > 0 &&
            NativeMethods.DwmQueryThumbnailSourceSize(_thumb, out SIZE src) == 0 && src.Cx > 0 && src.Cy > 0)
        {
            srcSize = src;
            double scale = Math.Min((double)dw / src.Cx, (double)dh / src.Cy);
            int w = (int)(src.Cx * scale), h = (int)(src.Cy * scale);
            p1.X += (dw - w) / 2;
            p1.Y += (dh - h) / 2;
            p2.X = p1.X + w;
            p2.Y = p1.Y + h;
        }
        return new RECT { Left = p1.X, Top = p1.Y, Right = p2.X, Bottom = p2.Y };
    }

    private void UnregisterThumbnail()
    {
        if (_thumb != IntPtr.Zero)
        {
            NativeMethods.DwmUnregisterThumbnail(_thumb);
            _thumb = IntPtr.Zero;
            _thumbSource = IntPtr.Zero;
            _lastDest = default;
            _lastSrc = default;
            _lastVisible = false;
        }
    }

    // ---- interactions ----

    private void Card_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestorButton(d) != null) return;
        if (Vm != null) Owner?.FocusWorkspace(Vm);
    }

    private void Session_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionViewModel session } && Vm != null)
        {
            Owner?.HandleSessionClick(Vm, session);
            e.Handled = true;
        }
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Owner?.EditWorkspace(Vm);
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Owner?.PinWorkspace(Vm);
    }

    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.Expanded = !vm.Expanded;
        if (vm.Expanded) Owner?.DiscoverHistoricalSessions(vm);
    }

    private void Tasks_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.TasksExpanded = !vm.TasksExpanded;
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { Path.Length: > 0 } vm) return;
        try { Clipboard.SetText(vm.Path); }
        catch (ExternalException) { } // clipboard busy (locked by another process) — ignore
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Owner?.ToggleHideWorkspace(Vm);
    }

    /// <summary>The modifier held here picks the VSCode instance on a card that has session
    /// groups (Shay's three .claude accounts) and means nothing on any other card.</summary>
    private void NewSession_Click(object sender, RoutedEventArgs e)
    {
        if (Vm == null || Owner == null) return;
        Owner.NewSessionInVscode(Vm, null, Owner.GroupForModifiers(Vm));
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Owner?.CloseWorkspaceWindow(Vm);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Vm != null) Owner?.RemoveWorkspace(Vm);
    }

    /// <summary>The ⋯ actions menu (feedback 2026-07-19): sync dynamic items, then open.</summary>
    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        HideMenuItem.Header = vm.Hidden ? "Show on the deck again" : "Hide";
        CopyPathMenuItem.IsEnabled = vm.Path.Length > 0; // drag-in adds have no path yet (decision 21)
        CloseWindowMenuItem.IsEnabled = vm.State == BindState.Connected;
        MenuButton.ContextMenu.PlacementTarget = MenuButton;
        MenuButton.ContextMenu.IsOpen = true;
    }

    private void SyncExpandGlyph()
    {
        if (Vm is { } vm)
            ExpandButton.Content = vm.Expanded ? "▲" : "▼";
    }

    private static Button? FindAncestorButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Button b) return b;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
