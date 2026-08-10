using KioskWatchdog.Core.Health;

namespace KioskWatchdog.Core.Tests.Fakes;

internal sealed class FakeHealthChecker : IHealthChecker
{
    private readonly Queue<HealthCheckResult> _results = new();
    private HealthCheckResult? _defaultResult;

    public int CallCount { get; private set; }

    public void Enqueue(params HealthCheckResult[] results)
    {
        foreach (var result in results)
            _results.Enqueue(result);
    }

    public void SetDefault(HealthCheckResult result) => _defaultResult = result;

    public Task<HealthCheckResult> CheckAsync(string url, CancellationToken cancellationToken = default)
    {
        CallCount++;
        if (_results.Count > 0)
            return Task.FromResult(_results.Dequeue());

        if (_defaultResult is not null)
            return Task.FromResult(_defaultResult);

        return Task.FromResult(new HealthCheckResult
        {
            Status = HealthStatus.Healthy,
            CheckedAt = DateTimeOffset.UtcNow,
            HttpStatusCode = 200,
            Message = "OK"
        });
    }

    public static HealthCheckResult Ok(DateTimeOffset? at = null) => new()
    {
        Status = HealthStatus.Healthy,
        CheckedAt = at ?? DateTimeOffset.UtcNow,
        HttpStatusCode = 200,
        Message = "OK"
    };

    public static HealthCheckResult Fail(DateTimeOffset? at = null, string message = "fail") => new()
    {
        Status = HealthStatus.Unhealthy,
        CheckedAt = at ?? DateTimeOffset.UtcNow,
        HttpStatusCode = 503,
        Message = message
    };
}
