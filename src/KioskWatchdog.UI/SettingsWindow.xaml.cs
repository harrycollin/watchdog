using System.Reflection;
using System.Windows;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Process;
using KioskWatchdog.Core.Updates;
using MessageBox = System.Windows.MessageBox;

namespace KioskWatchdog;

public partial class SettingsWindow : Window
{
    private readonly Version _currentVersion;
    private UpdateCheckResult? _pendingUpdate;
    private bool _updateBusy;

    public ServiceSettingsConfig ServiceResult { get; private set; }
    public NotificationsConfig NotificationsResult { get; private set; }
    public UpdatesConfig UpdatesResult { get; private set; }

    public SettingsWindow(
        ServiceSettingsConfig service,
        NotificationsConfig notifications,
        UpdatesConfig updates)
    {
        InitializeComponent();
        _currentVersion = UpdateVersion.FromAssembly(Assembly.GetExecutingAssembly());
        ServiceResult = CloneService(service);
        NotificationsResult = CloneNotifications(notifications);
        UpdatesResult = CloneUpdates(updates);
        LoadIntoForm(ServiceResult, NotificationsResult, UpdatesResult);
        RefreshServiceStatus();
        CurrentVersionText.Text = $"Installed version: {_currentVersion}";
    }

    private void LoadIntoForm(
        ServiceSettingsConfig service,
        NotificationsConfig notifications,
        UpdatesConfig updates)
    {
        StartOnBootCheck.IsChecked = service.StartOnBoot;
        CheckOnStartupCheck.IsChecked = updates.CheckOnStartup;

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

    private UpdatesConfig ReadUpdatesFromForm()
        => new()
        {
            CheckOnStartup = CheckOnStartupCheck.IsChecked == true,
            GitHubRepository = string.IsNullOrWhiteSpace(UpdatesResult.GitHubRepository)
                ? UpdatesConfig.DefaultGitHubRepository
                : UpdatesResult.GitHubRepository
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

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy)
            return;

        _updateBusy = true;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking GitHub Releases…";

        try
        {
            var repo = ReadUpdatesFromForm().GitHubRepository;
            using var client = new GitHubUpdateClient(repo);
            var result = await client.CheckForUpdateAsync(_currentVersion).ConfigureAwait(true);
            _pendingUpdate = result;

            if (result.UpdateAvailable)
            {
                UpdateStatusText.Text = $"Update available: {result.LatestVersion} ({result.TagName}).";
                InstallUpdateButton.IsEnabled = true;
            }
            else
            {
                UpdateStatusText.Text = $"Up to date ({result.LatestVersion}).";
            }
        }
        catch (Exception ex)
        {
            _pendingUpdate = null;
            UpdateStatusText.Text = "Check failed.";
            MessageBox.Show(
                "Could not check for updates:\n" + ex.Message,
                "Updates",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _updateBusy = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || _pendingUpdate is null || !_pendingUpdate.UpdateAvailable)
            return;

        var confirm = MessageBox.Show(
            $"Download and install Kiosk Watchdog {_pendingUpdate.LatestVersion}?\n\n" +
            "Windows may ask for administrator approval. The UI will close so Setup can replace files.",
            "Install update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _updateBusy = true;
        CheckUpdatesButton.IsEnabled = false;
        InstallUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Downloading installer…";

        try
        {
            var repo = ReadUpdatesFromForm().GitHubRepository;
            using var client = new GitHubUpdateClient(repo);
            var progress = new Progress<double>(p =>
            {
                UpdateStatusText.Text = $"Downloading installer… {p:P0}";
            });

            var setupPath = await client.DownloadSetupAsync(
                    _pendingUpdate.DownloadUrl,
                    _pendingUpdate.SetupFileName,
                    progress)
                .ConfigureAwait(true);

            UpdateStatusText.Text = "Starting installer…";
            if (!UpdateInstaller.TryLaunch(setupPath, out var error))
            {
                MessageBox.Show(
                    error ?? "Could not start the installer.",
                    "Install update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                UpdateStatusText.Text = "Install cancelled or failed.";
                InstallUpdateButton.IsEnabled = true;
                return;
            }

            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Download failed.";
            MessageBox.Show(
                "Could not download the update:\n" + ex.Message,
                "Install update",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            InstallUpdateButton.IsEnabled = true;
        }
        finally
        {
            _updateBusy = false;
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private static ServiceSettingsConfig CloneService(ServiceSettingsConfig source)
        => new()
        {
            StartOnBoot = source.StartOnBoot
        };

    private static UpdatesConfig CloneUpdates(UpdatesConfig source)
        => new()
        {
            CheckOnStartup = source.CheckOnStartup,
            GitHubRepository = string.IsNullOrWhiteSpace(source.GitHubRepository)
                ? UpdatesConfig.DefaultGitHubRepository
                : source.GitHubRepository.Trim()
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
        UpdatesResult = ReadUpdatesFromForm();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
