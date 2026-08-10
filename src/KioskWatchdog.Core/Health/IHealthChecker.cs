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
    Task<HealthCheckResult> CheckHttpAsync(
        string url,
        int expectedStatusCode = 200,
        CancellationToken cancellationToken = default);

    Task<HealthCheckResult> CheckTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default);

    /// <summary>Back-compat helper (expects HTTP 200).</summary>
    Task<HealthCheckResult> CheckAsync(string url, CancellationToken cancellationToken = default)
        => CheckHttpAsync(url, 200, cancellationToken);
}
