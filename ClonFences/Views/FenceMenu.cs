using System;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ClonFences.Models;
using ClonFences.Services;

namespace ClonFences.Views;

/// <summary>Menú contextual de un fence (clic derecho en el título o en una zona vacía).</summary>
internal static class FenceMenu
{
    public static ContextMenu Build(FenceWindow w, FenceManager mgr)
    {
        var model = w.Model;
        var s = mgr.Settings;
        var m = new ContextMenu();

        m.Items.Add(Item("Renombrar fence", w.BeginRename));

        if (!w.IsPortal)
        {
            m.Items.Add(Item("Agregar archivos…", () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Agregar al fence" };
                if (dlg.ShowDialog() == true) w.AddFiles(dlg.FileNames);
            }));
            var sys = new MenuItem { Header = "Agregar elemento del sistema" };
            foreach (var kv in ShellPaths.VirtualItems)
            {
                var key = kv.Key;
                sys.Items.Add(Item(kv.Value, () => w.AddFiles(new[] { key })));
            }
            m.Items.Add(sys);
        }
        else
        {
            m.Items.Add(Item("Abrir carpeta en el Explorador", () => FileOps.Open(w.CurrentFolder ?? model.FolderPath!)));
            if (w.CurrentFolder != null && !ShellPaths.Equal(w.CurrentFolder, model.FolderPath))
                m.Items.Add(Item("Volver a la carpeta raíz", w.ResetPortal));
            m.Items.Add(Item("Cambiar carpeta…", () =>
            {
                var f = mgr.PickFolder();
                if (f != null) mgr.ConvertToPortal(w, f);
            }));
        }

        m.Items.Add(new Separator());

        var sort = new MenuItem { Header = "Ordenar por" };
        foreach (var (mode, label) in new[]
                 {
                     (SortMode.Manual, "Manual"), (SortMode.Name, "Nombre"), (SortMode.Type, "Tipo"),
                     (SortMode.Date, "Fecha de modificación"), (SortMode.Size, "Tamaño"),
                 })
        {
            var mm = mode;
            sort.Items.Add(Check(label, model.Sort == mm, () => { model.Sort = mm; w.Reload(); mgr.SaveSoon(); }));
        }
        m.Items.Add(sort);

        var icons = new MenuItem { Header = "Tamaño de iconos" };
        icons.Items.Add(Check($"Predeterminado ({s.DefaultIconSize}px)", model.IconSize == null,
            () => { model.IconSize = null; w.ApplyAppearance(); mgr.SaveSoon(); }));
        foreach (var (size, label) in new[] { (32, "Pequeños"), (48, "Medianos"), (64, "Grandes"), (96, "Muy grandes") })
        {
            var sz = size;
            icons.Items.Add(Check(label, model.IconSize == sz, () => { model.IconSize = sz; w.ApplyAppearance(); mgr.SaveSoon(); }));
        }
        m.Items.Add(icons);

        var color = new MenuItem { Header = "Color" };
        color.Items.Add(Check("Predeterminado", model.Color == null, () => { model.Color = null; w.ApplyAppearance(false); mgr.SaveSoon(); }));
        foreach (var (name, hex) in ColorUtil.Presets)
        {
            var h = hex;
            var mi = Check(name, string.Equals(model.Color, h, StringComparison.OrdinalIgnoreCase),
                () => { model.Color = h; w.ApplyAppearance(false); mgr.SaveSoon(); });
            mi.Icon = Swatch(h);
            color.Items.Add(mi);
        }
        color.Items.Add(new Separator());
        color.Items.Add(Item("Personalizado…", () =>
        {
            var picked = ColorUtil.PickColor(model.Color ?? s.DefaultColor);
            if (picked != null) { model.Color = picked; w.ApplyAppearance(false); mgr.SaveSoon(); }
        }));
        m.Items.Add(color);

        var opacity = new MenuItem { Header = "Opacidad del fondo" };
        opacity.Items.Add(Check($"Predeterminada ({Math.Round(s.DefaultOpacity * 100)}%)", model.Opacity == null,
            () => { model.Opacity = null; w.ApplyAppearance(false); mgr.SaveSoon(); }));
        foreach (var pct in new[] { 0, 15, 30, 45, 60, 75, 90, 100 })
        {
            var v = pct / 100.0;
            opacity.Items.Add(Check($"{pct}%", model.Opacity is { } o && Math.Abs(o - v) < 0.001,
                () => { model.Opacity = v; w.ApplyAppearance(false); mgr.SaveSoon(); }));
        }
        m.Items.Add(opacity);

        m.Items.Add(new Separator());

        if (!w.IsPortal)
        {
            m.Items.Add(Item("Reglas de clasificación…", () => EditRules(w, mgr)));
            bool isDefault = s.DefaultFenceId == model.Id;
            m.Items.Add(Check("Destino de los elementos nuevos del escritorio", isDefault, () =>
            {
                s.DefaultFenceId = isDefault ? null : model.Id;
                mgr.SaveSoon();
            }));
        }
        m.Items.Add(Check("Enrollado (doble clic en el título)", model.RolledUp, w.ToggleRollup));
        m.Items.Add(Check("Bloquear posición y tamaño", model.Locked, () =>
        {
            model.Locked = !model.Locked;
            w.RefreshLockState();
            mgr.SaveSoon();
        }));

        m.Items.Add(new Separator());

        if (w.IsPortal)
            m.Items.Add(Item("Convertir en fence normal", w.ConvertToStandard));
        else
            m.Items.Add(Item("Convertir en portal de carpeta…", () =>
            {
                var f = mgr.PickFolder();
                if (f != null) mgr.ConvertToPortal(w, f);
            }));
        m.Items.Add(Item("Nuevo fence", () => mgr.CreateFenceNear(w)));
        m.Items.Add(Item("Eliminar fence…", () => mgr.ConfirmDeleteFence(w)));

        m.Items.Add(new Separator());
        m.Items.Add(Item("Configuración de Clon Fences…", mgr.ShowSettings));
        return m;
    }

    private static void EditRules(FenceWindow w, FenceManager mgr)
    {
        var current = string.Join(" ", w.Model.Rules);
        var answer = InputDialog.Ask("Reglas de clasificación",
            "Los elementos nuevos del escritorio que coincidan irán a este fence.\n" +
            "Escribe extensiones separadas por espacios (p. ej. .pdf .docx .xlsx).\n" +
            "Usa la palabra «carpeta» para las carpetas.", current);
        if (answer == null) return;
        w.Model.Rules = answer
            .Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Select(t => t == "carpeta" || t == "carpetas" ? "carpeta" : (t.StartsWith('.') ? t : "." + t))
            .Distinct()
            .ToList();
        mgr.SaveSoon();
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static MenuItem Check(string header, bool isChecked, Action onClick)
    {
        var mi = new MenuItem { Header = header, IsCheckable = false, IsChecked = isChecked };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static Rectangle Swatch(string hex)
    {
        var c = ColorUtil.Parse(hex, Colors.Black);
        return new Rectangle
        {
            Width = 14,
            Height = 14,
            RadiusX = 3,
            RadiusY = 3,
            Fill = new SolidColorBrush(c),
            Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0, 0, 0)),
            StrokeThickness = 1,
        };
    }
}
