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
            () => harness.PrimaryStatus().Status == ApplicationStatus.Running
                  && harness.PrimaryStatus().ProcessId is not null,
            TimeSpan.FromSeconds(20),
            "TestApp should be running.");

        Assert.Equal(ApplicationStatus.Running, harness.PrimaryStatus().Status);
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
                var a = c.Primary();
                a.Restart.RestartDelaySeconds = 1;
                a.Restart.MaxRestarts = 5;
            });

        await harness.StartEngineAsync();
        await WaitHelper.UntilAsync(
            () => harness.PrimaryStatus().RestartCount >= 2
                  || harness.PrimaryStatus().Status == ApplicationStatus.RestartLimitReached,
            TimeSpan.FromSeconds(30),
            "Crashing TestApp should cause restarts.");

        Assert.True(
            harness.PrimaryStatus().RestartCount >= 2
            || harness.PrimaryStatus().Status == ApplicationStatus.RestartLimitReached);
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
            configure: c => c.Primary().Restart.RestartDelaySeconds = 1);

        await harness.StartEngineAsync();
        await WaitHelper.UntilAsync(
            () => harness.PrimaryStatus().ProcessId is not null,
            TimeSpan.FromSeconds(15));

        var firstPid = harness.PrimaryStatus().ProcessId;

        await WaitHelper.UntilAsync(
            () => harness.PrimaryStatus().ProcessId is int pid
                  && pid != firstPid
                  && harness.PrimaryStatus().Status == ApplicationStatus.Running,
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
                var a = c.Primary();
                a.Health.Enabled = true;
                a.Health.Url = $"http://127.0.0.1:{port}/health";
                a.Monitoring.HealthCheckIntervalSeconds = 1;
                a.Monitoring.HealthTimeoutSeconds = 2;
                a.Restart.RestartDelaySeconds = 1;
            });

        await harness.StartEngineAsync();
        await WaitHelper.UntilAsync(
            () => harness.PrimaryStatus().RestartCount >= 1
                  || harness.PrimaryStatus().Status is ApplicationStatus.Unhealthy
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
            configure: c => c.Primary().Restart.MaxRestarts = 2);

        await harness.StartEngineAsync();
        await Task.Delay(TimeSpan.FromSeconds(4));

        Assert.Equal(ApplicationStatus.NotConfigured, harness.PrimaryStatus().Status);
        Assert.Equal(0, harness.PrimaryStatus().RestartCount);
        Assert.False(harness.PrimaryStatus().RestartLimitReached);
    }

    [Fact]
    public async Task Two_TestApp_copies_are_monitored_independently()
    {
        RequireWindows();
        var portA = FreePort();
        var portB = FreePort();
        var exeA = TestPaths.CopyTestAppAs("TestAppA.exe");
        var exeB = TestPaths.CopyTestAppAs("TestAppB.exe");

        await using var harness = EngineHarness.CreateMulti(
            exeA,
            $"--normal --port {portA}",
            exeB,
            $"--normal --port {portB}");

        await harness.StartEngineAsync();
        await WaitHelper.UntilAsync(
            () => harness.Status.Get("app-a")?.Status == ApplicationStatus.Running
                  && harness.Status.Get("app-b")?.Status == ApplicationStatus.Running,
            TimeSpan.FromSeconds(25),
            "Both TestApp copies should be running.");

        var pidA = harness.Status.Get("app-a")!.ProcessId;
        var pidB = harness.Status.Get("app-b")!.ProcessId;
        Assert.NotNull(pidA);
        Assert.NotNull(pidB);
        Assert.NotEqual(pidA, pidB);
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
