using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using ClonFences.Native;
using static ClonFences.Native.NativeMethods;

namespace ClonFences.Services;

internal readonly record struct ShellMenuResult(bool Shown, int ExtraId, string? Verb);

/// <summary>Muestra el menú contextual real de Explorer para uno o varios archivos.</summary>
internal static class ShellContextMenu
{
    private const uint CmdFirst = 1, CmdLast = 0x7FFF, ExtraBase = 0x8000;
    private const uint CMF_NORMAL = 0x0, CMF_EXTENDEDVERBS = 0x100, CMF_CANRENAME = 0x10;
    private const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000,
        CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;
    private const uint GCS_VERBW = 4;

    private static readonly Guid IID_IShellFolder = typeof(IShellFolder).GUID;
    private static readonly Guid IID_IContextMenu = typeof(IContextMenu).GUID;

    private static IContextMenu2? _cm2;
    private static IContextMenu3? _cm3;

    /// <summary>Reenvía los mensajes de dibujo de submenús (Enviar a, Abrir con…) mientras el menú está abierto.</summary>
    public static bool HandleMenuMessage(int msg, IntPtr wParam, IntPtr lParam, out IntPtr result)
    {
        result = IntPtr.Zero;
        try
        {
            if (_cm3 != null)
                return _cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out result) == 0;
            if (_cm2 != null && msg != WM_MENUCHAR)
                return _cm2.HandleMenuMsg((uint)msg, wParam, lParam) == 0;
        }
        catch { }
        return false;
    }

    public static ShellMenuResult Show(IntPtr hwnd, IReadOnlyList<string> paths, POINT pt, IReadOnlyList<(int Id, string Text)> extras)
    {
        if (paths.Count == 0) return default;

        var pidls = new List<IntPtr>();
        IntPtr parentPtr = IntPtr.Zero, cmPtr = IntPtr.Zero, hmenu = IntPtr.Zero;
        IShellFolder? parent = null;
        IContextMenu? cm = null;
        try
        {
            // GetUIObjectOf exige que todos los elementos compartan carpeta padre.
            var parentDir = ShellPaths.IsVirtual(paths[0]) ? null : Path.GetDirectoryName(paths[0]);
            foreach (var p in paths)
            {
                var dir = ShellPaths.IsVirtual(p) ? null : Path.GetDirectoryName(p);
                if (!(dir == null && parentDir == null) && !ShellPaths.Equal(dir, parentDir)) continue;
                if (SHParseDisplayName(p, IntPtr.Zero, out var pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                    pidls.Add(pidl);
                if (ShellPaths.IsVirtual(p)) break; // los elementos virtuales van de uno en uno
            }
            if (pidls.Count == 0) return default;

            var iidSf = IID_IShellFolder;
            if (SHBindToParent(pidls[0], ref iidSf, out parentPtr, out _) != 0 || parentPtr == IntPtr.Zero)
                return default;
            parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);

            var children = pidls.Select(ShellInterop.ILFindLastID).ToArray();
            var iidCm = IID_IContextMenu;
            if (parent.GetUIObjectOf(hwnd, (uint)children.Length, children, ref iidCm, IntPtr.Zero, out cmPtr) != 0 || cmPtr == IntPtr.Zero)
                return default;
            cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);

            hmenu = CreatePopupMenu();
            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            uint flags = CMF_NORMAL | CMF_CANRENAME | (shift ? CMF_EXTENDEDVERBS : 0);
            if (cm.QueryContextMenu(hmenu, 0, CmdFirst, CmdLast, flags) < 0) return default;

            uint pos = 0;
            foreach (var (id, text) in extras)
                InsertMenu(hmenu, pos++, MF_BYPOSITION | MF_STRING, (UIntPtr)(ExtraBase + (uint)id), text);
            if (extras.Count > 0)
                InsertMenu(hmenu, pos, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);

            _cm2 = cm as IContextMenu2;
            _cm3 = cm as IContextMenu3;
            SetForegroundWindow(hwnd);
            int cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.X, pt.Y, hwnd, IntPtr.Zero);
            PostMessage(hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            _cm2 = null;
            _cm3 = null;

            if (cmd <= 0) return new ShellMenuResult(true, 0, null);
            if (cmd >= ExtraBase) return new ShellMenuResult(true, cmd - (int)ExtraBase, null);

            var offset = (uint)cmd - CmdFirst;
            var verb = GetVerb(cm, offset);
            if (string.Equals(verb, "rename", StringComparison.OrdinalIgnoreCase))
                return new ShellMenuResult(true, 0, "rename");

            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE |
                        (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? CMIC_MASK_SHIFT_DOWN : 0) |
                        (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? CMIC_MASK_CONTROL_DOWN : 0),
                hwnd = hwnd,
                lpVerb = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                nShow = 1, // SW_SHOWNORMAL
                ptInvoke = pt,
            };
            cm.InvokeCommand(ref info);
            return new ShellMenuResult(true, 0, verb ?? "");
        }
        catch (Exception ex)
        {
            Logger.Log(ex);
            return default;
        }
        finally
        {
            _cm2 = null;
            _cm3 = null;
            if (hmenu != IntPtr.Zero) DestroyMenu(hmenu);
            if (cm != null) Marshal.ReleaseComObject(cm);
            if (cmPtr != IntPtr.Zero) Marshal.Release(cmPtr);
            if (parent != null) Marshal.ReleaseComObject(parent);
            if (parentPtr != IntPtr.Zero) Marshal.Release(parentPtr);
            foreach (var p in pidls) Marshal.FreeCoTaskMem(p);
        }
    }

    private static string? GetVerb(IContextMenu cm, uint offset)
    {
        IntPtr buf = Marshal.AllocHGlobal(520);
        try
        {
            Marshal.WriteInt16(buf, 0);
            if (cm.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buf, 260) != 0) return null;
            return Marshal.PtrToStringUni(buf);
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
