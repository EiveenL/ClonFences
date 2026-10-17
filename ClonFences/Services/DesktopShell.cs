using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Automation;
using ClonFences.Native;
using static ClonFences.Native.NativeMethods;

namespace ClonFences.Services;

/// <summary>Integración con el escritorio de Explorer (Progman / WorkerW / SHELLDLL_DefView).</summary>
internal static class DesktopShell
{
    private const int ToggleDesktopIconsCommand = 0x7402;

    public static string UserDesktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string CommonDesktop => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);

    public static IEnumerable<string> DesktopFolders()
    {
        var list = new List<string>();
        foreach (var d in new[] { UserDesktop, CommonDesktop })
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d) && !list.Any(x => ShellPaths.Equal(x, d)))
                list.Add(d);
        return list;
    }

    public static bool IsOnDesktop(string path)
    {
        if (ShellPaths.IsVirtual(path)) return true;
        string? dir;
        try { dir = Path.GetDirectoryName(path); } catch { return false; }
        return ShellPaths.Equal(dir, UserDesktop) || ShellPaths.Equal(dir, CommonDesktop);
    }

    public static IEnumerable<string> EnumerateDesktopItems() =>
        DesktopFolders().SelectMany(ShellPaths.SafeEnumerate);

    /// <summary>Ventana de nivel superior que contiene la vista de iconos del escritorio.</summary>
    public static IntPtr GetDesktopHost()
    {
        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero && FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            return progman;

        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (ClassOf(h) == "WorkerW" && FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                found = h;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found != IntPtr.Zero ? found : progman;
    }

    private static IntPtr GetDefView()
    {
        var host = GetDesktopHost();
        return host == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null);
    }

    private static IntPtr GetListView()
    {
        var defView = GetDefView();
        return defView == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    public static bool AreIconsVisible()
    {
        var lv = GetListView();
        return lv != IntPtr.Zero && IsWindowVisible(lv);
    }

    /// <summary>Equivale a Escritorio ▸ Ver ▸ Mostrar iconos del escritorio.</summary>
    public static void SetIconsVisible(bool visible)
    {
        try
        {
            if (AreIconsVisible() == visible) return;
            var defView = GetDefView();
            if (defView != IntPtr.Zero)
                SendMessage(defView, WM_COMMAND, new IntPtr(ToggleDesktopIconsCommand), IntPtr.Zero);
        }
        catch (Exception ex) { Logger.Log(ex); }
    }

    /// <summary>¿La ventana bajo el cursor es el propio escritorio? (seguro de llamar desde el hook).</summary>
    public static bool IsDesktopWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        var cls = ClassOf(hwnd);
        if (cls != "SysListView32" && cls != "SHELLDLL_DefView" && cls != "Progman" && cls != "WorkerW")
            return false;
        var rootCls = ClassOf(GetAncestor(hwnd, GA_ROOT));
        if (rootCls != "Progman" && rootCls != "WorkerW") return false;
        if (cls == "SysListView32" && ClassOf(GetParent(hwnd)) != "SHELLDLL_DefView") return false;
        return true;
    }

    /// <summary>¿El punto está sobre un hueco vacío del escritorio (no sobre un icono)?</summary>
    public static bool IsEmptyDesktopSpot(POINT pt)
    {
        if (!IsDesktopWindow(WindowFromPoint(pt))) return false;
        if (!AreIconsVisible()) return true;
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
            if (el == null) return true;
            if (el.Current.ControlType == ControlType.ListItem) return false;
            var parent = TreeWalker.ControlViewWalker.GetParent(el);
            return parent == null || parent.Current.ControlType != ControlType.ListItem;
        }
        catch
        {
            return false;
        }
    }
}
