using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Health;
using KioskWatchdog.Core.Ipc;
using KioskWatchdog.Core.Process;
using KioskWatchdog.Core.Status;
using KioskWatchdog.Core.Updates;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace KioskWatchdog;

public partial class MainWindow : Window
{
    private readonly JsonConfigStore _configStore = new();
    private readonly CommandFileQueue _commandQueue = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly ObservableCollection<AppListItem> _apps = new();
    private readonly TrayIconService _tray;
    private ServiceSettingsConfig _service = new();
    private NotificationsConfig _notifications = new();
    private UpdatesConfig _updates = new();
    private bool _suppressSelectionEvents;
    private bool _allowClose;
    private bool _startupUpdateCheckStarted;
    private AppListItem? _selected;

    public MainWindow()
    {
        InitializeComponent();
        AppList.ItemsSource = _apps;
        VersionText.Text = $"v{UpdateVersion.FromAssembly(Assembly.GetExecutingAssembly())}";

        _tray = new TrayIconService(
            ShowFromTray,
            ExitFromTray,
            (type, appId) => _commandQueue.Enqueue(type, appId),
            GetTrayApps);

        var config = _configStore.Load();
        LoadAppsFromConfig(config);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        _refreshTimer.Start();
        RefreshStatus();

        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_startupUpdateCheckStarted || !_updates.CheckOnStartup)
            return;

