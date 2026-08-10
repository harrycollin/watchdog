namespace KioskWatchdog.Core.Status;

public enum ApplicationStatus
{
    Unknown,
    Stopped,
    Starting,
    Running,
    Unhealthy,
    Restarting,
    RestartLimitReached,
    Error
}

public sealed class WatchdogStatus
{
    public string ApplicationName { get; set; } = "Kiosk Application";
    public ApplicationStatus Status { get; set; } = ApplicationStatus.Unknown;
    public int? ProcessId { get; set; }
    public DateTimeOffset? ProcessStartTime { get; set; }
    public DateTimeOffset? LastHealthCheckAt { get; set; }
    public bool? LastHealthCheckSucceeded { get; set; }
    public DateTimeOffset? LastRestartAt { get; set; }
    public int RestartCount { get; set; }
    public bool RestartLimitReached { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public TimeSpan? Uptime =>
        ProcessStartTime is null || Status is ApplicationStatus.Stopped or ApplicationStatus.Unknown
            ? null
            : UpdatedAt - ProcessStartTime.Value;

    public WatchdogStatus Clone() => new()
    {
        ApplicationName = ApplicationName,
        Status = Status,
        ProcessId = ProcessId,
        ProcessStartTime = ProcessStartTime,
        LastHealthCheckAt = LastHealthCheckAt,
        LastHealthCheckSucceeded = LastHealthCheckSucceeded,
        LastRestartAt = LastRestartAt,
        RestartCount = RestartCount,
        RestartLimitReached = RestartLimitReached,
        LastError = LastError,
        UpdatedAt = UpdatedAt
    };
}

public interface IWatchdogStatusStore
{
    WatchdogStatus Current { get; }
    event EventHandler? Changed;
    void Update(Action<WatchdogStatus> mutate);
}
