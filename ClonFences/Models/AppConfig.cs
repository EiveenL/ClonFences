using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace ClonFences.Models;

public enum SortMode { Manual, Name, Type, Date, Size }

public sealed class FenceModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "Nuevo fence";

    // Posición y tamaño en píxeles físicos de pantalla (Height = altura desplegada).
    public int X { get; set; } = 40;
    public int Y { get; set; } = 40;
    public int Width { get; set; } = 360;
    public int Height { get; set; } = 240;

    public bool RolledUp { get; set; }
    public bool Locked { get; set; }

    // null = usar el valor predeterminado de la configuración
    public string? Color { get; set; }
    public double? Opacity { get; set; }
    public int? IconSize { get; set; }

    /// <summary>Si tiene valor, el fence es un "portal" que muestra el contenido de esta carpeta.</summary>
    public string? FolderPath { get; set; }

    /// <summary>Rutas de los elementos (fences normales), en orden manual.</summary>
    public List<string> Items { get; set; } = new();

    public SortMode Sort { get; set; } = SortMode.Manual;

    /// <summary>Reglas de clasificación automática: extensiones (".pdf") o "carpeta".</summary>
    public List<string> Rules { get; set; } = new();

    [JsonIgnore] public bool IsPortal => !string.IsNullOrWhiteSpace(FolderPath);
}

public sealed class AppSettings
{
    public bool QuickHide { get; set; } = true;
    public bool RightDragCreate { get; set; } = true;
    public bool HideDesktopIcons { get; set; }
    public bool AutoAddNewItems { get; set; } = true;
    public string? DefaultFenceId { get; set; }
    public bool SnapEnabled { get; set; } = true;
    public bool BlurBehind { get; set; } = true;
    public bool PeekRolledUp { get; set; } = true;
    public bool LockAll { get; set; }
    public int DefaultIconSize { get; set; } = 48;
    public string DefaultColor { get; set; } = "#1E1E1E";
    public double DefaultOpacity { get; set; } = 0.45;
    public bool FirstRun { get; set; } = true;
}

public sealed class LayoutSnapshot
{
    public string Name { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.Now;
    public List<FenceModel> Fences { get; set; } = new();
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<FenceModel> Fences { get; set; } = new();
    public List<LayoutSnapshot> Snapshots { get; set; } = new();

    public void Normalize()
    {
        Settings ??= new AppSettings();
        Fences ??= new List<FenceModel>();
        Snapshots ??= new List<LayoutSnapshot>();
        var ids = new HashSet<string>();
        foreach (var f in Fences)
        {
            f.Items ??= new List<string>();
            f.Rules ??= new List<string>();
            if (string.IsNullOrEmpty(f.Id) || !ids.Add(f.Id))
            {
                f.Id = Guid.NewGuid().ToString("N");
                ids.Add(f.Id);
            }
            f.Items = f.Items.Where(p => !string.IsNullOrWhiteSpace(p))
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            f.Width = Math.Max(f.Width, 120);
            f.Height = Math.Max(f.Height, 80);
        }
        Settings.DefaultIconSize = Math.Clamp(Settings.DefaultIconSize, 16, 256);
        Settings.DefaultOpacity = Math.Clamp(Settings.DefaultOpacity, 0, 1);
    }
}
