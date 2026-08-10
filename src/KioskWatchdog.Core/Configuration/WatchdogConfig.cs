using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace KioskWatchdog.Core.Configuration;

public sealed class WatchdogConfig
{
    public const string DefaultConfigDirectory = @"C:\ProgramData\KioskWatchdog";
    public const string DefaultConfigFileName = "config.json";
    public const string DefaultLogsDirectory = @"C:\ProgramData\KioskWatchdog\logs";
    public const string DefaultApplicationId = "default";

    public List<MonitoredApplicationConfig> Applications { get; set; } = new();

    public ServiceSettingsConfig Service { get; set; } = new();

    public NotificationsConfig Notifications { get; set; } = new();

    public static string DefaultConfigPath =>
        Path.Combine(DefaultConfigDirectory, DefaultConfigFileName);

    public static WatchdogConfig CreateDefault() => new();

    /// <summary>
    /// Ensures each application has an id and non-null nested sections.
    /// </summary>
    public void Normalize()
    {
        Service ??= new ServiceSettingsConfig();
        Notifications ??= new NotificationsConfig();
        Notifications.Webhook ??= new WebhookConfig();
        Notifications.Webhook.Events ??= new WebhookEventsConfig();
        Notifications.Webhook.StatusReport ??= new StatusReportConfig();

        if (Notifications.Webhook.TimeoutSeconds is < 1 or > 120)
            Notifications.Webhook.TimeoutSeconds = 10;

        if (Notifications.Webhook.StatusReport.IntervalMinutes is < 1 or > 1440)
            Notifications.Webhook.StatusReport.IntervalMinutes = 60;

        foreach (var app in Applications)
        {
            if (string.IsNullOrWhiteSpace(app.Id))
                app.Id = DefaultApplicationId;

            app.Id = app.Id.Trim();
            app.Application ??= new ApplicationConfig();
            app.Http ??= new HttpAppConfig();
            app.Tcp ??= new TcpAppConfig();
            app.WindowsService ??= new WindowsServiceAppConfig();
            app.Monitoring ??= new MonitoringConfig();
            app.Restart ??= new RestartConfig();
            app.Health ??= new HealthConfig();
            app.Launch ??= new LaunchConfig();
            app.Schedule ??= new ScheduleConfig();
            app.Schedule.DaysOfWeek ??= new List<DayOfWeek>();
            app.Resources ??= new ResourceLimitsConfig();

            if (app.Resources.MaxMemoryMegabytes < 0)
                app.Resources.MaxMemoryMegabytes = 0;
            if (app.Resources.MaxCpuPercent < 0)
                app.Resources.MaxCpuPercent = 0;
            if (app.Resources.BreachDurationSeconds is < 1 or > 86400)
                app.Resources.BreachDurationSeconds = 300;

            if (app.Schedule.Enabled && app.Schedule.DaysOfWeek.Count == 0)
            {
                app.Schedule.DaysOfWeek.AddRange(
                [
                    DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                    DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday
                ]);
            }

            if (ScheduleConfig.TryParseTime(app.Schedule.StartTime, out var start))
                app.Schedule.StartTime = ScheduleConfig.FormatTime(start);
            if (ScheduleConfig.TryParseTime(app.Schedule.EndTime, out var end))
                app.Schedule.EndTime = ScheduleConfig.FormatTime(end);

            if (app.IsHttp)
            {
                // HTTP apps are live when their health URL responds.
                if (!string.IsNullOrWhiteSpace(app.Health.Url))
                    app.Health.Enabled = true;

                if (string.IsNullOrWhiteSpace(app.Application.WorkingDirectory)
                    && !string.IsNullOrWhiteSpace(app.Http.WorkingDirectory))
                {
                    app.Application.WorkingDirectory = app.Http.WorkingDirectory;
                }
            }

            if (app.IsTcp
                && string.IsNullOrWhiteSpace(app.Application.WorkingDirectory)
                && !string.IsNullOrWhiteSpace(app.Tcp.WorkingDirectory))
            {
                app.Application.WorkingDirectory = app.Tcp.WorkingDirectory;
            }

            if (app.Health.ExpectedStatusCode is < 100 or > 599)
                app.Health.ExpectedStatusCode = 200;
        }
    }

    public IEnumerable<MonitoredApplicationConfig> EnabledApplications()
        => Applications.Where(a => a.Enabled);

