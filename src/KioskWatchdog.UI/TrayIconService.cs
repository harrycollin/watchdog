using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using KioskWatchdog.Core.Ipc;
using KioskWatchdog.Core.Status;
using Application = System.Windows.Application;

namespace KioskWatchdog;

internal readonly record struct TrayAppEntry(
    string Id,
    string Name,
    ApplicationStatus Status,
    bool Enabled);

/// <summary>
/// System tray icon with a live control menu. Uses the native NotifyIcon popup path
/// (SetForegroundWindow) so the menu can appear above fullscreen apps.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    private static readonly MethodInfo? ShowContextMenuMethod =
        typeof(NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Action _showWindow;
    private readonly Action _exitApp;
    private readonly Action<WatchdogCommandType, string?> _enqueueCommand;
    private readonly Func<IReadOnlyList<TrayAppEntry>> _getApps;
    private bool _disposed;

    public TrayIconService(
        Action showWindow,
        Action exitApp,
        Action<WatchdogCommandType, string?> enqueueCommand,
        Func<IReadOnlyList<TrayAppEntry>> getApps)
    {
        _showWindow = showWindow;
        _exitApp = exitApp;
        _enqueueCommand = enqueueCommand;
        _getApps = getApps;

        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false
        };
        _menu.Opening += (_, _) => RebuildMenu();

        _notifyIcon = new NotifyIcon
        {
            Text = "Kiosk Watchdog",
            Visible = true,
            ContextMenuStrip = _menu,
            Icon = LoadIcon()
        };

        // Left-click: same native ShowContextMenu path as right-click (stays above fullscreen).
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                ShowNativeContextMenu();
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
        _menu.Dispose();
    }

    private void ShowNativeContextMenu()
    {
        if (_disposed || ShowContextMenuMethod is null)
            return;

        ShowContextMenuMethod.Invoke(_notifyIcon, null);
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();

        var apps = _getApps() ?? Array.Empty<TrayAppEntry>();
        _menu.Items.Add(CreateHeader(apps));
        _menu.Items.Add(new ToolStripSeparator());

        if (apps.Count == 0)
        {
            _menu.Items.Add(DisabledItem("No applications configured"));
        }
        else if (apps.Count == 1)
        {
            AddAppActions(_menu.Items, apps[0], includeNameHeader: true);
        }
        else
        {
            foreach (var app in apps)
                _menu.Items.Add(CreateAppSubmenu(app));
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(
            "Reload config",
            null,
            (_, _) => _enqueueCommand(WatchdogCommandType.ReloadConfig, null)));
        _menu.Items.Add(new ToolStripMenuItem("Open Watchdog", null, (_, _) => _showWindow()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => _exitApp()));
    }

    private ToolStripMenuItem CreateAppSubmenu(TrayAppEntry app)
    {
        var label = app.Enabled
            ? $"{app.Name}  ·  {FormatStatus(app.Status)}"
            : $"{app.Name}  ·  disabled";

        var submenu = new ToolStripMenuItem(label)
        {
            Enabled = app.Enabled
        };

        if (app.Enabled)
            AddAppActions(submenu.DropDownItems, app, includeNameHeader: false);

        return submenu;
    }

    private void AddAppActions(ToolStripItemCollection items, TrayAppEntry app, bool includeNameHeader)
    {
        if (includeNameHeader)
        {
            items.Add(DisabledItem($"{app.Name}  ·  {FormatStatus(app.Status)}"));
            items.Add(new ToolStripSeparator());
        }

        var id = app.Id;
        items.Add(new ToolStripMenuItem(
            "Start",
            null,
            (_, _) => _enqueueCommand(WatchdogCommandType.Start, id)));
        items.Add(new ToolStripMenuItem(
            "Stop",
            null,
            (_, _) => _enqueueCommand(WatchdogCommandType.Stop, id)));
        items.Add(new ToolStripMenuItem(
            "Restart",
            null,
            (_, _) => _enqueueCommand(WatchdogCommandType.Restart, id)));
        items.Add(new ToolStripMenuItem(
            "Reset restart count",
            null,
            (_, _) => _enqueueCommand(WatchdogCommandType.ResetRestartCounter, id)));
    }

    private static ToolStripMenuItem CreateHeader(IReadOnlyList<TrayAppEntry> apps)
    {
        if (apps.Count == 0)
            return DisabledItem("Kiosk Watchdog — no status");

        if (apps.Count == 1)
            return DisabledItem($"Kiosk Watchdog — {FormatStatus(apps[0].Status)}");

        var running = apps.Count(a => a.Status == ApplicationStatus.Running);
        var issues = apps.Count(a =>
            a.Status is ApplicationStatus.Unhealthy
                or ApplicationStatus.Error
                or ApplicationStatus.RestartLimitReached);

        var text = $"Kiosk Watchdog — {running}/{apps.Count} running";
        if (issues > 0)
            text += $", {issues} issue(s)";

        return DisabledItem(text);
    }

    private static ToolStripMenuItem DisabledItem(string text)
        => new(text) { Enabled = false };

    private static string FormatStatus(ApplicationStatus status)
        => status switch
        {
            ApplicationStatus.OutsideSchedule => "Scheduled off",
            ApplicationStatus.RestartLimitReached => "Restart limit",
            ApplicationStatus.NotConfigured => "Not configured",
            _ => status.ToString()
        };

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
