using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace KioskWatchdog.Core.Configuration;

public sealed class WatchdogConfig
{
    public const string DefaultConfigDirectory = @"C:\ProgramData\KioskWatchdog";
    public const string DefaultConfigFileName = "config.json";
    public const string DefaultLogsDirectory = @"C:\ProgramData\KioskWatchdog\logs";

    public ApplicationConfig Application { get; set; } = new();
    public MonitoringConfig Monitoring { get; set; } = new();
    public RestartConfig Restart { get; set; } = new();
    public HealthConfig Health { get; set; } = new();
    public LaunchConfig Launch { get; set; } = new();

    public static string DefaultConfigPath =>
        Path.Combine(DefaultConfigDirectory, DefaultConfigFileName);

    public static WatchdogConfig CreateDefault() => new();
}

public sealed class ApplicationConfig
{
    [Required]
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
    public bool Enabled { get; set; } = true;

    public string Type { get; set; } = "http";

    public string Url { get; set; } = "http://127.0.0.1:3000/health";
}

public sealed class LaunchConfig
{
    /// <summary>
    /// Interactive: launch the process directly in the current user session (recommended for Electron).
    /// Service: watchdog runs as a Windows Service; process launch uses the interactive-session launcher when available.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LaunchMode Mode { get; set; } = LaunchMode.Interactive;
}

public enum LaunchMode
{
    Interactive,
    Service
}
