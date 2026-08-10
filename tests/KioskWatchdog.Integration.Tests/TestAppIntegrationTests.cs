using System.Net;
using System.Net.Sockets;
using KioskWatchdog.Core.Status;
using KioskWatchdog.Integration.Tests.Support;

namespace KioskWatchdog.Integration.Tests;

[Collection("IntegrationSerial")]
public class TestAppIntegrationTests
{
    [Fact]
    public async Task TestApp_normal_is_started_and_tracked()
    {
        RequireWindows();
        var port = FreePort();
        var app = TestPaths.RequireTestAppExe();

        await using var harness = EngineHarness.Create(
            app,
            $"--normal --port {port}");

        await harness.StartEngineAsync();
        await WaitHelper.UntilAsync(
            () => harness.Status.Current.Status == ApplicationStatus.Running
                  && harness.Status.Current.ProcessId is not null,
            TimeSpan.FromSeconds(20),
            "TestApp should be running.");

        Assert.Equal(ApplicationStatus.Running, harness.Status.Current.Status);
    }

    [Fact]
    public async Task TestApp_crash_is_restarted()
    {
        RequireWindows();
        var app = TestPaths.RequireTestAppExe();

        await using var harness = EngineHarness.Create(
            app,
            "--crash",
            configure: c =>
            {
                c.Restart.RestartDelaySeconds = 1;
                c.Restart.MaxRestarts = 5;
            });

        await harness.StartEngineAsync();
        await WaitHelper.UntilAsync(
            () => harness.Status.Current.RestartCount >= 2
                  || harness.Status.Current.Status == ApplicationStatus.RestartLimitReached,
            TimeSpan.FromSeconds(30),
            "Crashing TestApp should cause restarts.");

        Assert.True(
            harness.Status.Current.RestartCount >= 2
            || harness.Status.Current.Status == ApplicationStatus.RestartLimitReached);
    }

    [Fact]
    public async Task TestApp_exit_after_is_restarted()
    {
        RequireWindows();
        var port = FreePort();
        var app = TestPaths.RequireTestAppExe();

        await using var harness = EngineHarness.Create(
            app,
            $"--exit-after 2 --port {port}",
            configure: c => c.Restart.RestartDelaySeconds = 1);

        await harness.StartEngineAsync();
        await WaitHelper.UntilAsync(
            () => harness.Status.Current.ProcessId is not null,
            TimeSpan.FromSeconds(15));

        var firstPid = harness.Status.Current.ProcessId;

        await WaitHelper.UntilAsync(
            () => harness.Status.Current.ProcessId is int pid
                  && pid != firstPid
                  && harness.Status.Current.Status == ApplicationStatus.Running,
            TimeSpan.FromSeconds(30),
            "TestApp should restart after --exit-after.");
    }

    [Fact]
    public async Task TestApp_health_fail_triggers_unhealthy_restart_when_enabled()
    {
        RequireWindows();
        var port = FreePort();
        var app = TestPaths.RequireTestAppExe();

        await using var harness = EngineHarness.Create(
            app,
            $"--health-fail --port {port}",
            configure: c =>
            {
                c.Health.Enabled = true;
                c.Health.Url = $"http://127.0.0.1:{port}/health";
                c.Monitoring.HealthCheckIntervalSeconds = 1;
                c.Monitoring.HealthTimeoutSeconds = 2;
                c.Restart.RestartDelaySeconds = 1;
            });

        await harness.StartEngineAsync();
        await WaitHelper.UntilAsync(
            () => harness.Status.Current.RestartCount >= 1
                  || harness.Status.Current.Status is ApplicationStatus.Unhealthy
                      or ApplicationStatus.Restarting
                      or ApplicationStatus.RestartLimitReached,
            TimeSpan.FromSeconds(40),
            "Health-fail TestApp should become unhealthy and restart.");
    }

    [Fact]
    public async Task Missing_executable_does_not_burn_restart_budget()
    {
        RequireWindows();
        var missing = Path.Combine(Path.GetTempPath(), "kw-missing-" + Guid.NewGuid().ToString("N") + ".exe");

        await using var harness = EngineHarness.Create(
            missing,
            "",
            configure: c => c.Restart.MaxRestarts = 2);

        await harness.StartEngineAsync();
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Equal(ApplicationStatus.NotConfigured, harness.Status.Current.Status);
        Assert.Equal(0, harness.Status.Current.RestartCount);
        Assert.False(harness.Status.Current.RestartLimitReached);
    }

    private static void RequireWindows()
        => Assert.True(OperatingSystem.IsWindows(), "Integration process tests require Windows.");

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[CollectionDefinition("IntegrationSerial", DisableParallelization = true)]
public class IntegrationSerialCollection;
