using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using ClonFences.Native;
using static ClonFences.Native.NativeMethods;

namespace ClonFences.Services;

/// <summary>Operaciones de archivo con los diálogos estándar de Windows (progreso, conflictos, papelera).</summary>
internal static class FileOps
{
    private const uint FO_MOVE = 1, FO_COPY = 2, FO_DELETE = 3;
    private const ushort FOF_MULTIDESTFILES = 0x1, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40,
        FOF_NOCONFIRMMKDIR = 0x200, FOF_WANTNUKEWARNING = 0x4000;

    private static string Join(IEnumerable<string> paths) => string.Join("\0", paths) + "\0";

    public static bool Transfer(bool move, IReadOnlyList<string> sources, IReadOnlyList<string> destinations, IntPtr hwnd)
    {
        if (sources.Count == 0) return true;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = hwnd,
            wFunc = move ? FO_MOVE : FO_COPY,
            pFrom = Join(sources),
            pTo = Join(destinations),
            fFlags = FOF_MULTIDESTFILES | FOF_ALLOWUNDO | FOF_NOCONFIRMMKDIR,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    public static bool Delete(IReadOnlyList<string> paths, IntPtr hwnd, bool permanent)
    {
        if (paths.Count == 0) return true;
        var op = new SHFILEOPSTRUCT
        {
            hwnd = hwnd,
            wFunc = FO_DELETE,
            pFrom = Join(paths),
            fFlags = permanent ? FOF_WANTNUKEWARNING : (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING),
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>Calcula destinos únicos dentro de <paramref name="dir"/>: "nombre (2).ext", etc.</summary>
    public static List<string> PlanDestinations(IEnumerable<string> sources, string dir)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var src in sources)
        {
            var name = Path.GetFileName(src.TrimEnd('\\', '/'));
            var dest = Path.Combine(dir, name);
            if (Exists(dest) || reserved.Contains(dest))
            {
                bool isDir = Directory.Exists(src);
                var stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
                var ext = isDir ? "" : Path.GetExtension(name);
                for (int i = 2; ; i++)
                {
                    dest = Path.Combine(dir, $"{stem} ({i}){ext}");
                    if (!Exists(dest) && !reserved.Contains(dest)) break;
                }
            }
            reserved.Add(dest);
            result.Add(dest);
        }
        return result;
    }

    private static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    public static void Open(string path)
    {
        try
        {
            if (ShellPaths.IsVirtual(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:" + path) { UseShellExecute = true });
                return;
            }
            var psi = new ProcessStartInfo(path) { UseShellExecute = true };
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) psi.WorkingDirectory = dir;
            Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // El usuario canceló (p. ej. UAC).
        }
        catch (Exception ex)
        {
            MessageBox.Show($"No se pudo abrir \"{ShellPaths.DisplayName(path)}\".\n\n{ex.Message}", "Clon Fences",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            var args = Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex) { Logger.Log(ex); }
    }

    public static bool TryRename(string path, string newName, out string newPath, out string? error)
    {
        newPath = path;
        error = null;
        newName = newName.Trim();
        if (newName.Length == 0 || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "El nombre no es válido. No puede contener: \\ / : * ? \" < > |";
            return false;
        }
        var dir = Path.GetDirectoryName(path);
        if (dir == null) { error = "No se puede renombrar este elemento."; return false; }
        var target = Path.Combine(dir, newName);
        if (string.Equals(target, path, StringComparison.Ordinal)) return true;
        try
        {
            bool caseOnly = string.Equals(target, path, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && Exists(target)) { error = "Ya existe un elemento con ese nombre."; return false; }
            if (Directory.Exists(path))
            {
                if (caseOnly)
                {
                    var tmp = path + ".~ren";
                    Directory.Move(path, tmp);
                    Directory.Move(tmp, target);
                }
                else Directory.Move(path, target);
            }
            else File.Move(path, target);
            newPath = target;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static IEnumerable<string> NonVirtual(IEnumerable<string> paths) => paths.Where(p => !ShellPaths.IsVirtual(p));
}
