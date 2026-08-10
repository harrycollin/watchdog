using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Health;
using KioskWatchdog.Core.Ipc;
using KioskWatchdog.Core.Status;
using Microsoft.Win32;

namespace KioskWatchdog;

public partial class MainWindow : Window
{
    private readonly JsonConfigStore _configStore = new();
    private readonly CommandFileQueue _commandQueue = new();
    private readonly DispatcherTimer _refreshTimer;
    private WatchdogConfig _config;

    public MainWindow()
    {
        InitializeComponent();
        _config = _configStore.Load();
        LoadConfigIntoForm(_config);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();
        RefreshStatus();
    }

    private void LoadConfigIntoForm(WatchdogConfig config)
    {
        ExecutablePathBox.Text = config.Application.ExecutablePath;
        ArgumentsBox.Text = config.Application.Arguments;
        WorkingDirectoryBox.Text = config.Application.WorkingDirectory;
        DisplayNameBox.Text = config.Application.DisplayName;
        HealthUrlBox.Text = config.Health.Url;
        HealthEnabledBox.IsChecked = config.Health.Enabled;
        HealthIntervalBox.Text = config.Monitoring.HealthCheckIntervalSeconds.ToString();
        HealthTimeoutBox.Text = config.Monitoring.HealthTimeoutSeconds.ToString();
        ProcessIntervalBox.Text = config.Monitoring.ProcessCheckIntervalSeconds.ToString();
        RestartDelayBox.Text = config.Restart.RestartDelaySeconds.ToString();
        MaxRestartsBox.Text = config.Restart.MaxRestarts.ToString();
        RestartWindowBox.Text = config.Restart.RestartWindowMinutes.ToString();

        foreach (System.Windows.Controls.ComboBoxItem item in LaunchModeBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), config.Launch.Mode.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                LaunchModeBox.SelectedItem = item;
                break;
            }
        }
    }

    private WatchdogConfig ReadConfigFromForm()
    {
        var modeTag = (LaunchModeBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString()
                      ?? "Interactive";

        return new WatchdogConfig
        {
            Application =
            {
                ExecutablePath = ExecutablePathBox.Text.Trim(),
                Arguments = ArgumentsBox.Text,
                WorkingDirectory = WorkingDirectoryBox.Text.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text) ? "Kiosk Application" : DisplayNameBox.Text.Trim()
            },
            Monitoring =
            {
                ProcessCheckIntervalSeconds = ParseInt(ProcessIntervalBox.Text, 5),
                HealthCheckIntervalSeconds = ParseInt(HealthIntervalBox.Text, 10),
                HealthTimeoutSeconds = ParseInt(HealthTimeoutBox.Text, 45),
                GracefulTerminationTimeoutSeconds = _config.Monitoring.GracefulTerminationTimeoutSeconds
            },
            Restart =
            {
                RestartOnExit = true,
                RestartOnUnhealthy = true,
                RestartDelaySeconds = ParseInt(RestartDelayBox.Text, 5),
                MaxRestarts = ParseInt(MaxRestartsBox.Text, 5),
                RestartWindowMinutes = ParseInt(RestartWindowBox.Text, 10)
            },
            Health =
            {
                Enabled = HealthEnabledBox.IsChecked == true,
                Type = "http",
                Url = HealthUrlBox.Text.Trim()
            },
            Launch =
            {
                Mode = Enum.TryParse<LaunchMode>(modeTag, true, out var mode) ? mode : LaunchMode.Interactive
            }
        };
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text, out var value) ? value : fallback;

    private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select application executable",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (!string.IsNullOrWhiteSpace(ExecutablePathBox.Text))
        {
            try
            {
                dialog.InitialDirectory = Path.GetDirectoryName(ExecutablePathBox.Text);
                dialog.FileName = Path.GetFileName(ExecutablePathBox.Text);
            }
            catch
            {
                // ignore bad path
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            ExecutablePathBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text))
                WorkingDirectoryBox.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(DisplayNameBox.Text) || DisplayNameBox.Text == "Kiosk Application")
                DisplayNameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private void BrowseWorkingDirectory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select working directory"
        };

        if (!string.IsNullOrWhiteSpace(WorkingDirectoryBox.Text) && Directory.Exists(WorkingDirectoryBox.Text))
            dialog.InitialDirectory = WorkingDirectoryBox.Text;
        else if (!string.IsNullOrWhiteSpace(ExecutablePathBox.Text))
        {
            try { dialog.InitialDirectory = Path.GetDirectoryName(ExecutablePathBox.Text); }
            catch { /* ignore */ }
        }

        if (dialog.ShowDialog(this) == true)
            WorkingDirectoryBox.Text = dialog.FolderName;
    }

    private void RefreshStatus()
    {
        var status = StatusFilePublisher.Read() ?? new WatchdogStatus
        {
            ApplicationName = DisplayNameBox.Text,
            Status = ApplicationStatus.Unknown,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        AppNameText.Text = status.ApplicationName;
        StatusText.Text = status.Status.ToString().ToUpperInvariant();
        PidText.Text = status.ProcessId?.ToString() ?? "—";
        UptimeText.Text = status.Uptime is TimeSpan uptime
            ? uptime.ToString(@"hh\:mm\:ss")
            : "—";
        LastHealthText.Text = HealthEnabledBox.IsChecked == true
            ? FormatAgo(status.LastHealthCheckAt)
            : "disabled";
        LastRestartText.Text = FormatAgo(status.LastRestartAt);
        RestartCountText.Text = status.RestartCount.ToString();
        LastErrorText.Text = string.IsNullOrWhiteSpace(status.LastError) ? "—" : status.LastError;

        StatusDot.Fill = status.Status switch
        {
            ApplicationStatus.Running => new SolidColorBrush(Color.FromRgb(0x08, 0x7F, 0x5B)),
            ApplicationStatus.Unhealthy => new SolidColorBrush(Color.FromRgb(0xB5, 0x47, 0x08)),
            ApplicationStatus.RestartLimitReached => new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18)),
            ApplicationStatus.Error => new SolidColorBrush(Color.FromRgb(0xB4, 0x23, 0x18)),
            ApplicationStatus.NotConfigured => new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
            ApplicationStatus.Stopped => new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
            ApplicationStatus.Starting or ApplicationStatus.Restarting => new SolidColorBrush(Color.FromRgb(0x17, 0x6B, 0xA0)),
            _ => new SolidColorBrush(Colors.Gray)
        };
    }

    private static string FormatAgo(DateTimeOffset? value)
    {
        if (value is null)
            return "—";

        var elapsed = DateTimeOffset.UtcNow - value.Value;
        if (elapsed < TimeSpan.FromSeconds(1))
            return "just now";
        if (elapsed < TimeSpan.FromMinutes(1))
            return $"{(int)elapsed.TotalSeconds} seconds ago";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes} minutes ago";
        return $"{(int)elapsed.TotalHours} hours ago";
    }

    private void Start_Click(object sender, RoutedEventArgs e)
        => _commandQueue.Enqueue(WatchdogCommandType.Start);

    private void Stop_Click(object sender, RoutedEventArgs e)
        => _commandQueue.Enqueue(WatchdogCommandType.Stop);

    private void Restart_Click(object sender, RoutedEventArgs e)
        => _commandQueue.Enqueue(WatchdogCommandType.Restart);

    private void ResetCounter_Click(object sender, RoutedEventArgs e)
        => _commandQueue.Enqueue(WatchdogCommandType.ResetRestartCounter);

    private void SaveConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = ReadConfigFromForm();
            var validation = ConfigValidator.Validate(config);
            if (!validation.IsValid)
            {
                MessageBox.Show(
                    string.Join(Environment.NewLine, validation.Errors),
                    "Invalid configuration",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _configStore.Save(config);
            _config = config;
            MessageBox.Show(
                "Configuration saved. The watchdog service will reload it automatically.",
                "Saved",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TestHealth_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var url = HealthUrlBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(url))
            {
                MessageBox.Show(
                    "Enter a localhost health URL first (e.g. http://127.0.0.1:3000/health).",
                    "Health check",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var checker = new HttpHealthChecker(http);
            var result = await checker.CheckAsync(url).ConfigureAwait(true);

            MessageBox.Show(
                $"{result.Status}: {result.Message} (HTTP {result.HttpStatusCode?.ToString() ?? "n/a"})",
                "Health check",
                MessageBoxButton.OK,
                result.IsSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Health check failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var logs = WatchdogConfig.DefaultLogsDirectory;
        Directory.CreateDirectory(logs);
        Process.Start(new ProcessStartInfo
        {
            FileName = logs,
            UseShellExecute = true
        });
    }
}
