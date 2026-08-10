using System.Drawing;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace KioskWatchdog;

/// <summary>
/// System tray icon for the configuration UI. Close-to-tray; Exit from the menu quits.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Action _showWindow;
    private readonly Action _exitApp;
    private bool _disposed;

    public TrayIconService(Action showWindow, Action exitApp)
    {
        _showWindow = showWindow;
        _exitApp = exitApp;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Watchdog", null, (_, _) => _showWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _exitApp());

        _notifyIcon = new NotifyIcon
        {
            Text = "Kiosk Watchdog",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = LoadIcon()
        };
        _notifyIcon.DoubleClick += (_, _) => _showWindow();
    }

    public void UpdateTooltip(string text)
    {
        if (_disposed)
            return;

        // NotifyIcon.Text max length is 63 characters.
        var trimmed = text.Length <= 63 ? text : text[..60] + "...";
        _notifyIcon.Text = trimmed;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }

    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo?.Stream is not null)
                return new Icon(streamInfo.Stream);
        }
        catch
        {
            // Fall through to system icon.
        }

        return SystemIcons.Application;
    }
}
