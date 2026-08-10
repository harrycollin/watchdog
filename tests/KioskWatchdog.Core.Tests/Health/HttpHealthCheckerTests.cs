using System.Net;
using System.Text;
using KioskWatchdog.Core.Health;
using KioskWatchdog.Core.Tests.Fakes;

namespace KioskWatchdog.Core.Tests.Health;

public class HttpHealthCheckerTests
{
    [Fact]
    public async Task Http_200_returns_healthy()
    {
        using var server = new LocalHealthServer(HttpStatusCode.OK);
        using var client = new HttpClient();
        var checker = new HttpHealthChecker(client, new FakeClock());

        var result = await checker.CheckAsync(server.Url);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(200, result.HttpStatusCode);
    }

    [Fact]
    public async Task Http_failure_returns_unhealthy()
    {
        using var server = new LocalHealthServer(HttpStatusCode.ServiceUnavailable);
        using var client = new HttpClient();
        var checker = new HttpHealthChecker(client, new FakeClock());

        var result = await checker.CheckAsync(server.Url);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(503, result.HttpStatusCode);
    }

    [Fact]
    public async Task Http_expected_status_code_is_honored()
    {
        using var server = new LocalHealthServer(HttpStatusCode.NoContent);
        using var client = new HttpClient();
        var checker = new HttpHealthChecker(client, new FakeClock());

        var ok = await checker.CheckHttpAsync(server.Url, expectedStatusCode: 204);
        var bad = await checker.CheckHttpAsync(server.Url, expectedStatusCode: 200);

        Assert.Equal(HealthStatus.Healthy, ok.Status);
        Assert.Equal(HealthStatus.Unhealthy, bad.Status);
    }

    [Fact]
    public async Task Tcp_open_port_is_healthy()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            using var client = new HttpClient();
            var checker = new HttpHealthChecker(client, new FakeClock());
            var result = await checker.CheckTcpAsync("127.0.0.1", port);
            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class LocalHealthServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public string Url { get; }

        public LocalHealthServer(HttpStatusCode statusCode)
        {
            var port = GetFreePort();
            Url = $"http://127.0.0.1:{port}/health";
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    var body = Encoding.UTF8.GetBytes("""{"status":"ok"}""");
                    context.Response.StatusCode = (int)statusCode;
                    context.Response.ContentType = "application/json";
                    context.Response.OutputStream.Write(body);
                    context.Response.Close();
                }
            });
        }

        private static int GetFreePort()
        {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* ignore */ }
            _listener.Close();
            _cts.Dispose();
        }
    }
}
