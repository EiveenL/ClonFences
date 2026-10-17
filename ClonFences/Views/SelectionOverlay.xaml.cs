using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using ClonFences.Native;
using static ClonFences.Native.NativeMethods;

namespace ClonFences.Views;

/// <summary>Rectángulo que se dibuja mientras se arrastra con el botón derecho sobre el escritorio.</summary>
public partial class SelectionOverlay : Window
{
    private IntPtr _hwnd;

    public SelectionOverlay()
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = (long)GetWindowLongPtr(_hwnd, GWL_EXSTYLE);
            SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT));
        };
    }

    internal void ShowRect(RECT r)
    {
        if (!IsVisible) Show();
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, r.Left, r.Top, Math.Max(2, r.Width), Math.Max(2, r.Height),
            SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    internal void HideOverlay()
    {
        if (IsVisible) Hide();
    }

    /// <summary>Menú nativo con las opciones; devuelve el índice (1..n) elegido o 0.</summary>
    internal int ShowMenu(POINT at, IReadOnlyList<string?> options)
    {
        if (_hwnd == IntPtr.Zero) return 0;
        var menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i] == null) AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                else AppendMenu(menu, MF_STRING, (UIntPtr)(uint)(i + 1), options[i]);
            }
            ForceForeground(_hwnd);
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, at.X, at.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            return cmd;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }
}
