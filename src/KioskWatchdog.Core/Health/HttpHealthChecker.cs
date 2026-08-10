using System.Net;
using Microsoft.Extensions.Logging;
using KioskWatchdog.Core.Abstractions;

namespace KioskWatchdog.Core.Health;

public sealed class HttpHealthChecker : IHealthChecker
{
    private readonly HttpClient _httpClient;
    private readonly IClock _clock;
    private readonly ILogger<HttpHealthChecker>? _logger;

    public HttpHealthChecker(
        HttpClient httpClient,
        IClock? clock = null,
        ILogger<HttpHealthChecker>? logger = null)
    {
        _httpClient = httpClient;
        _clock = clock ?? new SystemClock();
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var checkedAt = _clock.UtcNow;

        try
        {
            using var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                return new HealthCheckResult
                {
                    Status = HealthStatus.Healthy,
                    CheckedAt = checkedAt,
                    HttpStatusCode = (int)response.StatusCode,
                    Message = "OK"
                };
            }

            return new HealthCheckResult
            {
                Status = HealthStatus.Unhealthy,
                CheckedAt = checkedAt,
                HttpStatusCode = (int)response.StatusCode,
                Message = $"Unexpected status code {(int)response.StatusCode}"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Health check failed for {Url}", url);
            return new HealthCheckResult
            {
                Status = HealthStatus.Unhealthy,
                CheckedAt = checkedAt,
                Message = ex.Message,
                Exception = ex
            };
        }
    }
}
