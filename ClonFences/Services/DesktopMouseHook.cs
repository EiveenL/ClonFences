using System;
using System.Runtime.InteropServices;
using System.Threading;
using ClonFences.Native;
using static ClonFences.Native.NativeMethods;

namespace ClonFences.Services;

/// <summary>
/// Hook global de ratón (WH_MOUSE_LL) en un hilo propio:
///  · doble clic sobre el escritorio → ocultar/mostrar fences;
///  · clic derecho + arrastrar sobre el escritorio → dibujar un fence nuevo.
/// Un clic derecho sin arrastre se reinyecta para que Explorer muestre su menú normal.
/// Los eventos se disparan en el hilo del hook: los suscriptores deben pasar al hilo de la UI.
/// </summary>
internal sealed class DesktopMouseHook : IDisposable
{
    private static readonly IntPtr Magic = new(0x46454E43); // "FENC": marca nuestros eventos inyectados
    private const int WM_APP_REPLAY = WM_APP + 1;
    private const int DragThreshold = 8;

    private readonly LowLevelMouseProc _proc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;

    private uint _lastClickTime;
    private POINT _lastClickPt;
    private bool _rDown, _dragging, _canceled;
    private POINT _rStart;

    public volatile bool QuickHideEnabled;
    public volatile bool RightDragEnabled;

    public event Action<POINT>? DoubleClicked;
    public event Action<POINT, POINT>? DragMoved;
    public event Action<POINT, POINT>? DragFinished;
    public event Action? DragCanceled;

    public DesktopMouseHook() => _proc = HookProc;

    public void Start()
    {
        if (_thread != null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) Logger.Log($"SetWindowsHookEx falló: {Marshal.GetLastWin32Error()}");
            ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_APP_REPLAY)
                {
                    ReplayRightClick();
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        { IsBackground = true, Name = "DesktopMouseHook" };
        _thread.Start();
        ready.Wait(3000);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (info.dwExtraInfo != Magic && Handle((int)wParam, info))
                    return new IntPtr(1); // tragar el evento
            }
            catch
            {
                // Nunca propagar excepciones desde un hook de bajo nivel.
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool Handle(int msg, MSLLHOOKSTRUCT info)
    {
        switch (msg)
        {
            case WM_LBUTTONDOWN:
                if (_rDown)
                {
                    // Clic izquierdo durante el arrastre: cancelar.
                    if (_dragging && !_canceled)
                    {
                        _canceled = true;
                        DragCanceled?.Invoke();
                    }
                    return true;
                }
                if (QuickHideEnabled && DesktopShell.IsDesktopWindow(WindowFromPoint(info.pt)))
                {
                    uint elapsed = info.time - _lastClickTime;
                    bool near = Math.Abs(info.pt.X - _lastClickPt.X) <= Math.Max(4, GetSystemMetrics(SM_CXDOUBLECLK) / 2) &&
                                Math.Abs(info.pt.Y - _lastClickPt.Y) <= Math.Max(4, GetSystemMetrics(SM_CYDOUBLECLK) / 2);
                    if (_lastClickTime != 0 && elapsed <= GetDoubleClickTime() && near)
                    {
                        _lastClickTime = 0;
                        DoubleClicked?.Invoke(info.pt);
                    }
                    else
                    {
                        _lastClickTime = info.time;
                        _lastClickPt = info.pt;
                    }
                }
                else _lastClickTime = 0;
                return false;

            case WM_RBUTTONDOWN:
                if (RightDragEnabled && DesktopShell.IsDesktopWindow(WindowFromPoint(info.pt)))
                {
                    _rDown = true;
                    _dragging = false;
                    _canceled = false;
                    _rStart = info.pt;
                    return true;
                }
                return false;

            case WM_MOUSEMOVE:
                if (_rDown && !_canceled)
                {
                    if (!_dragging && (Math.Abs(info.pt.X - _rStart.X) > DragThreshold || Math.Abs(info.pt.Y - _rStart.Y) > DragThreshold))
                        _dragging = true;
                    if (_dragging) DragMoved?.Invoke(_rStart, info.pt);
                }
                return false;

            case WM_RBUTTONUP:
                if (!_rDown) return false;
                _rDown = false;
                if (_canceled) { _canceled = false; _dragging = false; return true; }
                if (_dragging)
                {
                    _dragging = false;
                    DragFinished?.Invoke(_rStart, info.pt);
                }
                else
                {
                    // Fue un clic derecho normal: reproducirlo para Explorer después de salir del hook.
                    PostThreadMessage(_threadId, WM_APP_REPLAY, IntPtr.Zero, IntPtr.Zero);
                }
                return true;
        }
        return false;
    }

    private static void ReplayRightClick()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].U.mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTDOWN, dwExtraInfo = Magic };
        inputs[1].type = INPUT_MOUSE;
        inputs[1].U.mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_RIGHTUP, dwExtraInfo = Magic };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (_thread == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _thread = null;
    }
}
