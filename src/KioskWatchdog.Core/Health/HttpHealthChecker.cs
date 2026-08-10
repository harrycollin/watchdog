using System.Net;
using System.Net.Sockets;
using KioskWatchdog.Core.Abstractions;
using Microsoft.Extensions.Logging;

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

    public Task<HealthCheckResult> CheckAsync(string url, CancellationToken cancellationToken = default)
        => CheckHttpAsync(url, 200, cancellationToken);

    public async Task<HealthCheckResult> CheckHttpAsync(
        string url,
        int expectedStatusCode = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (expectedStatusCode is < 100 or > 599)
            expectedStatusCode = 200;

        var checkedAt = _clock.UtcNow;

        try
        {
            using var response = await _httpClient
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            var code = (int)response.StatusCode;
            if (code == expectedStatusCode)
            {
                return new HealthCheckResult
                {
                    Status = HealthStatus.Healthy,
                    CheckedAt = checkedAt,
                    HttpStatusCode = code,
                    Message = "OK"
                };
            }

            return new HealthCheckResult
            {
                Status = HealthStatus.Unhealthy,
                CheckedAt = checkedAt,
                HttpStatusCode = code,
                Message = $"Unexpected status code {code} (expected {expectedStatusCode})"
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

    public async Task<HealthCheckResult> CheckTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            return new HealthCheckResult
            {
                Status = HealthStatus.Unhealthy,
                CheckedAt = _clock.UtcNow,
                Message = "TCP port must be between 1 and 65535."
            };
        }

        if (!IsLocalhost(host))
        {
            return new HealthCheckResult
            {
                Status = HealthStatus.Unhealthy,
                CheckedAt = _clock.UtcNow,
                Message = "TCP host must be localhost (127.0.0.1 / localhost / ::1)."
            };
        }

        var checkedAt = _clock.UtcNow;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new HealthCheckResult
            {
                Status = HealthStatus.Healthy,
                CheckedAt = checkedAt,
                Message = $"TCP {host}:{port} open"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "TCP check failed for {Host}:{Port}", host, port);
            return new HealthCheckResult
            {
                Status = HealthStatus.Unhealthy,
                CheckedAt = checkedAt,
                Message = ex.Message,
                Exception = ex
            };
        }
    }

    private static bool IsLocalhost(string host)
        => string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase)
           || IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
}
