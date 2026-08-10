using KioskWatchdog.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Health;

/// <summary>
/// Tracks consecutive health failures and marks the application unhealthy
/// only after the configured timeout has elapsed with no successful check.
/// </summary>
public sealed class HealthMonitor
{
    private readonly IClock _clock;
    private readonly ILogger<HealthMonitor>? _logger;
    private readonly object _gate = new();

    private DateTimeOffset? _unhealthySince;
    private DateTimeOffset? _lastSuccessAt;
    private DateTimeOffset? _lastFailureAt;
    private DateTimeOffset? _lastCheckAt;
    private int _consecutiveFailures;
    private int _successesSinceLastFailureLog;
    private HealthCheckResult? _lastResult;

    public HealthMonitor(IClock? clock = null, ILogger<HealthMonitor>? logger = null)
    {
        _clock = clock ?? new SystemClock();
        _logger = logger;
    }

    public DateTimeOffset? LastSuccessAt
    {
        get { lock (_gate) return _lastSuccessAt; }
    }

    public DateTimeOffset? LastFailureAt
    {
        get { lock (_gate) return _lastFailureAt; }
    }

    public DateTimeOffset? LastCheckAt
    {
        get { lock (_gate) return _lastCheckAt; }
    }

    public int ConsecutiveFailures
    {
        get { lock (_gate) return _consecutiveFailures; }
    }

    public HealthCheckResult? LastResult
    {
        get { lock (_gate) return _lastResult; }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _unhealthySince = null;
            _consecutiveFailures = 0;
            _successesSinceLastFailureLog = 0;
        }
    }

    public HealthEvaluation Evaluate(HealthCheckResult result, TimeSpan unhealthyTimeout)
    {
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            _lastResult = result;
            _lastCheckAt = result.CheckedAt;

            if (result.IsSuccess)
            {
                var hadFailures = _consecutiveFailures > 0;
                _consecutiveFailures = 0;
                _unhealthySince = null;
                _lastSuccessAt = result.CheckedAt;
                _successesSinceLastFailureLog++;

                if (hadFailures)
                {
                    _logger?.LogInformation("Health check recovered after previous failures.");
                    _successesSinceLastFailureLog = 0;
                }
                else if (_successesSinceLastFailureLog == 1 || _successesSinceLastFailureLog % 30 == 0)
                {
                    // Aggregate successful checks — log first and then periodically.
                    _logger?.LogDebug(
                        "Health check succeeded ({Count} consecutive since last failure log).",
                        _successesSinceLastFailureLog);
                }

                return new HealthEvaluation(HealthStatus.Healthy, false, 0, null);
            }

            _consecutiveFailures++;
            _lastFailureAt = result.CheckedAt;
            _successesSinceLastFailureLog = 0;
            _unhealthySince ??= result.CheckedAt;

            var elapsed = result.CheckedAt - _unhealthySince.Value;
            var shouldRestart = elapsed >= unhealthyTimeout;

            _logger?.LogWarning(
                "Health check failed ({Failures} consecutive, unhealthy for {Elapsed}): {Message}",
                _consecutiveFailures,
                elapsed,
                result.Message);

            if (shouldRestart)
            {
                _logger?.LogError(
                    "Application marked unhealthy after {Elapsed} without a successful health check.",
                    elapsed);
            }

            return new HealthEvaluation(
                HealthStatus.Unhealthy,
                shouldRestart,
                _consecutiveFailures,
                elapsed);
        }
    }
}

public sealed record HealthEvaluation(
    HealthStatus Status,
    bool ShouldRestart,
    int ConsecutiveFailures,
    TimeSpan? UnhealthyDuration);
