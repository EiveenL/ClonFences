using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ClonFences.Services;

namespace ClonFences;

public partial class App : Application
{
    private Mutex? _mutex;
    private FenceManager? _manager;
    private TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\ClonFences.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("Clon Fences ya se está ejecutando.\nBusca su icono en el área de notificación.",
                "Clon Fences", MessageBoxButton.OK, MessageBoxImage.Information);
            _mutex = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Logger.Log(a.ExceptionObject?.ToString() ?? "Error desconocido");

        var config = ConfigService.Load();
        if (e.Args.Contains("--reset", StringComparer.OrdinalIgnoreCase))
        {
            config = new Models.AppConfig();
            config.Normalize();
        }

        _manager = new FenceManager(config);
        _tray = new TrayIcon(_manager, ExitApp);
        _manager.Start();

        if (config.Settings.FirstRun)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _manager.RunFirstRunWizard());

        Logger.Log($"Clon Fences iniciado con {config.Fences.Count} fences.");
    }

    private void ExitApp()
    {
        _manager?.Shutdown();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _manager?.Shutdown();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _manager?.Shutdown();
        _tray?.Dispose();
        if (_mutex != null)
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Logger.Log(e.Exception);
        e.Handled = true; // un error puntual no debe cerrar todos los fences
    }
}
