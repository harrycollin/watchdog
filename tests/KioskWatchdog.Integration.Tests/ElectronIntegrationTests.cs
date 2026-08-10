using System.Net.Http;
using KioskWatchdog.Core.Process;
using KioskWatchdog.Core.Status;
using KioskWatchdog.Integration.Tests.Support;

namespace KioskWatchdog.Integration.Tests;

[Collection("IntegrationSerial")]
public class ElectronIntegrationTests
{
    [Fact]
    public async Task Electron_fixture_counts_as_single_root_instance()
    {
        RequireWindows();
        var electron = TestPaths.TryFindElectronExe();
        var main = TestPaths.TryFindElectronMain();
        Assert.True(electron is not null && main is not null,
            "Electron fixture is not installed. CI should run npm install in fixtures/electron-health-app.");

        var workDir = Path.GetDirectoryName(main)!;
        var processes = new SystemProcessManager();

        // Clean up any leftovers from prior runs
        foreach (var existing in processes.FindByExecutablePath(electron!))
        {
            processes.Kill(existing.Id, entireProcessTree: true);
        }

        await Task.Delay(500);

        var started = processes.Start(
            electron!,
            $"\"{main}\"",
            workDir);

        try
        {
            await WaitHelper.UntilAsync(
                () => processes.FindByExecutablePath(electron!).Count >= 1,
                TimeSpan.FromSeconds(20),
                "Electron root process should appear.");

            // Give Chromium helpers time to spawn
            await Task.Delay(3000);

            var roots = processes.FindByExecutablePath(electron!);
            Assert.True(
                roots.Count == 1,
                $"Expected 1 Electron root instance, found {roots.Count} (helpers must be filtered).");
            Assert.Equal(started.Id, roots[0].Id);
        }
        finally
        {
            processes.Kill(started.Id, entireProcessTree: true);
            await Task.Delay(500);
        }
    }

    [Fact]
    public async Task Electron_fixture_is_monitored_and_reports_health()
    {
        RequireWindows();
        var electron = TestPaths.TryFindElectronExe();
        var main = TestPaths.TryFindElectronMain();
        Assert.True(electron is not null && main is not null,
            "Electron fixture is not installed. CI should run npm install in fixtures/electron-health-app.");

        var port = FreePort();
        var workDir = Path.GetDirectoryName(main)!;

        await using var harness = EngineHarness.Create(
            electron!,
            $"\"{main}\" --health-port={port}",
            workDir,
            c =>
            {
                var a = c.Primary();
                a.Health.Enabled = true;
                a.Health.Url = $"http://127.0.0.1:{port}/health";
                a.Monitoring.HealthCheckIntervalSeconds = 1;
                a.Monitoring.HealthTimeoutSeconds = 10;
                a.Application.DisplayName = "Electron Fixture";
            });

        await harness.StartEngineAsync();
        await WaitHelper.UntilAsync(
            () => harness.PrimaryStatus().Status == ApplicationStatus.Running
                  && harness.PrimaryStatus().ProcessId is not null,
            TimeSpan.FromSeconds(30),
            "Electron fixture should be running under the watchdog.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await WaitHelper.UntilAsync(
            () =>
            {
                try
                {
                    var response = http.GetAsync($"http://127.0.0.1:{port}/health").GetAwaiter().GetResult();
                    return response.IsSuccessStatusCode;
                }
                catch
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(30),
            "Electron /health should return HTTP 200.");

        await WaitHelper.UntilAsync(
            () => harness.PrimaryStatus().LastHealthCheckSucceeded == true,
            TimeSpan.FromSeconds(20),
            "Watchdog should observe a successful health check.");
    }

    private static void RequireWindows()
        => Assert.True(OperatingSystem.IsWindows(), "Electron integration tests require Windows.");

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
