using System.Windows;

namespace KioskWatchdog;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Keep process alive when the main window is hidden to the tray.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
    }
}
