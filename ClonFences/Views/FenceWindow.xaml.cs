using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ClonFences.Models;
using ClonFences.Native;
using ClonFences.Services;
using static ClonFences.Native.NativeMethods;

namespace ClonFences.Views;

public partial class FenceWindow : Window
{
    public const double HeaderHeight = 30;
    internal const string DragFormat = "ClonFences.SourceFence";
    internal const string PathsFormat = "ClonFences.Paths";

    private enum DropKind { None, Reorder, Reference, FileOp }

    private sealed record DropPlan(DropKind Kind, DragDropEffects Effect, string[] Paths,
        string? TargetDir = null, bool Copy = false, string? InsertBefore = null)
    {
        public static readonly DropPlan Nothing = new(DropKind.None, DragDropEffects.None, Array.Empty<string>());
    }

    private readonly FenceManager _mgr;
    private readonly ObservableCollection<FenceItem> _items = new();
    private readonly DispatcherTimer _portalRefresh;
    private IntPtr _hwnd;
    private string? _currentPath;
    private FileSystemWatcher? _portalWatcher;
    private bool _peeking;
    private bool _renaming;
    private int _fadeVersion;

    private Point _dragStart;
    private FenceItem? _pressedItem;
    private FenceItem? _pendingSingleSelect;

    internal FenceWindow(FenceModel model, FenceManager manager)
    {
        Model = model;
        _mgr = manager;
        InitializeComponent();

        ItemsList.ItemsSource = _items;
        _portalRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _portalRefresh.Tick += (_, _) => { _portalRefresh.Stop(); Reload(); };
        _currentPath = model.FolderPath;

        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => ApplyBounds();
        MouseEnter += (_, _) => BeginPeek();
        MouseLeave += (_, _) => EndPeekIfOutside();

        ApplyAppearance(reloadIcons: false);
        UpdateHeader();
        SetupPortalWatcher();
        Reload();
        ApplyRollupVisuals();
    }

    internal FenceModel Model { get; }
    internal bool ClosingIntentionally { get; set; }
    internal IntPtr Handle => _hwnd;
    internal bool IsPortal => Model.IsPortal;

    private bool IsLocked => Model.Locked || _mgr.Settings.LockAll;
    private bool IsCollapsed => Model.RolledUp && !_peeking;
    private double Scale => _hwnd != IntPtr.Zero ? GetDpiForWindow(_hwnd) / 96.0 : SystemScale;
    private int HeaderPx => (int)Math.Round((HeaderHeight + 2) * Scale);
    private int IconPx => Model.IconSize ?? _mgr.Settings.DefaultIconSize;