    public MonitoredApplicationConfig? FindApplication(string? applicationId)
    {
        if (Applications.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return Applications.FirstOrDefault(a =>
                       string.Equals(a.Id, DefaultApplicationId, StringComparison.OrdinalIgnoreCase))
                   ?? Applications[0];
        }

        return Applications.FirstOrDefault(a =>
            string.Equals(a.Id, applicationId, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class MonitoredApplicationConfig
{
    public string Id { get; set; } = WatchdogConfig.DefaultApplicationId;
    public bool Enabled { get; set; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ApplicationKind Kind { get; set; } = ApplicationKind.Process;

    public ApplicationConfig Application { get; set; } = new();

    /// <summary>Used when <see cref="Kind"/> is <see cref="ApplicationKind.Http"/>.</summary>
    public HttpAppConfig Http { get; set; } = new();

    /// <summary>Used when <see cref="Kind"/> is <see cref="ApplicationKind.Tcp"/>.</summary>
    public TcpAppConfig Tcp { get; set; } = new();

    /// <summary>Used when <see cref="Kind"/> is <see cref="ApplicationKind.WindowsService"/>.</summary>
    public WindowsServiceAppConfig WindowsService { get; set; } = new();

    public MonitoringConfig Monitoring { get; set; } = new();
    public RestartConfig Restart { get; set; } = new();
    public HealthConfig Health { get; set; } = new();
    public LaunchConfig Launch { get; set; } = new();
    public ScheduleConfig Schedule { get; set; } = new();
    public ResourceLimitsConfig Resources { get; set; } = new();

    public bool IsHttp => Kind == ApplicationKind.Http;
    public bool IsTcp => Kind == ApplicationKind.Tcp;
    public bool IsWindowsService => Kind == ApplicationKind.WindowsService;
    public bool IsProbeTarget => IsHttp || IsTcp;
}

/// <summary>Local website / server started via a shell command, monitored by HTTP health.</summary>
public sealed class HttpAppConfig
{
    /// <summary>Shell command to start the site, e.g. <c>npm start</c> or <c>dotnet run</c>. Empty = probe-only.</summary>
    public string StartCommand { get; set; } = string.Empty;

    /// <summary>
    /// Optional shell command to stop. If empty, the watchdog kills the process tree of the started command.
    /// </summary>
    public string StopCommand { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;
}

/// <summary>TCP port liveness (optional start/stop commands for recovery).</summary>
public sealed class TcpAppConfig
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public string StartCommand { get; set; } = string.Empty;
    public string StopCommand { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
}

/// <summary>Monitor / restart a Windows Service by name.</summary>
public sealed class WindowsServiceAppConfig
{
    public string ServiceName { get; set; } = string.Empty;
}

public enum ApplicationKind
{
    Process,
    Http,
    Tcp,
    WindowsService
}

public sealed class ApplicationConfig
{
    public string ExecutablePath { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string DisplayName { get; set; } = "Kiosk Application";
}

public sealed class MonitoringConfig
{
    [Range(1, 3600)]
    public int ProcessCheckIntervalSeconds { get; set; } = 5;

    [Range(1, 3600)]
    public int HealthCheckIntervalSeconds { get; set; } = 10;

    [Range(1, 3600)]
    public int HealthTimeoutSeconds { get; set; } = 45;

    [Range(1, 120)]
    public int GracefulTerminationTimeoutSeconds { get; set; } = 10;
}

public sealed class RestartConfig
{
    public bool RestartOnExit { get; set; } = true;
    public bool RestartOnUnhealthy { get; set; } = true;

    [Range(0, 3600)]
    public int RestartDelaySeconds { get; set; } = 5;

    [Range(1, 1000)]
    public int MaxRestarts { get; set; } = 5;

    [Range(1, 1440)]
    public int RestartWindowMinutes { get; set; } = 10;
}

public sealed class HealthConfig
{
    public bool Enabled { get; set; } = false;

    public string Type { get; set; } = "http";

    /// <summary>
    /// Optional localhost health URL, e.g. http://127.0.0.1:3000/health.
    /// Only used when <see cref="Enabled"/> is true.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Expected HTTP status for a healthy probe (default 200).</summary>
    public int ExpectedStatusCode { get; set; } = 200;
}

public sealed class LaunchConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LaunchMode Mode { get; set; } = LaunchMode.Interactive;
}

public enum LaunchMode
{
    Interactive,
    Service
}
