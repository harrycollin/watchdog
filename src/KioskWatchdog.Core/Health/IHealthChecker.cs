namespace KioskWatchdog.Core.Health;

public enum HealthStatus
{
    Healthy,
    Unhealthy,
    Unknown
}

public sealed class HealthCheckResult
{
    public required HealthStatus Status { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }
    public int? HttpStatusCode { get; init; }
    public string? Message { get; init; }
    public Exception? Exception { get; init; }

    public bool IsSuccess => Status == HealthStatus.Healthy;
}

public interface IHealthChecker
{
    Task<HealthCheckResult> CheckAsync(string url, CancellationToken cancellationToken = default);
}