    // ───────────────────────────── Ventana nativa ─────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
        ex = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW; // fuera de Alt+Tab y de la barra de tareas
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        int round = 2; // DWMWCP_ROUND (Windows 11)
        DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        NativeMethods.SetBlur(_hwnd, _mgr.Settings.BlurBehind);
        ApplyBounds();
    }

    internal void SetOwner(IntPtr host)
    {
        if (_hwnd != IntPtr.Zero) SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, host);
        else new WindowInteropHelper(this).Owner = host;
    }

    /// <summary>Aplica la posición/tamaño guardados (en píxeles físicos).</summary>
    internal void ApplyBounds()
    {
        if (_hwnd == IntPtr.Zero) return;
        var r = new RECT(Model.X, Model.Y, Model.X + Model.Width, Model.Y + Model.Height);
        if (!IsOnAnyMonitor(r)) // el monitor donde estaba ya no existe
        {
            var wa = GetWorkAreaAt(new POINT(0, 0));
            Model.X = wa.Left + 40;
            Model.Y = wa.Top + 40;
        }
        // Dos pasadas: si la ventana cambia de monitor con otro DPI, la primera provoca un reescalado.
        for (int i = 0; i < 2; i++)
        {
            int h = IsCollapsed ? HeaderPx : Model.Height;
            SetWindowPos(_hwnd, IntPtr.Zero, Model.X, Model.Y, Model.Width, h, SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    /// <summary>Guarda la posición/tamaño actuales en el modelo.</summary>
    internal void ReadBounds()
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var r)) return;
        Model.X = r.Left;
        Model.Y = r.Top;
        Model.Width = r.Width;
        if (!Model.RolledUp && !_peeking) Model.Height = r.Height;
        _mgr.SaveSoon();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING:
            {
                // Mantener el fence siempre al fondo, pegado al escritorio.
                var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                wp.hwndInsertAfter = HWND_BOTTOM;
                wp.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(wp, lParam, false);
                break;
            }
            case WM_NCHITTEST:
            {
                if (IsLocked || Model.RolledUp || _peeking) break;
                int code = HitTestBorder(lParam);
                if (code != 0)
                {
                    handled = true;
                    return new IntPtr(code);
                }
                break;
            }
            case WM_MOVING:
            {
                if (!_mgr.Settings.SnapEnabled) break;
                var r = Marshal.PtrToStructure<RECT>(lParam);
                SnapMove(ref r);
                Marshal.StructureToPtr(r, lParam, false);
                handled = true;
                return new IntPtr(1);
            }
            case WM_SIZING:
            {
                var r = Marshal.PtrToStructure<RECT>(lParam);
                SnapSize(wParam.ToInt32(), ref r);
                Marshal.StructureToPtr(r, lParam, false);
                handled = true;
                return new IntPtr(1);
            }
            case WM_EXITSIZEMOVE:
                ReadBounds();
                break;
            case WM_SYSCOMMAND:
            {
                int sc = wParam.ToInt32() & 0xFFF0;
                if (sc == SC_MAXIMIZE || sc == SC_MINIMIZE)
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
            }
            case WM_INITMENUPOPUP:
            case WM_DRAWITEM:
            case WM_MEASUREITEM:
            case WM_MENUCHAR:
                if (ShellContextMenu.HandleMenuMessage(msg, wParam, lParam, out var result))
                {
                    handled = true;
                    return result;
                }
                break;
        }
        return IntPtr.Zero;
    }

    private int HitTestBorder(IntPtr lParam)
    {
        long lp = lParam.ToInt64();
        int x = unchecked((short)(lp & 0xFFFF));
        int y = unchecked((short)((lp >> 16) & 0xFFFF));
        if (!GetWindowRect(_hwnd, out var r)) return 0;
        int b = Math.Max(4, (int)(6 * Scale));
        bool left = x < r.Left + b, right = x >= r.Right - b, top = y < r.Top + b, bottom = y >= r.Bottom - b;
        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return 0;
    }

    // ───────────────────────────── Imantado ─────────────────────────────

    private void SnapCandidates(RECT r, out List<int> left, out List<int> right, out List<int> top, out List<int> bottom)
    {
        int gap = (int)(8 * Scale);
        var wa = GetWorkArea(r);
        left = new() { wa.Left };
        right = new() { wa.Right };
        top = new() { wa.Top };
        bottom = new() { wa.Bottom };
        foreach (var o in _mgr.GetOtherFenceRects(this))
        {
            left.Add(o.Left); left.Add(o.Right + gap);
            right.Add(o.Right); right.Add(o.Left - gap);
            top.Add(o.Top); top.Add(o.Bottom + gap);
            bottom.Add(o.Bottom); bottom.Add(o.Top - gap);
        }
    }

    private static int Nearest(int value, List<int> candidates, int threshold, out int best)
    {
        best = int.MaxValue;
        int delta = 0;
        foreach (var c in candidates)
        {
            int d = c - value;
            if (Math.Abs(d) < Math.Abs(best)) { best = d; delta = d; }
        }
        if (Math.Abs(best) > threshold) { best = int.MaxValue; return 0; }
        return delta;
    }

    private void SnapMove(ref RECT r)
    {
        int th = (int)(10 * Scale);
        SnapCandidates(r, out var ls, out var rs, out var ts, out var bs);
        int dl = Nearest(r.Left, ls, th, out var bl), dr = Nearest(r.Right, rs, th, out var br);
        int dx = Math.Abs(bl) <= Math.Abs(br) ? dl : dr;
        int dt = Nearest(r.Top, ts, th, out var bt), db = Nearest(r.Bottom, bs, th, out var bb);
        int dy = Math.Abs(bt) <= Math.Abs(bb) ? dt : db;
        r.Left += dx; r.Right += dx;
        r.Top += dy; r.Bottom += dy;
    }

    private void SnapSize(int edge, ref RECT r)
    {
        bool l = edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT;
        bool rt = edge is WMSZ_RIGHT or WMSZ_TOPRIGHT or WMSZ_BOTTOMRIGHT;
        bool t = edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT;
        bool b = edge is WMSZ_BOTTOM or WMSZ_BOTTOMLEFT or WMSZ_BOTTOMRIGHT;

        if (_mgr.Settings.SnapEnabled)
        {
            int th = (int)(10 * Scale);
            SnapCandidates(r, out var ls, out var rs, out var ts, out var bs);
            if (l) r.Left += Nearest(r.Left, ls, th, out _);
            if (rt) r.Right += Nearest(r.Right, rs, th, out _);
            if (t) r.Top += Nearest(r.Top, ts, th, out _);
            if (b) r.Bottom += Nearest(r.Bottom, bs, th, out _);
        }

        int minW = (int)(140 * Scale), minH = (int)((HeaderHeight + 2 + 56) * Scale);
        if (r.Width < minW) { if (l) r.Left = r.Right - minW; else r.Right = r.Left + minW; }
        if (r.Height < minH) { if (t) r.Top = r.Bottom - minH; else r.Bottom = r.Top + minH; }
    }

    // ───────────────────────────── Apariencia ─────────────────────────────

    internal void ApplyAppearance(bool reloadIcons = true)
    {
        var s = _mgr.Settings;
        var c = ColorUtil.Parse(Model.Color ?? s.DefaultColor, Color.FromRgb(0x1E, 0x1E, 0x1E));
        double op = ColorUtil.Clamp01(Model.Opacity ?? s.DefaultOpacity);
        byte alpha = (byte)Math.Clamp(Math.Round(op * 255), 1, 255);
        Frame.Background = ColorUtil.Brush(alpha, c.R, c.G, c.B);

        bool light = ColorUtil.Luminance(c) > 0.6 && op >= 0.4;
        var fg = light ? ColorUtil.Brush(255, 0x1A, 0x1A, 0x1A) : Brushes.White;
        Foreground = fg;
        ItemsList.Foreground = fg;
        TitleText.Foreground = fg;
        EmptyHint.Foreground = fg;
        PortalGlyph.Foreground = fg;
        LockGlyph.Foreground = fg;
        Resources["ShadowOpacity"] = light ? 0.0 : 0.85;
        Frame.BorderBrush = light ? ColorUtil.Brush(0x30, 0, 0, 0) : ColorUtil.Brush(0x38, 255, 255, 255);
        TitleBar.Background = light ? ColorUtil.Brush(0x18, 0, 0, 0) : ColorUtil.Brush(0x30, 0, 0, 0);

        Resources["IconSize"] = (double)IconPx;
        Resources["TileWidth"] = (double)(IconPx + 36);

        if (_hwnd != IntPtr.Zero) NativeMethods.SetBlur(_hwnd, s.BlurBehind);
        UpdateHeader();
        if (reloadIcons) RequestIcons(_items);
    }

    private void UpdateHeader()
    {
        bool nested = IsPortal && _currentPath != null && !ShellPaths.Equal(_currentPath, Model.FolderPath);
        TitleText.Text = nested
            ? $"{Model.Title}  ›  {Path.GetFileName(_currentPath!.TrimEnd('\\'))}"
            : Model.Title;
        TitleText.ToolTip = IsPortal ? _currentPath : null;
        BackButton.Visibility = nested ? Visibility.Visible : Visibility.Collapsed;
        PortalGlyph.Visibility = IsPortal && !nested ? Visibility.Visible : Visibility.Collapsed;
        LockGlyph.Visibility = IsLocked ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void RefreshLockState() => UpdateHeader();

    // ───────────────────────────── Contenido ─────────────────────────────

    internal void Reload()
    {
        var selected = new HashSet<string>(SelectedPaths(), StringComparer.OrdinalIgnoreCase);
        var existing = new Dictionary<string, FenceItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in _items) existing.TryAdd(i.Path, i);

        var list = new List<FenceItem>();
        string hint = IsPortal ? "Carpeta vacía" : "Arrastra archivos aquí";

        if (IsPortal)
        {
            if (_currentPath == null || !Directory.Exists(_currentPath))
                hint = "No se encuentra la carpeta\n" + _currentPath;
            else
                foreach (var p in ShellPaths.SafeEnumerate(_currentPath))
                    AddItem(p);
        }
        else
        {
            bool pruned = false;
            foreach (var p in Model.Items.ToList())
            {
                if (!ShellPaths.Exists(p))
                {
                    if (ShellPaths.ShouldPrune(p)) { Model.Items.Remove(p); pruned = true; }
                    continue;
                }
                AddItem(p);
            }
            if (pruned) _mgr.SaveSoon();
        }

        void AddItem(string p)
        {
            var fresh = FenceItem.TryCreate(p);
            if (fresh == null) return;
            if (existing.TryGetValue(p, out var old) && old.Modified == fresh.Modified && old.Name == fresh.Name && !old.IsVirtual)
                list.Add(old);
            else
            {
                if (old?.Icon != null) fresh.Icon = old.Icon;
                list.Add(fresh);
            }
        }

        var sorted = Sort(list).ToList();
        _items.Clear();
        foreach (var i in sorted) _items.Add(i);
        foreach (var i in sorted.Where(i => selected.Contains(i.Path))) ItemsList.SelectedItems.Add(i);

        EmptyHint.Text = hint;
        ApplyRollupVisuals();
        RequestIcons(sorted.Where(i => i.Icon == null || i.IsVirtual));
    }

    private IEnumerable<FenceItem> Sort(List<FenceItem> list)
    {
        var cmp = StringComparer.CurrentCultureIgnoreCase;
        return Model.Sort switch
        {
            SortMode.Name => list.OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, cmp),
            SortMode.Type => list.OrderByDescending(i => i.IsFolder).ThenBy(i => i.Extension, cmp).ThenBy(i => i.Name, cmp),
            SortMode.Date => list.OrderByDescending(i => i.Modified),
            SortMode.Size => list.OrderByDescending(i => i.IsFolder).ThenByDescending(i => i.Size),
            _ => IsPortal ? list.OrderByDescending(i => i.IsFolder).ThenBy(i => i.Name, cmp) : list,
        };
    }

    private void RequestIcons(IEnumerable<FenceItem> items)
    {
        int px = (int)Math.Round(IconPx * Scale);
        foreach (var item in items.ToList())
        {
            var target = item;
            IconLoader.Load(target.Path, px, Dispatcher, img => target.Icon = img, bypassCache: target.IsVirtual);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RequestIcons(_items);
    }

    private void SetupPortalWatcher()
    {
        _portalWatcher?.Dispose();
        _portalWatcher = null;
        if (!IsPortal || _currentPath == null || !Directory.Exists(_currentPath)) return;
        try
        {
            var w = new FileSystemWatcher(_currentPath)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            void Kick() => Dispatcher.BeginInvoke(() => { _portalRefresh.Stop(); _portalRefresh.Start(); });
            w.Created += (_, _) => Kick();
            w.Deleted += (_, _) => Kick();
            w.Changed += (_, _) => Kick();
            w.Renamed += (_, _) => Kick();
            w.EnableRaisingEvents = true;
            _portalWatcher = w;
        }
        catch (Exception ex) { Logger.Log(ex); }
    }

    private void Navigate(string dir)
    {
        _currentPath = dir;
        SetupPortalWatcher();
        UpdateHeader();
        _items.Clear();
        Reload();
    }

    private void NavigateUp()
    {
        if (!IsPortal || _currentPath == null || ShellPaths.Equal(_currentPath, Model.FolderPath)) return;
        var parent = Path.GetDirectoryName(_currentPath.TrimEnd('\\'));
        Navigate(parent ?? Model.FolderPath!);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => NavigateUp();

    internal void ResetPortal()
    {
        _currentPath = Model.FolderPath;
        SetupPortalWatcher();
        UpdateHeader();
        _items.Clear();
        Reload();
    }

    internal void DisposeResources()
    {
        _portalRefresh.Stop();
        _portalWatcher?.Dispose();
        _portalWatcher = null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 sobre un fence no debe cerrarlo.
        if (!ClosingIntentionally && !_mgr.IsShuttingDown) e.Cancel = true;
        base.OnClosing(e);
    }

    // ───────────────────────────── Enrollar / asomar ─────────────────────────────

    private void ApplyRollupVisuals()
    {
        bool collapsed = IsCollapsed;
        ItemsList.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        EmptyHint.Visibility = !collapsed && _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    internal void ToggleRollup()
    {
        if (_peeking)
        {
            _peeking = false;
            Model.RolledUp = false;
        }
        else
        {
            if (!Model.RolledUp) ReadBounds();
            Model.RolledUp = !Model.RolledUp;
        }
        ApplyRollupVisuals();
        SyncHeightKeepingPosition();
        _mgr.SaveSoon();
    }

    private void SyncHeightKeepingPosition()
    {
        if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var r)) return;
        int h = IsCollapsed ? HeaderPx : Model.Height;
        SetWindowPos(_hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, h, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void BeginPeek()
    {
        if (!Model.RolledUp || _peeking || _renaming || !_mgr.Settings.PeekRolledUp) return;
        _peeking = true;
        ApplyRollupVisuals();
        SyncHeightKeepingPosition();
    }

    private void EndPeekIfOutside()
    {
        if (!_peeking) return;
        GetCursorPos(out var p);
        if (_hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out var r) && r.Contains(p)) return;
        _peeking = false;
        ApplyRollupVisuals();
        SyncHeightKeepingPosition();
    }

    private void SchedulePeekCheck()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        t.Tick += (_, _) => { t.Stop(); EndPeekIfOutside(); };
        t.Start();
    }

    // ───────────────────────────── Mostrar / ocultar ─────────────────────────────

    internal void FadeIn()
    {
        int v = ++_fadeVersion;
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
        BeginAnimation(OpacityProperty, anim);
        Show();
        _ = v;
    }

    internal void FadeOut()
    {
        int v = ++_fadeVersion;
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        anim.Completed += (_, _) =>
        {
            if (v != _fadeVersion) return;
            Hide();
            BeginAnimation(OpacityProperty, null);
        };
        BeginAnimation(OpacityProperty, anim);
    }

    // ───────────────────────────── Título ─────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_renaming) return;
        if (e.ClickCount == 2)
        {
            ToggleRollup();
            e.Handled = true;
            return;
        }
        if (IsLocked) return;
        try { DragMove(); } catch (InvalidOperationException) { }
        ReadBounds();
    }

    internal void BeginRename()
    {
        _renaming = true;
        if (_peeking) { _peeking = false; }
        if (Model.RolledUp) { Model.RolledUp = false; ApplyRollupVisuals(); SyncHeightKeepingPosition(); }
        if (_hwnd != IntPtr.Zero) ForceForeground(_hwnd);
        Activate();
        TitleEditor.Text = Model.Title;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditor.Visibility = Visibility.Visible;
        TitleEditor.Focus();
        Keyboard.Focus(TitleEditor);
        TitleEditor.SelectAll();
    }

    private void EndRename(bool commit)
    {
        if (!_renaming) return;
        _renaming = false;
        if (commit)
        {
            var t = TitleEditor.Text.Trim();
            if (t.Length > 0 && t != Model.Title)
            {
                Model.Title = t;
                _mgr.SaveSoon();
            }
        }
        TitleEditor.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        UpdateHeader();
    }

    private void TitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndRename(true); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndRename(false); e.Handled = true; }
    }

    private void TitleEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndRename(true);

    // ───────────────────────────── Selección y ratón ─────────────────────────────

    private static FenceItem? ItemFromElement(object? source)
    {
        var d = source as DependencyObject;
        while (d != null)
        {
            if (d is ListBoxItem lbi) return lbi.DataContext as FenceItem;
            if (d is ScrollBar) return null;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private static bool IsOnScrollBar(object? source)
    {
        var d = source as DependencyObject;
        while (d != null)
        {
            if (d is ScrollBar) return true;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private List<FenceItem> SelectedItemsInOrder() =>
        _items.Where(i => ItemsList.SelectedItems.Contains(i)).ToList();

    private List<string> SelectedPaths() => SelectedItemsInOrder().Select(i => i.Path).ToList();

    private void SelectOnly(FenceItem item)
    {
        ItemsList.SelectedItems.Clear();
        ItemsList.SelectedItem = item;
    }

    private void ItemsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = ItemFromElement(e.OriginalSource);
        _pressedItem = item;
        _pendingSingleSelect = null;
        _dragStart = e.GetPosition(ItemsList);

        if (item == null)
        {
            if (!IsOnScrollBar(e.OriginalSource)) ItemsList.UnselectAll();
            return;
        }
        if (e.ClickCount == 2)
        {
            _pressedItem = null;
            e.Handled = true;
            OpenItems(new[] { item });
            return;
        }
        // Mantener la selección múltiple si se va a arrastrar un elemento ya seleccionado.
        if (Keyboard.Modifiers == ModifierKeys.None && ItemsList.SelectedItems.Count > 1 && ItemsList.SelectedItems.Contains(item))
        {
            _pendingSingleSelect = item;
            e.Handled = true;
        }
    }

    private void ItemsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pendingSingleSelect != null) SelectOnly(_pendingSingleSelect);
        _pendingSingleSelect = null;
        _pressedItem = null;
    }

    private void ItemsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressedItem == null) return;
        var pos = e.GetPosition(ItemsList);
        if (Math.Abs(pos.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(pos.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _pressedItem;
        _pressedItem = null;
        _pendingSingleSelect = null;
        if (!ItemsList.SelectedItems.Contains(item)) SelectOnly(item);
        StartDrag();
    }

    private void ItemsList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var item = ItemFromElement(e.OriginalSource);
        if (item == null) return; // menú del fence (Window_MouseRightButtonUp)
        e.Handled = true;
        if (!ItemsList.SelectedItems.Contains(item)) SelectOnly(item);
        ShowItemMenu(item);
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_renaming || e.Handled) return;
        e.Handled = true;
        var menu = FenceMenu.Build(this, _mgr);
        menu.Placement = PlacementMode.MousePoint;
        menu.PlacementTarget = this;
        menu.Closed += (_, _) => SchedulePeekCheck();
        menu.IsOpen = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_renaming) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var sel = SelectedItemsInOrder();
        switch (e.Key)
        {
            case Key.Enter when sel.Count > 0:
                OpenItems(sel);
                break;
            case Key.Delete when sel.Count > 0:
                DeleteItems(sel.Select(i => i.Path).ToList(), permanent: shift);
                break;
            case Key.F2 when sel.Count == 1:
                RenameItem(sel[0]);
                break;
            case Key.F2 when sel.Count == 0:
                BeginRename();
                break;
            case Key.F5:
                Reload();
                break;
            case Key.Back:
                NavigateUp();
                break;
            case Key.A when ctrl:
                ItemsList.SelectAll();
                break;
            case Key.C when ctrl && sel.Count > 0:
                CopyToClipboard(sel.Select(i => i.Path), cut: false);
                break;
            case Key.X when ctrl && sel.Count > 0:
                CopyToClipboard(sel.Select(i => i.Path), cut: true);
                break;
            case Key.V when ctrl:
                Paste();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ───────────────────────────── Acciones sobre elementos ─────────────────────────────

    internal void OpenItems(IEnumerable<FenceItem> items)
    {
        foreach (var item in items.ToList())
        {
            if (IsPortal && item.IsFolder && !item.IsVirtual)
            {
                Navigate(item.Path);
                return;
            }
            FileOps.Open(item.Path);
        }
    }

    private void ShowItemMenu(FenceItem clicked)
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) paths.Add(clicked.Path);
        GetCursorPos(out var pt);

        var extras = new List<(int, string)>();
        if (IsPortal && clicked.IsFolder && !clicked.IsVirtual) extras.Add((2, "Abrir dentro del portal"));
        if (!IsPortal) extras.Add((1, "Quitar del fence"));

        var res = ShellContextMenu.Show(_hwnd, paths, pt, extras);
        if (!res.Shown)
        {
            ShowFallbackItemMenu(clicked, paths);
            return;
        }
        if (res.ExtraId == 1) _mgr.RemoveReferences(paths);
        else if (res.ExtraId == 2) Navigate(clicked.Path);
        else if (res.Verb == "rename") RenameItem(clicked);
        else if (res.Verb != null) AfterExternalChange();
        SchedulePeekCheck();
    }

    private void ShowFallbackItemMenu(FenceItem clicked, List<string> paths)
    {
        var m = new ContextMenu();
        void Add(string header, Action a) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => a(); m.Items.Add(mi); }
        Add("Abrir", () => OpenItems(SelectedItemsInOrder()));
        if (!clicked.IsVirtual) Add("Abrir ubicación", () => FileOps.OpenInExplorer(clicked.Path));
        if (!IsPortal) Add("Quitar del fence", () => _mgr.RemoveReferences(paths));
        if (!clicked.IsVirtual) Add("Renombrar", () => RenameItem(clicked));
        Add("Eliminar", () => DeleteItems(paths, false));
        m.Placement = PlacementMode.MousePoint;
        m.PlacementTarget = this;
        m.IsOpen = true;
    }

    private void AfterExternalChange()
    {
        _mgr.ValidateSoon();
        if (IsPortal) { _portalRefresh.Stop(); _portalRefresh.Start(); }
    }

    private void RenameItem(FenceItem item)
    {
        if (item.IsVirtual) return;
        var fileName = Path.GetFileName(item.Path.TrimEnd('\\'));
        bool hideExt = ShellPaths.HidesExtension(item.Path);
        var ext = Path.GetExtension(fileName);
        var initial = hideExt ? Path.GetFileNameWithoutExtension(fileName) : fileName;
        int selLen = !hideExt && !item.IsFolder && ext.Length > 0 ? initial.Length - ext.Length : initial.Length;

        var answer = InputDialog.Ask("Renombrar", "Nuevo nombre:", initial, selLen);
        if (answer == null) return;
        var newName = hideExt ? answer.Trim() + ext : answer.Trim();
        if (newName == fileName) return;

        if (FileOps.TryRename(item.Path, newName, out var newPath, out var error))
        {
            IconLoader.Invalidate(item.Path);
            if (!IsPortal) _mgr.RenamePath(item.Path, newPath);
            else Reload();
        }
        else
        {
            MessageBox.Show(error ?? "No se pudo renombrar.", "Clon Fences", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DeleteItems(List<string> paths, bool permanent)
    {
        var virtualItems = paths.Where(ShellPaths.IsVirtual).ToList();
        if (virtualItems.Count > 0 && !IsPortal) _mgr.RemoveReferences(virtualItems);
        var files = FileOps.NonVirtual(paths).ToList();
        if (files.Count == 0) return;
        FileOps.Delete(files, _hwnd, permanent);
        IconLoader.Invalidate(ShellPaths.RecycleBin);
        AfterExternalChange();
    }

    private static void CopyToClipboard(IEnumerable<string> paths, bool cut)
    {
        var files = FileOps.NonVirtual(paths).ToArray();
        if (files.Length == 0) return;
        var data = new DataObject();
        var col = new StringCollection();
        col.AddRange(files);
        data.SetFileDropList(col);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
        try { Clipboard.SetDataObject(data, true); }
        catch (Exception ex) { Logger.Log(ex); }
    }

    private void Paste()
    {
        try
        {
            var d = Clipboard.GetDataObject();
            if (d == null || !d.GetDataPresent(DataFormats.FileDrop)) return;
            if (d.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            bool move = false;
            if (d.GetData("Preferred DropEffect") is MemoryStream ms)
            {
                var b = new byte[4];
                if (ms.Read(b, 0, 4) == 4) move = (BitConverter.ToInt32(b, 0) & 2) != 0;
            }
            var target = IsPortal ? _currentPath : DesktopShell.UserDesktop;
            if (target == null) return;
            Execute(new DropPlan(DropKind.FileOp, move ? DragDropEffects.Move : DragDropEffects.Copy, files, target, !move));
            if (move) Clipboard.Clear();
        }
        catch (Exception ex) { Logger.Log(ex); }
    }

    internal void AddFiles(IEnumerable<string> paths) => _mgr.MoveReferences(paths, this, null);

    internal void ConvertToPortal(string folder)
    {
        Model.FolderPath = folder;
        Model.Items.Clear();
        Model.Title = Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder;
        if (_mgr.Settings.DefaultFenceId == Model.Id) _mgr.Settings.DefaultFenceId = null;
        ResetPortal();
        _mgr.SaveSoon();
    }

    internal void ConvertToStandard()
    {
        Model.FolderPath = null;
        _currentPath = null;
        SetupPortalWatcher();
        _items.Clear();
        UpdateHeader();
        Reload();
        _mgr.SaveSoon();
    }

    internal string? CurrentFolder => _currentPath;

    // ───────────────────────────── Arrastrar y soltar ─────────────────────────────

    private void StartDrag()
    {
        var paths = SelectedPaths();
        if (paths.Count == 0) return;
        var files = FileOps.NonVirtual(paths).ToArray();

        var data = new DataObject();
        if (files.Length > 0) data.SetData(DataFormats.FileDrop, files);
        data.SetData(DragFormat, Model.Id);
        data.SetData(PathsFormat, string.Join("\n", paths));

        var allowed = files.Length > 0
            ? DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link
            : DragDropEffects.Move;
        DragDropEffects result;
        try { result = DragDrop.DoDragDrop(ItemsList, data, allowed); }
        catch (Exception ex) { Logger.Log(ex); return; }

        // Soltado sobre el escritorio visible: sale del fence (como en Fences).
        if (!IsPortal && result != DragDropEffects.None && DesktopShell.AreIconsVisible())
        {
            GetCursorPos(out var pt);
            if (DesktopShell.IsDesktopWindow(WindowFromPoint(pt))) _mgr.RemoveReferences(paths);
        }
        AfterExternalChange();
        SchedulePeekCheck();
    }

    private static string[] GetDropPaths(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent(PathsFormat) && data.GetData(PathsFormat) is string s)
                return s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
                return files;
        }
        catch (Exception ex) { Logger.Log(ex); }
        return Array.Empty<string>();
    }

    private static DragDropEffects ChooseEffect(DragEventArgs e, string[] paths, string targetDir)
    {
        var allowed = e.AllowedEffects;
        var keys = e.KeyStates;
        DragDropEffects want;
        if (keys.HasFlag(DragDropKeyStates.ControlKey)) want = DragDropEffects.Copy;
        else if (keys.HasFlag(DragDropKeyStates.ShiftKey)) want = DragDropEffects.Move;
        else want = ShellPaths.SameRoot(paths[0], targetDir) ? DragDropEffects.Move : DragDropEffects.Copy;

        if ((allowed & want) == 0)
            want = (allowed & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                 : (allowed & DragDropEffects.Move) != 0 ? DragDropEffects.Move
                 : DragDropEffects.None;
        return want;
    }

    private DropPlan PlanDrop(DragEventArgs e)
    {
        var paths = GetDropPaths(e.Data);
        if (paths.Length == 0) return DropPlan.Nothing;

        string? source = null;
        try { if (e.Data.GetDataPresent(DragFormat)) source = e.Data.GetData(DragFormat) as string; } catch { }

        var over = ItemFromElement(e.OriginalSource);
        bool anyVirtual = paths.Any(ShellPaths.IsVirtual);
        var real = paths.Where(p => !ShellPaths.IsVirtual(p)).ToArray();
        string? before = over?.Path;

        // Soltar sobre una carpeta del fence: mover/copiar dentro de ella.
        if (over is { IsFolder: true, IsVirtual: false } && !anyVirtual && real.Length > 0 &&
            !paths.Contains(over.Path, StringComparer.OrdinalIgnoreCase))
        {
            var eff = ChooseEffect(e, real, over.Path);
            return eff == DragDropEffects.None ? DropPlan.Nothing
                : new DropPlan(DropKind.FileOp, eff, real, over.Path, eff == DragDropEffects.Copy);
        }

        if (source == Model.Id)
            return IsPortal ? DropPlan.Nothing : new DropPlan(DropKind.Reorder, DragDropEffects.Move, paths, InsertBefore: before);

        if (!IsPortal)
        {
            if (source != null && _mgr.IsStandardFence(source))
                return new DropPlan(DropKind.Reference, DragDropEffects.Move, paths, InsertBefore: before);

            if (paths.All(DesktopShell.IsOnDesktop))
            {
                var eff = (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link
                        : (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy
                        : DragDropEffects.Move;
                return new DropPlan(DropKind.Reference, eff, paths, InsertBefore: before);
            }
            if (anyVirtual) return DropPlan.Nothing;

            var fx = ChooseEffect(e, real, DesktopShell.UserDesktop);
            return fx == DragDropEffects.None ? DropPlan.Nothing
                : new DropPlan(DropKind.FileOp, fx, real, DesktopShell.UserDesktop, fx == DragDropEffects.Copy, before);
        }

        if (anyVirtual || _currentPath == null) return DropPlan.Nothing;
        bool ctrl = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);
        if (!ctrl && real.All(p => ShellPaths.Equal(Path.GetDirectoryName(p), _currentPath))) return DropPlan.Nothing;
        var pe = ChooseEffect(e, real, _currentPath);
        return pe == DragDropEffects.None ? DropPlan.Nothing
            : new DropPlan(DropKind.FileOp, pe, real, _currentPath, pe == DragDropEffects.Copy);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.RoutedEvent == DragEnterEvent) BeginPeek();
        var plan = PlanDrop(e);
        e.Effects = plan.Effect;
        DropHighlight.Visibility = plan.Effect == DragDropEffects.None ? Visibility.Collapsed : Visibility.Visible;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        SchedulePeekCheck();
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        var plan = PlanDrop(e);
        // Si nosotros movemos los archivos, el origen no debe intentar borrarlos.
        e.Effects = plan.Kind == DropKind.FileOp && !plan.Copy ? DragDropEffects.None : plan.Effect;
        e.Handled = true;
        if (plan.Kind != DropKind.None)
            Dispatcher.BeginInvoke(() => Execute(plan)); // no bloquear el bucle de arrastre del origen
        SchedulePeekCheck();
    }

    private void Execute(DropPlan plan)
    {
        try
        {
            switch (plan.Kind)
            {
                case DropKind.Reorder:
                    if (Model.Sort != SortMode.Manual)
                    {
                        var visible = _items.Select(i => i.Path).ToList();
                        Model.Items = visible.Concat(Model.Items.Where(p => !visible.Contains(p, StringComparer.OrdinalIgnoreCase))).ToList();
                        Model.Sort = SortMode.Manual;
                    }
                    if (plan.InsertBefore != null && plan.Paths.Contains(plan.InsertBefore, StringComparer.OrdinalIgnoreCase)) return;
                    _mgr.MoveReferences(plan.Paths, this, plan.InsertBefore);
                    break;

                case DropKind.Reference:
                    _mgr.MoveReferences(plan.Paths, this, plan.InsertBefore);
                    break;

                case DropKind.FileOp when plan.TargetDir != null:
                {
                    var toTransfer = plan.Paths.Where(p => !ShellPaths.Equal(Path.GetDirectoryName(p), plan.TargetDir) || plan.Copy).ToList();
                    var already = plan.Paths.Except(toTransfer, StringComparer.OrdinalIgnoreCase).ToList();
                    var dests = FileOps.PlanDestinations(toTransfer, plan.TargetDir);
                    bool addRefs = !IsPortal && ShellPaths.Equal(plan.TargetDir, DesktopShell.UserDesktop);
                    if (addRefs) _mgr.MoveReferences(already.Concat(dests), this, plan.InsertBefore);
                    if (toTransfer.Count > 0) FileOps.Transfer(!plan.Copy, toTransfer, dests, _hwnd);
                    AfterExternalChange();
                    if (addRefs) Reload();
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex);
        }
    }
}
