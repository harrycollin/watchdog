namespace KioskWatchdog.Core.Status;

public enum ApplicationStatus
{
    Unknown,
    NotConfigured,
    Stopped,
    OutsideSchedule,
    Starting,
    Running,
    Unhealthy,
    Restarting,
    RestartLimitReached,
    Error
}

public sealed class WatchdogStatus
{
    public string Id { get; set; } = "default";
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
    public double? MemoryMegabytes { get; set; }
    public double? CpuPercent { get; set; }
    public int? ResourceProcessCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public TimeSpan? Uptime =>
        ProcessStartTime is null
        || Status is ApplicationStatus.Stopped
            or ApplicationStatus.OutsideSchedule
            or ApplicationStatus.Unknown
            or ApplicationStatus.NotConfigured
            ? null
            : UpdatedAt - ProcessStartTime.Value;

    public WatchdogStatus Clone() => new()
    {
        Id = Id,
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
        MemoryMegabytes = MemoryMegabytes,
        CpuPercent = CpuPercent,
        ResourceProcessCount = ResourceProcessCount,
        UpdatedAt = UpdatedAt
    };
}

/// <summary>Multi-app status document written to status.json.</summary>
public sealed class WatchdogStatusSnapshot
{
    public DateTimeOffset UpdatedAt { get; set; }
    public List<WatchdogStatus> Applications { get; set; } = new();
}

public interface IWatchdogStatusStore
{
    event EventHandler? Changed;
    IReadOnlyList<WatchdogStatus> All { get; }
    WatchdogStatus? Get(string applicationId);
    void Upsert(string applicationId, Action<WatchdogStatus> mutate);
    void RemoveMissing(IEnumerable<string> activeIds);
    WatchdogStatusSnapshot CreateSnapshot();
}
