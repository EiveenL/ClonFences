using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClonFences.Models;
using ClonFences.Native;
using ClonFences.Views;
using static ClonFences.Native.NativeMethods;

namespace ClonFences.Services;

/// <summary>Núcleo de la aplicación: ventanas de fences, escritorio, hook de ratón, persistencia.</summary>
internal sealed class FenceManager : IDisposable
{
    private static readonly (string Title, string[] Rules)[] Categories =
    {
        ("Programas", new[] { ".lnk", ".url", ".exe", ".appref-ms", ".msc", ".bat", ".cmd", ".ps1" }),
        ("Carpetas", new[] { "carpeta" }),
        ("Documentos", new[] { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".txt", ".rtf",
                               ".odt", ".ods", ".odp", ".csv", ".md", ".json", ".xml", ".epub" }),
        ("Imágenes y multimedia", new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".heic",
                                          ".tif", ".tiff", ".psd", ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm",
                                          ".mp3", ".wav", ".flac", ".m4a", ".ogg" }),
        ("Comprimidos e instaladores", new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".msi", ".msix", ".iso" }),
        ("Otros", Array.Empty<string>()),
    };

    private static readonly HashSet<string> PartialExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload" };

    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, FenceWindow> _windows = new();
    private readonly List<FenceModel> _pendingRecreate = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _hostTimer;
    private readonly DispatcherTimer _validateTimer;
    private readonly DesktopMouseHook _hook = new();
    private readonly List<FileSystemWatcher> _desktopWatchers = new();
    private readonly object _dragLock = new();

    private SelectionOverlay? _overlay;
    private SettingsWindow? _settingsWindow;
    private IntPtr _host;
    private bool _hidden;
    private bool _iconsHiddenByQuickHide;
    private bool _validateQueued;
    private RECT _dragRect;
    private bool _dragQueued;

    public FenceManager(AppConfig config)
    {
        Config = config;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };

        _hostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _hostTimer.Tick += (_, _) => CheckDesktopHost();

        _validateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _validateTimer.Tick += (_, _) => ValidateAll();
    }

    public AppConfig Config { get; }
    public AppSettings Settings => Config.Settings;
    public bool FencesHidden => _hidden;
    public bool IsShuttingDown { get; private set; }
    public IEnumerable<FenceWindow> Windows => _windows.Values;
    public event Action<string, string>? Notify;

    // ───────────────────────────── Ciclo de vida ─────────────────────────────

    public void Start()
    {
        _host = DesktopShell.GetDesktopHost();
        foreach (var m in Config.Fences.ToList()) OpenWindow(m);

        ApplyDesktopIconsSetting(previous: false);
        StartDesktopWatchers();

        _hook.DoubleClicked += pt => _dispatcher.BeginInvoke(() => OnDesktopDoubleClick(pt));
        _hook.DragMoved += OnHookDragMoved;
        _hook.DragFinished += (a, b) => _dispatcher.BeginInvoke(DispatcherPriority.Input, () => OnDragFinished(a, b));
        _hook.DragCanceled += () => _dispatcher.BeginInvoke(DispatcherPriority.Input, () => _overlay?.HideOverlay());
        ApplyHookSettings();
        _hook.Start();

        _hostTimer.Start();
        _validateTimer.Start();

        if (Settings.HideDesktopIcons && Settings.AutoAddNewItems) AddUnfencedDesktopItems();
    }

    public void Shutdown()
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;
        _hostTimer.Stop();
        _validateTimer.Stop();
        _hook.Dispose();
        foreach (var w in _desktopWatchers) w.Dispose();
        _desktopWatchers.Clear();

        foreach (var w in _windows.Values) w.ReadBounds();
        _saveTimer.Stop();
        SaveNow();

        // Al salir, devolver los iconos del escritorio si los ocultamos nosotros.
        if (Settings.HideDesktopIcons || _iconsHiddenByQuickHide) DesktopShell.SetIconsVisible(true);

        foreach (var w in _windows.Values.ToList())
        {
            w.ClosingIntentionally = true;
            w.Close();
        }
        _windows.Clear();
        _overlay?.Close();
        _settingsWindow?.Close();
    }

    public void Dispose() => Shutdown();

    public void SaveSoon()
    {
        if (IsShuttingDown) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        try { ConfigService.Save(Config); }
        catch (Exception ex) { Logger.Log(ex); }
    }

    private void Delay(int ms, Action action)
    {
        var t = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (!IsShuttingDown) action();
        };
        t.Start();
    }

    // ───────────────────────────── Ventanas ─────────────────────────────

    private FenceWindow OpenWindow(FenceModel model)
    {
        var w = new FenceWindow(model, this);
        if (_host != IntPtr.Zero) new WindowInteropHelper(w).Owner = _host;
        w.Closed += OnWindowClosed;
        _windows[model.Id] = w;
        if (!_hidden) w.Show();
        return w;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not FenceWindow w) return;
        w.DisposeResources();
        if (IsShuttingDown || w.ClosingIntentionally) return;
        // La ventana murió con su propietaria (Explorer se reinició): recrearla luego.
        if (_windows.TryGetValue(w.Model.Id, out var current) && ReferenceEquals(current, w))
            _windows.Remove(w.Model.Id);
        _pendingRecreate.Add(w.Model);
        Logger.Log($"Fence \"{w.Model.Title}\" cerrado por el sistema; se recreará.");
    }

    private void CheckDesktopHost()
    {
        var host = DesktopShell.GetDesktopHost();
        if (host == IntPtr.Zero) return;
        if (host != _host || !IsWindow(_host))
        {
            _host = host;
            foreach (var w in _windows.Values) w.SetOwner(host);
            if (Settings.HideDesktopIcons) DesktopShell.SetIconsVisible(false);
        }
        if (_pendingRecreate.Count > 0)
        {
            var list = _pendingRecreate.ToList();
            _pendingRecreate.Clear();
            foreach (var m in list)
                if (Config.Fences.Contains(m) && !_windows.ContainsKey(m.Id))
                    OpenWindow(m);
        }
    }

    public bool IsStandardFence(string id) => _windows.TryGetValue(id, out var w) && !w.IsPortal;

    public IEnumerable<RECT> GetOtherFenceRects(FenceWindow self)
    {
        foreach (var w in _windows.Values)
        {
            if (ReferenceEquals(w, self) || w.Handle == IntPtr.Zero || !w.IsVisible) continue;
            if (GetWindowRect(w.Handle, out var r)) yield return r;
        }
    }

    private IEnumerable<FenceWindow> StandardWindows => _windows.Values.Where(w => !w.IsPortal);

    // ───────────────────────────── Crear / eliminar ─────────────────────────────

    public FenceWindow CreateFence(RECT r, string? folder, string title, bool beginRename)
    {
        if (_hidden) ToggleAllFences();
        var m = new FenceModel
        {
            Title = title,
            X = r.Left,
            Y = r.Top,
            Width = r.Width,
            Height = r.Height,
            FolderPath = folder,
        };
        Config.Fences.Add(m);
        var w = OpenWindow(m);
        if (folder == null && Settings.DefaultFenceId == null) Settings.DefaultFenceId = m.Id;
        SaveSoon();
        if (beginRename) w.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => w.BeginRename());
        return w;
    }

    private static RECT RectAt(POINT p, int w, int h)
    {
        var wa = GetWorkAreaAt(p);
        int x = Math.Clamp(p.X - w / 2, wa.Left, Math.Max(wa.Left, wa.Right - w));
        int y = Math.Clamp(p.Y - 20, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        return new RECT(x, y, x + w, y + h);
    }

    private static (int W, int H) DefaultSize()
    {
        double s = SystemScale;
        return ((int)(360 * s), (int)(250 * s));
    }

    public void CreateFenceAtCursor()
    {
        GetCursorPos(out var p);
        var (w, h) = DefaultSize();
        CreateFence(RectAt(p, w, h), null, "Nuevo fence", beginRename: true);
    }

    public void CreatePortalInteractive()
    {
        var folder = PickFolder();
        if (folder == null) return;
        GetCursorPos(out var p);
        var (w, h) = DefaultSize();
        CreateFence(RectAt(p, w, h), folder, FolderTitle(folder), beginRename: false);
    }

    public void CreateFenceNear(FenceWindow near)
    {
        var (w, h) = DefaultSize();
        if (near.Handle != IntPtr.Zero && GetWindowRect(near.Handle, out var r))
        {
            int gap = (int)(16 * SystemScale);
            var wa = GetWorkArea(r);
            int x = r.Right + gap;
            if (x + w > wa.Right) x = Math.Max(wa.Left, r.Left - gap - w);
            CreateFence(new RECT(x, r.Top, x + w, r.Top + h), null, "Nuevo fence", beginRename: true);
        }
        else CreateFenceAtCursor();
    }

    public string? PickFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Elige la carpeta que mostrará el portal" };
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private static string FolderTitle(string folder)
    {
        var name = Path.GetFileName(folder.TrimEnd('\\'));
        return string.IsNullOrEmpty(name) ? folder : name;
    }

    public void ConfirmDeleteFence(FenceWindow w)
    {
        var msg = w.IsPortal
            ? $"¿Eliminar el portal «{w.Model.Title}»?\n\nLa carpeta y sus archivos no se tocan."
            : $"¿Eliminar el fence «{w.Model.Title}»?\n\nLos archivos no se borran: siguen en el escritorio." +
              (Settings.HideDesktopIcons && w.Model.Items.Count > 0 ? "\nSus elementos pasarán a otro fence." : "");
        if (MessageBox.Show(msg, "Clon Fences", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        DeleteFence(w);
    }

    public void DeleteFence(FenceWindow w)
    {
        var orphans = w.IsPortal ? new List<string>() : w.Model.Items.ToList();
        w.ClosingIntentionally = true;
        _windows.Remove(w.Model.Id);
        Config.Fences.Remove(w.Model);
        if (Settings.DefaultFenceId == w.Model.Id) Settings.DefaultFenceId = null;
        w.Close();

        // Con los iconos de Windows ocultos, los elementos huérfanos serían invisibles.
        if (Settings.HideDesktopIcons && orphans.Count > 0)
        {
            var target = DefaultFence(createIfMissing: true);
            target?.AddFiles(orphans);
        }
        SaveSoon();
    }

    public void ConvertToPortal(FenceWindow w, string folder)
    {
        var orphans = w.IsPortal ? new List<string>() : w.Model.Items.ToList();
        w.ConvertToPortal(folder);
        if (Settings.HideDesktopIcons && orphans.Count > 0)
            DefaultFence(createIfMissing: true, exclude: w)?.AddFiles(orphans);
    }

    // ───────────────────────────── Referencias de elementos ─────────────────────────────

    /// <summary>Coloca rutas en <paramref name="target"/> (quitándolas de los demás fences).</summary>
    public void MoveReferences(IEnumerable<string> paths, FenceWindow target, string? insertBefore)
    {
        if (target.IsPortal) return;
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0) return;
        var set = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);

        foreach (var w in StandardWindows)
        {
            if (w.Model.Items.RemoveAll(set.Contains) > 0 && !ReferenceEquals(w, target)) w.Reload();
        }

        var items = target.Model.Items;
        int index = insertBefore == null ? -1 : items.FindIndex(p => string.Equals(p, insertBefore, StringComparison.OrdinalIgnoreCase));
        if (index < 0) items.AddRange(list);
        else items.InsertRange(index, list);
        target.Reload();
        SaveSoon();
    }

    public void RemoveReferences(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var w in StandardWindows)
            if (w.Model.Items.RemoveAll(set.Contains) > 0) w.Reload();
        SaveSoon();
    }

    public bool RenamePath(string oldPath, string newPath)
    {
        bool found = false;
        foreach (var w in StandardWindows)
        {
            int i = w.Model.Items.FindIndex(p => string.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            w.Model.Items[i] = newPath;
            w.Reload();
            found = true;
        }
        if (found)
        {
            IconLoader.Invalidate(oldPath);
            SaveSoon();
        }
        return found;
    }

    private FenceWindow? FindFenceContaining(string path) =>
        StandardWindows.FirstOrDefault(w => w.Model.Items.Contains(path, StringComparer.OrdinalIgnoreCase));

    public void ValidateSoon()
    {
        if (_validateQueued) return;
        _validateQueued = true;
        Delay(800, () => { _validateQueued = false; ValidateAll(); });
    }

    public void ValidateAll()
    {
        foreach (var w in StandardWindows.ToList())
            if (w.Model.Items.Any(p => !ShellPaths.Exists(p))) w.Reload();
        IconLoader.Invalidate(ShellPaths.RecycleBin);
    }

    // ───────────────────────────── Escritorio ─────────────────────────────

    private void StartDesktopWatchers()
    {
        foreach (var dir in DesktopShell.DesktopFolders())
        {
            try
            {
                var fsw = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                fsw.Created += (_, e) => _dispatcher.BeginInvoke(() => OnDesktopCreated(e.FullPath));
                fsw.Deleted += (_, e) => _dispatcher.BeginInvoke(() => OnDesktopDeleted(e.FullPath));
                fsw.Renamed += (_, e) => _dispatcher.BeginInvoke(() => OnDesktopRenamed(e.OldFullPath, e.FullPath));
                fsw.EnableRaisingEvents = true;
                _desktopWatchers.Add(fsw);
            }
            catch (Exception ex) { Logger.Log(ex); }
        }
    }

    private void OnDesktopCreated(string path)
    {
        if (PartialExtensions.Contains(Path.GetExtension(path))) return; // descargas a medias: esperar al renombrado
        Delay(1200, () =>
        {
            if (!Settings.HideDesktopIcons || !Settings.AutoAddNewItems) return;
            if (!ShellPaths.Exists(path) || ShellPaths.IsHiddenOrSystem(path)) return;
            if (FindFenceContaining(path) != null) return;
            var target = FindRuleFence(path) ?? DefaultFence(createIfMissing: true);
            target?.AddFiles(new[] { path });
        });
    }

    private void OnDesktopDeleted(string path)
    {
        Delay(1500, () =>
        {
            if (!ShellPaths.Exists(path) && FindFenceContaining(path) != null)
                RemoveReferences(new[] { path });
        });
    }

    private void OnDesktopRenamed(string oldPath, string newPath)
    {
        if (!RenamePath(oldPath, newPath)) OnDesktopCreated(newPath);
    }

    private FenceWindow? FindRuleFence(string path)
    {
        bool isDir = Directory.Exists(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return StandardWindows.FirstOrDefault(w =>
            w.Model.Rules.Any(r => isDir ? r == "carpeta" : r == ext));
    }

    private FenceWindow? DefaultFence(bool createIfMissing, FenceWindow? exclude = null)
    {
        if (Settings.DefaultFenceId != null && _windows.TryGetValue(Settings.DefaultFenceId, out var w) &&
            !w.IsPortal && !ReferenceEquals(w, exclude))
            return w;
        var any = StandardWindows.FirstOrDefault(x => !ReferenceEquals(x, exclude));
        if (any != null || !createIfMissing) return any;

        var wa = GetWorkAreaAt(new POINT(0, 0));
        var (dw, dh) = DefaultSize();
        int m = (int)(20 * SystemScale);
        var created = CreateFence(new RECT(wa.Left + m, wa.Top + m, wa.Left + m + dw, wa.Top + m + dh), null, "Escritorio", false);
        Settings.DefaultFenceId = created.Model.Id;
        return created;
    }

    public void AddUnfencedDesktopItems()
    {
        var all = new HashSet<string>(StandardWindows.SelectMany(w => w.Model.Items), StringComparer.OrdinalIgnoreCase);
        foreach (var path in DesktopShell.EnumerateDesktopItems().Where(p => !all.Contains(p)).ToList())
        {
            if (PartialExtensions.Contains(Path.GetExtension(path))) continue;
            var target = FindRuleFence(path) ?? DefaultFence(createIfMissing: true);
            target?.Model.Items.Add(path);
        }
        foreach (var w in StandardWindows) w.Reload();
        SaveSoon();
    }

    public void ApplyDesktopIconsSetting(bool previous)
    {
        if (Settings.HideDesktopIcons) DesktopShell.SetIconsVisible(false);
        else if (previous) DesktopShell.SetIconsVisible(true);
    }

    public void SetHideDesktopIcons(bool hide)
    {
        bool previous = Settings.HideDesktopIcons;
        Settings.HideDesktopIcons = hide;
        ApplyDesktopIconsSetting(previous);
        if (hide && !previous && Settings.AutoAddNewItems) AddUnfencedDesktopItems();
        SaveSoon();
    }

    public void ApplyHookSettings()
    {
        _hook.QuickHideEnabled = Settings.QuickHide;
        _hook.RightDragEnabled = Settings.RightDragCreate;
    }

    // ───────────────────────────── Ocultación rápida ─────────────────────────────

    private void OnDesktopDoubleClick(POINT pt)
    {
        if (DesktopShell.IsEmptyDesktopSpot(pt)) ToggleAllFences();
    }

    public void ToggleAllFences()
    {
        _hidden = !_hidden;
        if (_hidden)
        {
            foreach (var w in _windows.Values) w.FadeOut();
            _iconsHiddenByQuickHide = DesktopShell.AreIconsVisible();
            if (_iconsHiddenByQuickHide) DesktopShell.SetIconsVisible(false);
        }
        else
        {
            foreach (var w in _windows.Values) w.FadeIn();
            if (_iconsHiddenByQuickHide) DesktopShell.SetIconsVisible(true);
            _iconsHiddenByQuickHide = false;
        }
    }

    // ───────────────────────────── Dibujar un fence con clic derecho ─────────────────────────────

    private static RECT Normalize(POINT a, POINT b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private void OnHookDragMoved(POINT a, POINT b)
    {
        lock (_dragLock)
        {
            _dragRect = Normalize(a, b);
            if (_dragQueued) return;
            _dragQueued = true;
        }
        _dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            RECT r;
            lock (_dragLock)
            {
                r = _dragRect;
                _dragQueued = false;
            }
            (_overlay ??= new SelectionOverlay()).ShowRect(r);
        });
    }

    private void OnDragFinished(POINT a, POINT b)
    {
        var r = Normalize(a, b);
        _overlay ??= new SelectionOverlay();
        _overlay.ShowRect(r);
        int cmd = _overlay.ShowMenu(b, new[] { "Crear fence aquí", "Crear portal de carpeta aquí", null, "Cancelar" });
        _overlay.HideOverlay();

        double s = SystemScale;
        int minW = (int)(160 * s), minH = (int)(110 * s);
        if (r.Width < minW) r.Right = r.Left + minW;
        if (r.Height < minH) r.Bottom = r.Top + minH;

        if (cmd == 1) CreateFence(r, null, "Nuevo fence", beginRename: true);
        else if (cmd == 2)
        {
            var folder = PickFolder();
            if (folder != null) CreateFence(r, folder, FolderTitle(folder), beginRename: false);
        }
    }

    // ───────────────────────────── Organizar escritorio ─────────────────────────────

    public void Organize()
    {
        var fenced = new HashSet<string>(StandardWindows.SelectMany(w => w.Model.Items), StringComparer.OrdinalIgnoreCase);
        var loose = DesktopShell.EnumerateDesktopItems().Where(p => !fenced.Contains(p)).ToList();

        if (Settings.HideDesktopIcons)
            foreach (var v in new[] { ShellPaths.ThisPc, ShellPaths.RecycleBin })
                if (!fenced.Contains(v)) loose.Insert(0, v);

        if (loose.Count == 0)
        {
            Notify?.Invoke("Clon Fences", "No hay elementos sueltos en el escritorio para organizar.");
            return;
        }

        var groups = new Dictionary<string, List<string>>();
        foreach (var p in loose)
        {
            string cat;
            if (ShellPaths.IsVirtual(p)) cat = "Programas";
            else
            {
                bool isDir = Directory.Exists(p);
                var ext = Path.GetExtension(p).ToLowerInvariant();
                cat = Categories.First(c => c.Rules.Length == 0 || c.Rules.Any(r => isDir ? r == "carpeta" : r == ext)).Title;
                if (isDir) cat = "Carpetas";
            }
            if (!groups.TryGetValue(cat, out var list)) groups[cat] = list = new List<string>();
            list.Add(p);
        }

        double s = SystemScale;
        var wa = GetWorkAreaAt(new POINT(0, 0));
        int margin = (int)(20 * s), gap = (int)(16 * s);
        int fw = (int)(340 * s);
        int tileW = (int)((Settings.DefaultIconSize + 36 + 6) * s);
        int tileH = (int)((Settings.DefaultIconSize + 50) * s);
        int cols = Math.Max(1, (fw - (int)(12 * s)) / tileW);

        // Colocar los fences nuevos bajo los existentes para no taparlos.
        int x = wa.Left + margin, y = wa.Top + margin, rowH = 0;
        foreach (var w in _windows.Values)
            if (w.Handle != IntPtr.Zero && GetWindowRect(w.Handle, out var er) && er.Left < wa.Left + fw * 2)
                y = Math.Max(y, er.Bottom + gap);
        if (y > wa.Bottom - (int)(150 * s)) y = wa.Top + margin;

        FenceWindow? others = null;
        foreach (var (title, rules) in Categories)
        {
            if (!groups.TryGetValue(title, out var items)) continue;
            var existing = StandardWindows.FirstOrDefault(w => w.Model.Title == title);
            if (existing != null)
            {
                existing.AddFiles(items);
                if (title == "Otros") others = existing;
                continue;
            }

            int rows = (items.Count + cols - 1) / cols;
            int fh = Math.Clamp((int)((HeaderHeightPx + 14) * s) + rows * tileH, (int)(150 * s), (int)(520 * s));
            if (x + fw > wa.Right - margin)
            {
                x = wa.Left + margin;
                y += rowH + gap;
                rowH = 0;
            }
            var created = CreateFence(new RECT(x, y, x + fw, y + fh), null, title, beginRename: false);
            created.Model.Rules = rules.ToList();
            created.AddFiles(items);
            if (title == "Otros") others = created;
            x += fw + gap;
            rowH = Math.Max(rowH, fh);
        }

        if (others != null) Settings.DefaultFenceId = others.Model.Id;
        SaveSoon();
    }

    private const double HeaderHeightPx = FenceWindow.HeaderHeight + 2;

    // ───────────────────────────── Instantáneas ─────────────────────────────

    public void SaveSnapshot(string name)
    {
        foreach (var w in _windows.Values) w.ReadBounds();
        Config.Snapshots.Add(new LayoutSnapshot
        {
            Name = name,
            Created = DateTime.Now,
            Fences = ConfigService.Clone(Config.Fences),
        });
        SaveSoon();
    }

    public void RestoreSnapshot(LayoutSnapshot snapshot)
    {
        foreach (var w in _windows.Values.ToList())
        {
            w.ClosingIntentionally = true;
            w.Close();
        }
        _windows.Clear();
        _pendingRecreate.Clear();
        Config.Fences.Clear();
        Config.Fences.AddRange(ConfigService.Clone(snapshot.Fences));
        Config.Normalize();
        if (Settings.DefaultFenceId != null && Config.Fences.All(f => f.Id != Settings.DefaultFenceId))
            Settings.DefaultFenceId = null;
        foreach (var m in Config.Fences) OpenWindow(m);
        if (Settings.HideDesktopIcons && Settings.AutoAddNewItems) AddUnfencedDesktopItems();
        SaveSoon();
    }

    public void DeleteSnapshot(LayoutSnapshot snapshot)
    {
        Config.Snapshots.Remove(snapshot);
        SaveSoon();
    }

    // ───────────────────────────── Configuración ─────────────────────────────

    public void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    public void ApplySettings(bool previousHideIcons)
    {
        ApplyHookSettings();
        ApplyDesktopIconsSetting(previousHideIcons);
        if (Settings.HideDesktopIcons && !previousHideIcons && Settings.AutoAddNewItems) AddUnfencedDesktopItems();
        foreach (var w in _windows.Values)
        {
            w.ApplyAppearance();
            w.RefreshLockState();
            w.ApplyBounds();
        }
        SaveSoon();
    }

    public void SetLockAll(bool locked)
    {
        Settings.LockAll = locked;
        foreach (var w in _windows.Values) w.RefreshLockState();
        SaveSoon();
    }

    public void RunFirstRunWizard()
    {
        var answer = MessageBox.Show(
            "¡Bienvenido a Clon Fences!\n\n" +
            "¿Quieres organizar tu escritorio ahora?\n\n" +
            "• Se crearán fences por tipo (Programas, Carpetas, Documentos…).\n" +
            "• Se ocultarán los iconos de Windows: los fences los muestran.\n" +
            "• Tus archivos NO se mueven: siguen en la carpeta Escritorio.\n\n" +
            "Puedes cambiarlo cuando quieras desde el icono de la bandeja.",
            "Clon Fences", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer == MessageBoxResult.Yes)
        {
            Settings.HideDesktopIcons = true;
            Organize();
            if (!StandardWindows.Any()) DefaultFence(createIfMissing: true);
            ApplyDesktopIconsSetting(previous: false);
        }
        else
        {
            var wa = GetWorkAreaAt(new POINT(0, 0));
            var (w, h) = DefaultSize();
            int m = (int)(30 * SystemScale);
            CreateFence(new RECT(wa.Left + m, wa.Top + m, wa.Left + m + w, wa.Top + m + h), null, "Mi primer fence", false);
        }
        Settings.FirstRun = false;
        SaveSoon();
        Notify?.Invoke("Clon Fences está activo",
            "Clic derecho + arrastrar en el escritorio crea un fence. Doble clic en el escritorio oculta/muestra todo.");
    }
}
