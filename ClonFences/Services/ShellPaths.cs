using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace ClonFences.Services;

/// <summary>Utilidades de rutas: elementos virtuales del sistema, nombres visibles, existencia.</summary>
internal static class ShellPaths
{
    public static readonly IReadOnlyDictionary<string, string> VirtualItems =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["::{645FF040-5081-101B-9F08-00AA002F954E}"] = "Papelera de reciclaje",
            ["::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"] = "Este equipo",
            ["::{59031A47-3F72-44A7-89C5-5595FE6B30EE}"] = "Carpeta de usuario",
            ["::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}"] = "Red",
            ["::{26EE0668-A00A-44D7-9371-BEB064C98683}"] = "Panel de control",
        };

    public const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    public const string ThisPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    private static readonly HashSet<string> AlwaysHiddenExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".lnk", ".url", ".appref-ms", ".pif" };

    private static readonly Lazy<bool> HideFileExt = new(() =>
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("HideFileExt") is not int v || v != 0;
        }
        catch { return true; }
    });

    public static bool IsVirtual(string path) => path.StartsWith("::", StringComparison.Ordinal);

    public static bool Exists(string path) =>
        IsVirtual(path) || File.Exists(path) || Directory.Exists(path);

    /// <summary>Un elemento que falta se elimina del fence sólo si su carpeta sigue existiendo
    /// (así no se pierden elementos de unidades desconectadas).</summary>
    public static bool ShouldPrune(string path)
    {
        if (IsVirtual(path)) return false;
        try
        {
            var dir = Path.GetDirectoryName(path);
            return dir == null || Directory.Exists(dir);
        }
        catch { return true; }
    }

    public static bool HidesExtension(string path) =>
        !Directory.Exists(path) && (AlwaysHiddenExtensions.Contains(Path.GetExtension(path)) || HideFileExt.Value);

    public static string DisplayName(string path)
    {
        if (IsVirtual(path))
            return VirtualItems.TryGetValue(path, out var n) ? n : path;

        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name)) return path; // raíz de unidad
        if (Directory.Exists(path)) return name;
        return HidesExtension(path) ? Path.GetFileNameWithoutExtension(name) : name;
    }

    public static bool Equal(string? a, string? b) =>
        a != null && b != null &&
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static bool SameRoot(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsHiddenOrSystem(string path)
    {
        try
        {
            if (IsVirtual(path)) return false;
            var attr = File.GetAttributes(path);
            return (attr & (FileAttributes.Hidden | FileAttributes.System)) != 0 ||
                   Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    public static IEnumerable<string> SafeEnumerate(string dir)
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(dir); }
        catch { yield break; }

        using var e = entries.GetEnumerator();
        while (true)
        {
            string current;
            try
            {
                if (!e.MoveNext()) yield break;
                current = e.Current;
            }
            catch { yield break; }
            if (!IsHiddenOrSystem(current)) yield return current;
        }
    }
}