        _startupUpdateCheckStarted = true;
        _ = CheckForUpdatesOnStartupAsync();
    }

    private void LoadAppsFromConfig(WatchdogConfig config)
    {
        _suppressSelectionEvents = true;
        try
        {
            _apps.Clear();

            if (config.Applications.Count == 0)
            {
                _apps.Add(AppListItem.CreateNew("default"));
            }
            else
            {
                foreach (var app in config.Applications)
                    _apps.Add(AppListItem.FromConfig(app));
            }

            _selected = _apps[0];
            AppList.SelectedItem = _selected;
            LoadSelectedIntoForm();
            _service = config.Service ?? new ServiceSettingsConfig();
            _notifications = config.Notifications;
            _updates = config.Updates ?? new UpdatesConfig();
        }
        finally
        {
            _suppressSelectionEvents = false;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_service, _notifications, _updates)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _service = dialog.ServiceResult;
            _notifications = dialog.NotificationsResult;
            _updates = dialog.UpdatesResult;
            FooterText.Text = "Settings updated. Save configuration to apply.";
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            var current = UpdateVersion.FromAssembly(Assembly.GetExecutingAssembly());
            using var client = new GitHubUpdateClient(_updates.GitHubRepository);
            var result = await client.CheckForUpdateAsync(current).ConfigureAwait(true);
            if (!result.UpdateAvailable)
                return;

            FooterText.Text = $"Update available: {result.LatestVersion}. Open Settings to install.";

            var answer = MessageBox.Show(
                $"Kiosk Watchdog {result.LatestVersion} is available (you have {result.CurrentVersion}).\n\n" +
                "Download and install now? Windows may ask for administrator approval.",
                "Update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer != MessageBoxResult.Yes)
                return;

            FooterText.Text = "Downloading update…";
            var setupPath = await client.DownloadSetupAsync(
                    result.DownloadUrl,
                    result.SetupFileName)
                .ConfigureAwait(true);

            if (!UpdateInstaller.TryLaunch(setupPath, out var error))
            {
                MessageBox.Show(
                    error ?? "Could not start the installer.",
                    "Update",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                FooterText.Text = "Update cancelled or failed.";
                return;
            }

            System.Windows.Application.Current.Shutdown();
        }
        catch
        {
            // Startup checks are best-effort; manual check lives in Settings.
        }
    }

    private void AppList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressSelectionEvents)
            return;

        PushFormToSelected();
        _selected = AppList.SelectedItem as AppListItem;
        LoadSelectedIntoForm();
        RefreshStatus();
    }

    private void LoadSelectedIntoForm()
    {
        if (_selected is null)
            return;

        AppIdBox.Text = _selected.Id;
        EnabledCheck.IsChecked = _selected.Enabled;
        DisplayNameBox.Text = _selected.DisplayName;
        ExecutableBox.Text = _selected.ExecutablePath;
        ArgumentsBox.Text = _selected.Arguments;
        WorkingDirBox.Text = _selected.WorkingDirectory;
        StartCommandBox.Text = _selected.StartCommand;
        StopCommandBox.Text = _selected.StopCommand;
        HttpWorkingDirBox.Text = string.IsNullOrWhiteSpace(_selected.HttpWorkingDirectory)
            ? _selected.WorkingDirectory
            : _selected.HttpWorkingDirectory;
        TcpHostBox.Text = string.IsNullOrWhiteSpace(_selected.TcpHost) ? "127.0.0.1" : _selected.TcpHost;
        TcpPortBox.Text = _selected.TcpPort > 0 ? _selected.TcpPort.ToString() : "";
        TcpStartCommandBox.Text = _selected.TcpStartCommand;
        TcpStopCommandBox.Text = _selected.TcpStopCommand;
        TcpWorkingDirBox.Text = _selected.TcpWorkingDirectory;
        ServiceNameBox.Text = _selected.ServiceName;
        ProcessIntervalBox.Text = _selected.ProcessCheckIntervalSeconds.ToString();
        HealthIntervalBox.Text = _selected.HealthCheckIntervalSeconds.ToString();
        HealthTimeoutBox.Text = _selected.HealthTimeoutSeconds.ToString();
        GracefulStopBox.Text = _selected.GracefulTerminationTimeoutSeconds.ToString();
        RestartDelayBox.Text = _selected.RestartDelaySeconds.ToString();
        MaxRestartsBox.Text = _selected.MaxRestarts.ToString();
        RestartWindowBox.Text = _selected.RestartWindowMinutes.ToString();
        HealthEnabledCheck.IsChecked = _selected.HealthEnabled;
        HealthUrlBox.Text = _selected.HealthUrl;
        ExpectedStatusBox.Text = _selected.ExpectedStatusCode.ToString();

        ScheduleEnabledCheck.IsChecked = _selected.ScheduleEnabled;
        ScheduleStartBox.Text = _selected.ScheduleStartTime;
        ScheduleEndBox.Text = _selected.ScheduleEndTime;
        DayMonCheck.IsChecked = _selected.ScheduleDays.Contains(DayOfWeek.Monday);
        DayTueCheck.IsChecked = _selected.ScheduleDays.Contains(DayOfWeek.Tuesday);
        DayWedCheck.IsChecked = _selected.ScheduleDays.Contains(DayOfWeek.Wednesday);
        DayThuCheck.IsChecked = _selected.ScheduleDays.Contains(DayOfWeek.Thursday);
        DayFriCheck.IsChecked = _selected.ScheduleDays.Contains(DayOfWeek.Friday);
        DaySatCheck.IsChecked = _selected.ScheduleDays.Contains(DayOfWeek.Saturday);
        DaySunCheck.IsChecked = _selected.ScheduleDays.Contains(DayOfWeek.Sunday);

        ResourcesEnabledCheck.IsChecked = _selected.ResourcesEnabled;
        ResourcesIncludeChildrenCheck.IsChecked = _selected.ResourcesIncludeChildren;
        MaxMemoryBox.Text = _selected.MaxMemoryMegabytes.ToString();
        MaxCpuBox.Text = _selected.MaxCpuPercent.ToString();
        BreachDurationBox.Text = _selected.BreachDurationSeconds.ToString();

        SelectKind(_selected.Kind);
        SelectLaunchMode(LaunchModeBox, _selected.LaunchMode);
        SelectLaunchMode(HttpLaunchModeBox, _selected.LaunchMode);
        ApplyKindUi();
    }

    private static void SelectLaunchMode(System.Windows.Controls.ComboBox box, LaunchMode mode)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in box.Items)
        {
            if (string.Equals(item.Tag?.ToString(), mode.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                break;
            }
        }
    }

    private void SelectKind(ApplicationKind kind)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in KindBox.Items)
        {
            if (string.Equals(item.Tag?.ToString(), kind.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                KindBox.SelectedItem = item;
                break;
            }
        }
    }

    private ApplicationKind SelectedKind()
    {
        var tag = (KindBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString() ?? "Process";
        return Enum.TryParse<ApplicationKind>(tag, true, out var kind) ? kind : ApplicationKind.Process;
    }

    private void KindBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _suppressSelectionEvents)
            return;

        ApplyKindUi();
    }

    private void ApplyKindUi()
    {
        var kind = SelectedKind();
        ProcessFieldsPanel.Visibility = kind == ApplicationKind.Process ? Visibility.Visible : Visibility.Collapsed;
        HttpFieldsPanel.Visibility = kind == ApplicationKind.Http ? Visibility.Visible : Visibility.Collapsed;
        TcpFieldsPanel.Visibility = kind == ApplicationKind.Tcp ? Visibility.Visible : Visibility.Collapsed;
        ServiceFieldsPanel.Visibility = kind == ApplicationKind.WindowsService ? Visibility.Visible : Visibility.Collapsed;
        HttpHealthHint.Visibility = kind == ApplicationKind.Http ? Visibility.Visible : Visibility.Collapsed;

        var httpHealthRelevant = kind is ApplicationKind.Process or ApplicationKind.Http;
        HealthEnabledCheck.Visibility = httpHealthRelevant ? Visibility.Visible : Visibility.Collapsed;
        HealthUrlBox.IsEnabled = httpHealthRelevant;
        ExpectedStatusBox.IsEnabled = httpHealthRelevant;

        if (kind == ApplicationKind.Http)
        {
            HealthEnabledCheck.IsChecked = true;
            HealthEnabledCheck.IsEnabled = false;
            HealthExpander.IsExpanded = true;
        }
        else
        {
            HealthEnabledCheck.IsEnabled = true;
        }
    }

    private void PushFormToSelected()
    {
        if (_selected is null)
            return;

        _selected.Id = string.IsNullOrWhiteSpace(AppIdBox.Text) ? _selected.Id : AppIdBox.Text.Trim();
        _selected.Enabled = EnabledCheck.IsChecked == true;
        _selected.Kind = SelectedKind();
        _selected.DisplayName = string.IsNullOrWhiteSpace(DisplayNameBox.Text)
            ? "Kiosk Application"
            : DisplayNameBox.Text.Trim();
        _selected.ExecutablePath = ExecutableBox.Text.Trim();
        _selected.Arguments = ArgumentsBox.Text;
        _selected.WorkingDirectory = WorkingDirBox.Text.Trim();
        _selected.StartCommand = StartCommandBox.Text.Trim();
        _selected.StopCommand = StopCommandBox.Text.Trim();
        _selected.HttpWorkingDirectory = HttpWorkingDirBox.Text.Trim();
        if (_selected.Kind == ApplicationKind.Http && !string.IsNullOrWhiteSpace(_selected.HttpWorkingDirectory))
            _selected.WorkingDirectory = _selected.HttpWorkingDirectory;

        _selected.TcpHost = TcpHostBox.Text.Trim();
        _selected.TcpPort = ParseInt(TcpPortBox.Text, 0);
        _selected.TcpStartCommand = TcpStartCommandBox.Text.Trim();
        _selected.TcpStopCommand = TcpStopCommandBox.Text.Trim();
        _selected.TcpWorkingDirectory = TcpWorkingDirBox.Text.Trim();
        _selected.ServiceName = ServiceNameBox.Text.Trim();

        _selected.ProcessCheckIntervalSeconds = ParseInt(ProcessIntervalBox.Text, 5);
        _selected.HealthCheckIntervalSeconds = ParseInt(HealthIntervalBox.Text, 10);
        _selected.HealthTimeoutSeconds = ParseInt(HealthTimeoutBox.Text, 45);
        _selected.GracefulTerminationTimeoutSeconds = ParseInt(GracefulStopBox.Text, 10);
        _selected.RestartDelaySeconds = ParseInt(RestartDelayBox.Text, 5);
        _selected.MaxRestarts = ParseInt(MaxRestartsBox.Text, 5);
        _selected.RestartWindowMinutes = ParseInt(RestartWindowBox.Text, 10);
        _selected.HealthEnabled = _selected.Kind == ApplicationKind.Http || HealthEnabledCheck.IsChecked == true;
        _selected.HealthUrl = HealthUrlBox.Text.Trim();
        _selected.ExpectedStatusCode = ParseInt(ExpectedStatusBox.Text, 200);

        _selected.ScheduleEnabled = ScheduleEnabledCheck.IsChecked == true;
        _selected.ScheduleStartTime = ScheduleStartBox.Text.Trim();
        _selected.ScheduleEndTime = ScheduleEndBox.Text.Trim();
        _selected.ScheduleDays = ReadScheduleDaysFromForm();

        _selected.ResourcesEnabled = ResourcesEnabledCheck.IsChecked == true;
        _selected.ResourcesIncludeChildren = ResourcesIncludeChildrenCheck.IsChecked == true;
        _selected.MaxMemoryMegabytes = ParseInt(MaxMemoryBox.Text, 0);
        _selected.MaxCpuPercent = ParseInt(MaxCpuBox.Text, 0);
        _selected.BreachDurationSeconds = ParseInt(BreachDurationBox.Text, 300);

        var launchBox = _selected.Kind == ApplicationKind.Http ? HttpLaunchModeBox : LaunchModeBox;
        var modeTag = (launchBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag?.ToString()
                      ?? "Interactive";
        _selected.LaunchMode = Enum.TryParse<LaunchMode>(modeTag, true, out var mode)
            ? mode
            : LaunchMode.Interactive;

        _selected.NotifyListLabelChanged();
    }

    private WatchdogConfig BuildConfigFromApps()
    {
        PushFormToSelected();
        var config = new WatchdogConfig
        {
            Service = new ServiceSettingsConfig
            {
                StartOnBoot = _service.StartOnBoot
            },
            Notifications = _notifications,
            Updates = _updates
        };
        foreach (var app in _apps)
            config.Applications.Add(app.ToConfig());
        return config;
    }

    private static int ParseInt(string text, int fallback)
        => int.TryParse(text, out var value) ? value : fallback;

    private List<DayOfWeek> ReadScheduleDaysFromForm()
    {
        var days = new List<DayOfWeek>();
        if (DayMonCheck.IsChecked == true) days.Add(DayOfWeek.Monday);
        if (DayTueCheck.IsChecked == true) days.Add(DayOfWeek.Tuesday);
        if (DayWedCheck.IsChecked == true) days.Add(DayOfWeek.Wednesday);
        if (DayThuCheck.IsChecked == true) days.Add(DayOfWeek.Thursday);
        if (DayFriCheck.IsChecked == true) days.Add(DayOfWeek.Friday);
        if (DaySatCheck.IsChecked == true) days.Add(DayOfWeek.Saturday);
        if (DaySunCheck.IsChecked == true) days.Add(DayOfWeek.Sunday);
        return days;
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        PushFormToSelected();
        var id = NextUniqueId("app");
        var item = AppListItem.CreateNew(id);
        _apps.Add(item);
        AppList.SelectedItem = item;
        FooterText.Text = $"Added application '{id}'.";
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;

        if (_apps.Count <= 1)
        {
            MessageBox.Show(
                "At least one application entry is required. Disable it instead of removing the last one.",
                "Remove application",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var remove = _selected;
        var label = string.IsNullOrWhiteSpace(remove.DisplayName) ? remove.Id : remove.DisplayName;
        var confirm = MessageBox.Show(
            $"Remove '{label}' ({remove.Id})?\n\nThis saves the configuration and stops monitoring that application.",
            "Remove application",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes)
            return;

        var index = _apps.IndexOf(remove);
        _suppressSelectionEvents = true;
        try
        {
            // Clear selection before remove so SelectionChanged cannot push the
            // removed app's form data onto the next selected item.
            AppList.SelectedItem = null;
            _selected = null;
            _apps.Remove(remove);

            _selected = _apps[Math.Clamp(index, 0, _apps.Count - 1)];
            AppList.SelectedItem = _selected;
            LoadSelectedIntoForm();
        }
        finally
        {
            _suppressSelectionEvents = false;
        }

        if (!TryPersistConfig(out var error))
        {
            _suppressSelectionEvents = true;
            try
            {
                var insertAt = Math.Clamp(index, 0, _apps.Count);
                _apps.Insert(insertAt, remove);
                _selected = remove;
                AppList.SelectedItem = remove;
                LoadSelectedIntoForm();
            }
            finally
            {
                _suppressSelectionEvents = false;
            }

            MessageBox.Show(
                error ?? "Could not save configuration after remove.",
                "Remove failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        FooterText.Text = $"Removed '{remove.Id}' and saved.";
        RefreshStatus();
    }

    private string NextUniqueId(string prefix)
    {
        var n = 1;
        while (_apps.Any(a => string.Equals(a.Id, $"{prefix}{n}", StringComparison.OrdinalIgnoreCase)))
            n++;
        return $"{prefix}{n}";
    }

    private void AppIdBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
            return;

        var next = AppIdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(next))
        {
            AppIdBox.Text = _selected.Id;
            return;
        }

        if (_apps.Any(a => !ReferenceEquals(a, _selected)
                           && string.Equals(a.Id, next, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                $"Application id '{next}' is already used.",
                "Duplicate id",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            AppIdBox.Text = _selected.Id;
            return;
        }

        _selected.Id = next;
        _selected.NotifyListLabelChanged();
    }

    private void EnabledCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _suppressSelectionEvents)
            return;

        _selected.Enabled = EnabledCheck.IsChecked == true;
        _selected.NotifyListLabelChanged();
    }

    private void BrowseExecutable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select application executable",
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (!string.IsNullOrWhiteSpace(ExecutableBox.Text))
        {
            try
            {
                dialog.InitialDirectory = Path.GetDirectoryName(ExecutableBox.Text);
                dialog.FileName = Path.GetFileName(ExecutableBox.Text);
            }
            catch
            {
                // ignore bad path
            }
        }

        if (dialog.ShowDialog(this) == true)
        {
            ExecutableBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(WorkingDirBox.Text))
                WorkingDirBox.Text = Path.GetDirectoryName(dialog.FileName) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(DisplayNameBox.Text) || DisplayNameBox.Text == "Kiosk Application")
                DisplayNameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        }
    }

    private void BrowseWorkingDir_Click(object sender, RoutedEventArgs e)
        => BrowseFolderInto(WorkingDirBox);

    private void BrowseHttpWorkingDir_Click(object sender, RoutedEventArgs e)
        => BrowseFolderInto(HttpWorkingDirBox);

    private void BrowseTcpWorkingDir_Click(object sender, RoutedEventArgs e)
        => BrowseFolderInto(TcpWorkingDirBox);

    private void BrowseFolderInto(System.Windows.Controls.TextBox target)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select working directory"
        };

        if (!string.IsNullOrWhiteSpace(target.Text) && Directory.Exists(target.Text))
            dialog.InitialDirectory = target.Text;
        else if (!string.IsNullOrWhiteSpace(ExecutableBox.Text))
        {
            try { dialog.InitialDirectory = Path.GetDirectoryName(ExecutableBox.Text); }
            catch { /* ignore */ }
        }

        if (dialog.ShowDialog(this) == true)
            target.Text = dialog.FolderName;
    }

    private void RefreshStatus()
    {
        var snapshot = StatusFilePublisher.ReadSnapshot();
        UpdateAppListStatuses(snapshot);

        var appId = _selected?.Id;
        WatchdogStatus? status = null;

        if (snapshot?.Applications is { Count: > 0 })
        {
            if (!string.IsNullOrWhiteSpace(appId))
            {
                status = snapshot.Applications.FirstOrDefault(a =>
                    string.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase));
            }

            status ??= snapshot.Applications[0];
        }

        status ??= new WatchdogStatus
        {
            Id = appId ?? WatchdogConfig.DefaultApplicationId,
            ApplicationName = DisplayNameBox.Text,
            Status = ApplicationStatus.Unknown,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        StatusValue.Text = status.Status.ToString();
        StatusChipText.Text = status.Status == ApplicationStatus.OutsideSchedule
            ? "Scheduled off"
            : status.Status.ToString();
        StatusChip.Background = BrushForStatus(status.Status);
        PidValue.Text = status.ProcessId?.ToString() ?? "—";
        RestartValue.Text = status.RestartCount.ToString();
        LastStartValue.Text = status.ProcessStartTime?.ToLocalTime().ToString("g") ?? "—";
        UptimeValue.Text = FormatLiveUptime(status);
        ScheduleValue.Text = FormatScheduleSummary();
        MemoryValue.Text = status.MemoryMegabytes is { } mb
            ? $"{mb:0} MB" + (status.ResourceProcessCount is int n && n > 1 ? $" ({n} procs)" : "")
            : "—";
        CpuValue.Text = status.CpuPercent is { } cpu ? $"{cpu:0}%" : "—";

        if (HealthEnabledCheck.IsChecked == true)
        {
            var ok = status.LastHealthCheckSucceeded;
            HealthValue.Text = ok is null
                ? FormatAgo(status.LastHealthCheckAt)
                : $"{(ok.Value ? "OK" : "FAIL")} · {FormatAgo(status.LastHealthCheckAt)}";
        }
        else
        {
            HealthValue.Text = "disabled";
        }

        MessageValue.Text = string.IsNullOrWhiteSpace(status.LastError) ? "—" : status.LastError;
        FooterText.Text = snapshot is null
            ? "Waiting for service status…"
            : $"Status updated {FormatAgo(snapshot.UpdatedAt)}";

        UpdateTrayTooltip(snapshot);
    }

    private void UpdateAppListStatuses(WatchdogStatusSnapshot? snapshot)
    {
        foreach (var app in _apps)
        {
            var match = snapshot?.Applications?.FirstOrDefault(a =>
                string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase));

            var status = app.Enabled
                ? match?.Status ?? ApplicationStatus.Unknown
                : ApplicationStatus.NotConfigured;

            app.SetStatus(status);
        }
    }

    private void UpdateTrayTooltip(WatchdogStatusSnapshot? snapshot)
    {
        if (snapshot?.Applications is not { Count: > 0 })
        {
            _tray.UpdateTooltip("Kiosk Watchdog — no status");
            return;
        }

        if (snapshot.Applications.Count == 1)
        {
            var only = snapshot.Applications[0];
            _tray.UpdateTooltip($"{only.ApplicationName}: {only.Status}");
            return;
        }

        var running = snapshot.Applications.Count(a => a.Status == ApplicationStatus.Running);
        var bad = snapshot.Applications.Count(a =>
            a.Status is ApplicationStatus.Unhealthy
                or ApplicationStatus.Error
                or ApplicationStatus.RestartLimitReached);
        _tray.UpdateTooltip($"Kiosk Watchdog — {running}/{snapshot.Applications.Count} running" +
                            (bad > 0 ? $", {bad} issue(s)" : ""));
    }

    private IReadOnlyList<TrayAppEntry> GetTrayApps()
    {
        var snapshot = StatusFilePublisher.ReadSnapshot();
        var entries = new List<TrayAppEntry>(_apps.Count);

        foreach (var app in _apps)
        {
            var match = snapshot?.Applications?.FirstOrDefault(a =>
                string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase));

            var status = app.Enabled
                ? match?.Status ?? ApplicationStatus.Unknown
                : ApplicationStatus.NotConfigured;

            entries.Add(new TrayAppEntry(
                app.Id,
                string.IsNullOrWhiteSpace(app.DisplayName) ? app.Id : app.DisplayName,
                status,
                app.Enabled));
        }

        return entries;
    }

    internal static SolidColorBrush BrushForStatus(ApplicationStatus status)
        => status switch
        {
            ApplicationStatus.Running => BrushFrom("#D1FAE5"),
            ApplicationStatus.Unhealthy or ApplicationStatus.Error or ApplicationStatus.RestartLimitReached
                => BrushFrom("#FEE2E2"),
            ApplicationStatus.Starting or ApplicationStatus.Restarting => BrushFrom("#DBEAFE"),
            ApplicationStatus.OutsideSchedule => BrushFrom("#FEF3C7"),
            ApplicationStatus.Stopped or ApplicationStatus.NotConfigured => BrushFrom("#E5E7EB"),
            _ => BrushFrom("#E5E7EB")
        };

    private static SolidColorBrush BrushFrom(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }

    private static string FormatAgo(DateTimeOffset? value)
    {
        if (value is null)
            return "—";

        var elapsed = DateTimeOffset.UtcNow - value.Value;
        if (elapsed < TimeSpan.FromSeconds(1))
            return "just now";
        if (elapsed < TimeSpan.FromMinutes(1))
            return $"{(int)elapsed.TotalSeconds}s ago";
        if (elapsed < TimeSpan.FromHours(1))
            return $"{(int)elapsed.TotalMinutes}m ago";
        return $"{(int)elapsed.TotalHours}h ago";
    }

    private string? SelectedApplicationId() => _selected?.Id;

    private void Start_Click(object sender, RoutedEventArgs e)
        => _commandQueue.Enqueue(WatchdogCommandType.Start, SelectedApplicationId());

    private void Stop_Click(object sender, RoutedEventArgs e)
        => _commandQueue.Enqueue(WatchdogCommandType.Stop, SelectedApplicationId());

    private void Restart_Click(object sender, RoutedEventArgs e)
        => _commandQueue.Enqueue(WatchdogCommandType.Restart, SelectedApplicationId());

    private void ResetCounter_Click(object sender, RoutedEventArgs e)
        => _commandQueue.Enqueue(WatchdogCommandType.ResetRestartCounter, SelectedApplicationId());

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryPersistConfig(out var error))
        {
            MessageBox.Show(
                error ?? "Save failed.",
                "Save failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        FooterText.Text = "Configuration saved. Service will reload automatically.";
        MessageBox.Show(
            "Configuration saved. The watchdog service will reload it automatically.",
            "Saved",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private bool TryPersistConfig(out string? error)
    {
        error = null;

        try
        {
            var config = BuildConfigFromApps();
            var validation = ConfigValidator.Validate(config);
            if (!validation.IsValid)
            {
                error = string.Join(Environment.NewLine, validation.Errors);
                return false;
            }

            _configStore.Save(config);

            // Best-effort only: the non-elevated UI usually cannot change the service start
            // type (sc.exe → access denied). The Windows service applies startOnBoot on reload.
            _ = WatchdogServiceManager.TrySetStartOnBoot(config.Service.StartOnBoot, out _);

            LoadAppsFromConfig(config);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = _configStore.Load();
            LoadAppsFromConfig(config);
            FooterText.Text = "Configuration reloaded from disk.";
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Reload failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TestHealth_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var checker = new HttpHealthChecker(http);
            HealthCheckResult result;

            if (SelectedKind() == ApplicationKind.Tcp)
            {
                var host = TcpHostBox.Text.Trim();
                var port = ParseInt(TcpPortBox.Text, 0);
                result = await checker.CheckTcpAsync(host, port).ConfigureAwait(true);
            }
            else
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

                var expected = ParseInt(ExpectedStatusBox.Text, 200);
                result = await checker.CheckHttpAsync(url, expected).ConfigureAwait(true);
            }

            MessageBox.Show(
                $"{result.Status}: {result.Message}" +
                (result.HttpStatusCode is int code ? $" (HTTP {code})" : ""),
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
        var viewer = new LogViewerWindow { Owner = this };
        viewer.Show();
    }

    private static string FormatLiveUptime(WatchdogStatus status)
    {
        if (status.ProcessStartTime is null)
            return "—";

        if (status.Status is ApplicationStatus.Stopped
            or ApplicationStatus.OutsideSchedule
            or ApplicationStatus.NotConfigured
            or ApplicationStatus.Unknown)
        {
            return "—";
        }

        var uptime = DateTimeOffset.UtcNow - status.ProcessStartTime.Value.ToUniversalTime();
        return ScheduleEvaluator.FormatUptime(uptime);
    }

    private string FormatScheduleSummary()
    {
        if (_selected is null || !_selected.ScheduleEnabled)
            return "Always on";

        var schedule = new ScheduleConfig
        {
            Enabled = true,
            StartTime = _selected.ScheduleStartTime,
            EndTime = _selected.ScheduleEndTime,
            DaysOfWeek = _selected.ScheduleDays.ToList()
        };

        var now = DateTimeOffset.UtcNow;
        var within = ScheduleEvaluator.IsWithinSchedule(schedule, now);
        var next = ScheduleEvaluator.GetNextTransition(schedule, now);
        var transition = ScheduleEvaluator.FormatTransition(next, now);
        var window = $"{schedule.StartTime}–{schedule.EndTime}";
        return within
            ? $"In window ({window}) · {transition}"
            : $"Outside ({window}) · {transition}";
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _refreshTimer.Stop();
            _tray.Dispose();
            return;
        }

        e.Cancel = true;
        Hide();
        FooterText.Text = "Running in the system tray.";
    }

    private void ShowFromTray() => BringToForeground();

    /// <summary>
    /// Shows and activates the window (tray double-click or a second UI launch).
    /// </summary>
    internal void BringToForeground()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _allowClose = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }
}

internal sealed class AppListItem : INotifyPropertyChanged
{
    private ApplicationStatus _status = ApplicationStatus.Unknown;
    private string _statusLabel = "—";
    private SolidColorBrush _statusBrush = MainWindow.BrushForStatus(ApplicationStatus.Unknown);

    public string Id { get; set; } = WatchdogConfig.DefaultApplicationId;
    public bool Enabled { get; set; } = true;
    public ApplicationKind Kind { get; set; } = ApplicationKind.Process;
    public string DisplayName { get; set; } = "Kiosk Application";
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string StartCommand { get; set; } = string.Empty;
    public string StopCommand { get; set; } = string.Empty;
    public string HttpWorkingDirectory { get; set; } = string.Empty;
    public string TcpHost { get; set; } = "127.0.0.1";
    public int TcpPort { get; set; }
    public string TcpStartCommand { get; set; } = string.Empty;
    public string TcpStopCommand { get; set; } = string.Empty;
    public string TcpWorkingDirectory { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public int ProcessCheckIntervalSeconds { get; set; } = 5;
    public int HealthCheckIntervalSeconds { get; set; } = 10;
    public int HealthTimeoutSeconds { get; set; } = 45;
    public int GracefulTerminationTimeoutSeconds { get; set; } = 10;
    public int RestartDelaySeconds { get; set; } = 5;
    public int MaxRestarts { get; set; } = 5;
    public int RestartWindowMinutes { get; set; } = 10;
    public bool HealthEnabled { get; set; }
    public string HealthUrl { get; set; } = string.Empty;
    public int ExpectedStatusCode { get; set; } = 200;
    public LaunchMode LaunchMode { get; set; } = LaunchMode.Interactive;
    public bool ScheduleEnabled { get; set; }
    public string ScheduleStartTime { get; set; } = "09:00";
    public string ScheduleEndTime { get; set; } = "18:00";
    public List<DayOfWeek> ScheduleDays { get; set; } =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
        DayOfWeek.Thursday, DayOfWeek.Friday
    ];
    public bool ResourcesEnabled { get; set; }
    public bool ResourcesIncludeChildren { get; set; } = true;
    public int MaxMemoryMegabytes { get; set; }
    public int MaxCpuPercent { get; set; }
    public int BreachDurationSeconds { get; set; } = 300;

    public string KindLabel => Kind switch
    {
        ApplicationKind.Http => "HTTP",
        ApplicationKind.Tcp => "TCP",
        ApplicationKind.WindowsService => "Windows Service",
        _ => "Process"
    };

    public string StatusLabel => _statusLabel;
    public SolidColorBrush StatusBrush => _statusBrush;

    public string ListLabel
    {
        get
        {
            var baseLabel = $"{Id} [{KindLabel}] — {DisplayName}";
            return Enabled ? baseLabel : $"{baseLabel} (disabled)";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetStatus(ApplicationStatus status)
    {
        var label = status switch
        {
            ApplicationStatus.Unknown => "—",
            ApplicationStatus.NotConfigured => Enabled ? "Unknown" : "Disabled",
            ApplicationStatus.OutsideSchedule => "Scheduled off",
            ApplicationStatus.RestartLimitReached => "Limit",
            _ => status.ToString()
        };

        if (_status == status && _statusLabel == label)
            return;

        _status = status;
        _statusLabel = label;
        _statusBrush = MainWindow.BrushForStatus(status);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
    }

    public void NotifyListLabelChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ListLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KindLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
    }

    public static AppListItem CreateNew(string id) => new()
    {
        Id = id,
        DisplayName = id
    };

    public static AppListItem FromConfig(MonitoredApplicationConfig app) => new()
    {
        Id = app.Id,
        Enabled = app.Enabled,
        Kind = app.Kind,
        DisplayName = app.Application.DisplayName,
        ExecutablePath = app.Application.ExecutablePath,
        Arguments = app.Application.Arguments,
        WorkingDirectory = app.Application.WorkingDirectory,
        StartCommand = app.Http.StartCommand,
        StopCommand = app.Http.StopCommand,
        HttpWorkingDirectory = app.Http.WorkingDirectory,
        TcpHost = app.Tcp.Host,
        TcpPort = app.Tcp.Port,
        TcpStartCommand = app.Tcp.StartCommand,
        TcpStopCommand = app.Tcp.StopCommand,
        TcpWorkingDirectory = app.Tcp.WorkingDirectory,
        ServiceName = app.WindowsService.ServiceName,
        ProcessCheckIntervalSeconds = app.Monitoring.ProcessCheckIntervalSeconds,
        HealthCheckIntervalSeconds = app.Monitoring.HealthCheckIntervalSeconds,
        HealthTimeoutSeconds = app.Monitoring.HealthTimeoutSeconds,
        GracefulTerminationTimeoutSeconds = app.Monitoring.GracefulTerminationTimeoutSeconds,
        RestartDelaySeconds = app.Restart.RestartDelaySeconds,
        MaxRestarts = app.Restart.MaxRestarts,
        RestartWindowMinutes = app.Restart.RestartWindowMinutes,
        HealthEnabled = app.Health.Enabled,
        HealthUrl = app.Health.Url,
        ExpectedStatusCode = app.Health.ExpectedStatusCode,
        LaunchMode = app.Launch.Mode,
        ScheduleEnabled = app.Schedule.Enabled,
        ScheduleStartTime = string.IsNullOrWhiteSpace(app.Schedule.StartTime) ? "09:00" : app.Schedule.StartTime,
        ScheduleEndTime = string.IsNullOrWhiteSpace(app.Schedule.EndTime) ? "18:00" : app.Schedule.EndTime,
        ScheduleDays = app.Schedule.DaysOfWeek is { Count: > 0 }
            ? app.Schedule.DaysOfWeek.ToList()
            :
            [
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday
            ],
        ResourcesEnabled = app.Resources.Enabled,
        ResourcesIncludeChildren = app.Resources.IncludeChildProcesses,
        MaxMemoryMegabytes = app.Resources.MaxMemoryMegabytes,
        MaxCpuPercent = app.Resources.MaxCpuPercent,
        BreachDurationSeconds = app.Resources.BreachDurationSeconds
    };

    public MonitoredApplicationConfig ToConfig() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Kind = Kind,
        Application = new ApplicationConfig
        {
            ExecutablePath = ExecutablePath,
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory,
            DisplayName = DisplayName
        },
        Http = new HttpAppConfig
        {
            StartCommand = StartCommand,
            StopCommand = StopCommand,
            WorkingDirectory = string.IsNullOrWhiteSpace(HttpWorkingDirectory)
                ? WorkingDirectory
                : HttpWorkingDirectory
        },
        Tcp = new TcpAppConfig
        {
            Host = string.IsNullOrWhiteSpace(TcpHost) ? "127.0.0.1" : TcpHost,
            Port = TcpPort,
            StartCommand = TcpStartCommand,
            StopCommand = TcpStopCommand,
            WorkingDirectory = TcpWorkingDirectory
        },
        WindowsService = new WindowsServiceAppConfig
        {
            ServiceName = ServiceName
        },
        Monitoring = new MonitoringConfig
        {
            ProcessCheckIntervalSeconds = ProcessCheckIntervalSeconds,
            HealthCheckIntervalSeconds = HealthCheckIntervalSeconds,
            HealthTimeoutSeconds = HealthTimeoutSeconds,
            GracefulTerminationTimeoutSeconds = GracefulTerminationTimeoutSeconds
        },
        Restart = new RestartConfig
        {
            RestartOnExit = true,
            RestartOnUnhealthy = true,
            RestartDelaySeconds = RestartDelaySeconds,
            MaxRestarts = MaxRestarts,
            RestartWindowMinutes = RestartWindowMinutes
        },
        Health = new HealthConfig
        {
            Enabled = Kind == ApplicationKind.Http || HealthEnabled,
            Type = "http",
            Url = HealthUrl,
            ExpectedStatusCode = ExpectedStatusCode
        },
        Launch = new LaunchConfig
        {
            Mode = LaunchMode
        },
        Schedule = new ScheduleConfig
        {
            Enabled = ScheduleEnabled,
            StartTime = ScheduleStartTime,
            EndTime = ScheduleEndTime,
            DaysOfWeek = ScheduleDays.ToList()
        },
        Resources = new ResourceLimitsConfig
        {
            Enabled = ResourcesEnabled,
            MaxMemoryMegabytes = MaxMemoryMegabytes,
            MaxCpuPercent = MaxCpuPercent,
            BreachDurationSeconds = BreachDurationSeconds,
            IncludeChildProcesses = ResourcesIncludeChildren
        }
    };
}
