using System.Windows;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Process;
using MessageBox = System.Windows.MessageBox;

namespace KioskWatchdog;

public partial class SettingsWindow : Window
{
    public ServiceSettingsConfig ServiceResult { get; private set; }
    public NotificationsConfig NotificationsResult { get; private set; }

    public SettingsWindow(ServiceSettingsConfig service, NotificationsConfig notifications)
    {
        InitializeComponent();
        ServiceResult = CloneService(service);
        NotificationsResult = CloneNotifications(notifications);
        LoadIntoForm(ServiceResult, NotificationsResult);
        RefreshServiceStatus();
    }

    private void LoadIntoForm(ServiceSettingsConfig service, NotificationsConfig notifications)
    {
        StartOnBootCheck.IsChecked = service.StartOnBoot;

        var webhook = notifications.Webhook;
        WebhookEnabledCheck.IsChecked = webhook.Enabled;
        WebhookUrlBox.Text = webhook.Url;
        WebhookTimeoutBox.Text = webhook.TimeoutSeconds.ToString();

        EventRestartLimitCheck.IsChecked = webhook.Events.RestartLimitReached;
        EventErrorCheck.IsChecked = webhook.Events.Error;
        EventRestartCheck.IsChecked = webhook.Events.Restart;
        EventUnhealthyCheck.IsChecked = webhook.Events.Unhealthy;
        EventRecoveredCheck.IsChecked = webhook.Events.Recovered;

        StatusReportEnabledCheck.IsChecked = webhook.StatusReport.Enabled;
        StatusReportIntervalBox.Text = webhook.StatusReport.IntervalMinutes.ToString();
    }

    private ServiceSettingsConfig ReadServiceFromForm()
        => new()
        {
            StartOnBoot = StartOnBootCheck.IsChecked == true
        };

    private NotificationsConfig ReadNotificationsFromForm()
        => new()
        {
            Webhook = new WebhookConfig
            {
                Enabled = WebhookEnabledCheck.IsChecked == true,
                Url = WebhookUrlBox.Text.Trim(),
                TimeoutSeconds = ParseInt(WebhookTimeoutBox.Text, 10),
                Events = new WebhookEventsConfig
                {
                    RestartLimitReached = EventRestartLimitCheck.IsChecked == true,
                    Error = EventErrorCheck.IsChecked == true,
                    Restart = EventRestartCheck.IsChecked == true,
                    Unhealthy = EventUnhealthyCheck.IsChecked == true,
                    Recovered = EventRecoveredCheck.IsChecked == true
                },
                StatusReport = new StatusReportConfig
                {
                    Enabled = StatusReportEnabledCheck.IsChecked == true,
                    IntervalMinutes = ParseInt(StatusReportIntervalBox.Text, 60)
                }
            }
        };

    private void RefreshServiceStatus()
    {
        if (!WatchdogServiceManager.TryGetIsRunning(out var running, out var error))
        {
            ServiceStatusText.Text = string.IsNullOrWhiteSpace(error)
                ? "Service: unavailable"
                : $"Service: unavailable ({error})";
            return;
        }

        var boot = WatchdogServiceManager.TryGetStartOnBoot(out var startOnBoot, out _)
            ? (startOnBoot ? ", auto-start" : ", manual start")
            : "";
        ServiceStatusText.Text = running
            ? $"Service: running{boot}"
            : $"Service: stopped{boot}";
    }

    private void StartService_Click(object sender, RoutedEventArgs e)
    {
        if (!WatchdogServiceManager.TryStart(out var error))
        {
            MessageBox.Show(
                error ?? "Could not start the KioskWatchdog service.",
                "Start service",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        RefreshServiceStatus();
    }

    private void StopService_Click(object sender, RoutedEventArgs e)
    {
        if (!WatchdogServiceManager.TryStop(out var error))
        {
            MessageBox.Show(
                error ?? "Could not stop the KioskWatchdog service.",
                "Stop service",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        RefreshServiceStatus();
    }

    private static ServiceSettingsConfig CloneService(ServiceSettingsConfig source)
        => new()
        {
            StartOnBoot = source.StartOnBoot
        };

    private static NotificationsConfig CloneNotifications(NotificationsConfig source)
        => new()
        {
            Webhook = new WebhookConfig
            {
                Enabled = source.Webhook.Enabled,
                Url = source.Webhook.Url,
                TimeoutSeconds = source.Webhook.TimeoutSeconds,
                Events = new WebhookEventsConfig
                {
                    RestartLimitReached = source.Webhook.Events.RestartLimitReached,
                    Error = source.Webhook.Events.Error,
                    Restart = source.Webhook.Events.Restart,
                    Unhealthy = source.Webhook.Events.Unhealthy,
                    Recovered = source.Webhook.Events.Recovered
                },
                StatusReport = new StatusReportConfig
                {
                    Enabled = source.Webhook.StatusReport.Enabled,
                    IntervalMinutes = source.Webhook.StatusReport.IntervalMinutes
                }
            }
        };

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text, out var value) ? value : fallback;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ServiceResult = ReadServiceFromForm();
        NotificationsResult = ReadNotificationsFromForm();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
