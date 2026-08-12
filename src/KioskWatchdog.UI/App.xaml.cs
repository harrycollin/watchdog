using System.Windows;

namespace KioskWatchdog;

public partial class App : System.Windows.Application
{
    internal SingleInstanceGuard? SingleInstance { get; set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep process alive when the main window is hidden to the tray.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        SingleInstance?.StartListening(() =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (MainWindow is MainWindow window)
                    window.BringToForeground();
            });
        });
    }
}
