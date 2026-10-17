using System;
using System.Drawing;
using System.Linq;
using ClonFences.Views;
using WinForms = System.Windows.Forms;

namespace ClonFences.Services;

/// <summary>Icono y menú en el área de notificación.</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly FenceManager _mgr;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Action _exit;

    public TrayIcon(FenceManager mgr, Action exit)
    {
        _mgr = mgr;
        _exit = exit;
        _menu = new WinForms.ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        _menu.Opening += (_, _) => Rebuild();
        Rebuild();

        _icon = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Clon Fences",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) _mgr.ToggleAllFences();
        };
        _mgr.Notify += ShowBalloon;
    }

    private static Icon LoadIcon()
    {
        try
        {
            using var s = typeof(TrayIcon).Assembly.GetManifestResourceStream("ClonFences.app.ico");
            if (s != null) return new Icon(s, WinForms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex) { Logger.Log(ex); }
        return SystemIcons.Application;
    }

    public void ShowBalloon(string title, string text) =>
        _icon.ShowBalloonTip(6000, title, text, WinForms.ToolTipIcon.Info);

    private void Rebuild()
    {
        var s = _mgr.Settings;
        _menu.Items.Clear();

        Add("Nuevo fence", _mgr.CreateFenceAtCursor).Font = new Font(_menu.Font, FontStyle.Bold);
        Add("Nuevo portal de carpeta…", _mgr.CreatePortalInteractive);
        Add("Organizar el escritorio automáticamente", _mgr.Organize);
        _menu.Items.Add(new WinForms.ToolStripSeparator());

        Add(_mgr.FencesHidden ? "Mostrar los fences" : "Ocultar los fences", _mgr.ToggleAllFences);
        Check("Ocultar los iconos de Windows del escritorio", s.HideDesktopIcons, () => _mgr.SetHideDesktopIcons(!s.HideDesktopIcons));
        Check("Bloquear todos los fences", s.LockAll, () => _mgr.SetLockAll(!s.LockAll));

        var snaps = new WinForms.ToolStripMenuItem("Instantáneas del diseño");
        snaps.DropDownItems.Add(new WinForms.ToolStripMenuItem("Guardar diseño actual…", null, (_, _) =>
        {
            var name = InputDialog.Ask("Guardar instantánea", "Nombre de la instantánea:", $"Diseño {DateTime.Now:dd/MM/yyyy HH:mm}");
            if (!string.IsNullOrWhiteSpace(name)) _mgr.SaveSnapshot(name.Trim());
        }));
        if (_mgr.Config.Snapshots.Count > 0) snaps.DropDownItems.Add(new WinForms.ToolStripSeparator());
        foreach (var snap in _mgr.Config.Snapshots.OrderByDescending(x => x.Created).ToList())
        {
            var item = new WinForms.ToolStripMenuItem($"{snap.Name}   ({snap.Fences.Count} fences)");
            item.DropDownItems.Add(new WinForms.ToolStripMenuItem("Restaurar", null, (_, _) =>
            {
                if (System.Windows.MessageBox.Show($"¿Restaurar «{snap.Name}»? El diseño actual se reemplazará.",
                        "Clon Fences", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
                    == System.Windows.MessageBoxResult.Yes)
                    _mgr.RestoreSnapshot(snap);
            }));
            item.DropDownItems.Add(new WinForms.ToolStripMenuItem("Eliminar", null, (_, _) => _mgr.DeleteSnapshot(snap)));
            snaps.DropDownItems.Add(item);
        }
        _menu.Items.Add(snaps);
        _menu.Items.Add(new WinForms.ToolStripSeparator());

        Add("Configuración…", _mgr.ShowSettings);
        Check("Iniciar con Windows", StartupService.IsEnabled(), () => StartupService.SetEnabled(!StartupService.IsEnabled()));
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        Add("Salir", _exit);
    }

    private WinForms.ToolStripMenuItem Add(string text, Action action)
    {
        var item = new WinForms.ToolStripMenuItem(text);
        item.Click += (_, _) => Safe(action);
        _menu.Items.Add(item);
        return item;
    }

    private void Check(string text, bool isChecked, Action toggle)
    {
        var item = new WinForms.ToolStripMenuItem(text) { Checked = isChecked };
        item.Click += (_, _) => Safe(toggle);
        _menu.Items.Add(item);
    }

    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex) { Logger.Log(ex); }
    }

    public void Dispose()
    {
        _mgr.Notify -= ShowBalloon;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
