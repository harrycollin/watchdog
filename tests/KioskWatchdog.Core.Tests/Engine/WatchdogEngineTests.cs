using KioskWatchdog.Core.Abstractions;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Engine;
using KioskWatchdog.Core.Health;
using KioskWatchdog.Core.Process;
using KioskWatchdog.Core.Status;
using KioskWatchdog.Core.Tests.Configuration;
using KioskWatchdog.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace KioskWatchdog.Core.Tests.Engine;

public class WatchdogEngineTests
{
    [Fact]
    public async Task Application_not_running_starts()
    {
        var harness = CreateHarness();
        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => harness.Processes.StartCallCount >= 1);
        await harness.Engine.StopAsync(CancellationToken.None);

        Assert.True(harness.Processes.StartCallCount >= 1);
        Assert.Equal(ApplicationStatus.Running, harness.PrimaryStatus().Status);
        Assert.NotNull(harness.PrimaryStatus().ProcessId);
    }

    [Fact]
    public async Task Application_already_running_does_not_start_duplicate()
    {
        var harness = CreateHarness();
        var existing = harness.Processes.Start(harness.Config.Primary().Application.ExecutablePath, "", "");
        var startsBefore = harness.Processes.StartCallCount;

        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => harness.PrimaryStatus().ProcessId == existing.Id);
        await Task.Delay(200);
        await harness.Engine.StopAsync(CancellationToken.None);

        Assert.Equal(startsBefore, harness.Processes.StartCallCount);
    }

    [Fact]
    public async Task Application_exits_then_restarts()
    {
        var harness = CreateHarness();
        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => harness.PrimaryStatus().ProcessId is not null);

        var pid = harness.PrimaryStatus().ProcessId!.Value;
        harness.Processes.SimulateExit(pid);

        await WaitForAsync(() => harness.Processes.StartCallCount >= 2, TimeSpan.FromSeconds(5));
        await harness.Engine.StopAsync(CancellationToken.None);

        Assert.True(harness.Processes.StartCallCount >= 2);
    }

    [Fact]
    public async Task Application_starts_successfully()
    {
        var harness = CreateHarness();
        await harness.Engine.StartApplicationAsync();

        Assert.Equal(1, harness.Processes.StartCallCount);
        Assert.Equal(ApplicationStatus.Running, harness.PrimaryStatus().Status);
    }

    [Fact]
    public async Task Application_fails_to_start_records_error()
    {
        var harness = CreateHarness();
        harness.Processes.FailNextStart = true;

        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() =>
            harness.PrimaryStatus().Status is ApplicationStatus.Error
                or ApplicationStatus.RestartLimitReached
                or ApplicationStatus.Restarting
            || harness.PrimaryStatus().LastError is not null,
            TimeSpan.FromSeconds(5));
        await harness.Engine.StopAsync(CancellationToken.None);

        Assert.NotNull(harness.PrimaryStatus().LastError);
    }

    [Fact]
    public async Task Unhealthy_application_is_terminated_and_restarted()
    {
        var harness = CreateHarness();
        var app = harness.Config.Primary();
        app.Monitoring.HealthCheckIntervalSeconds = 1;
        app.Monitoring.HealthTimeoutSeconds = 1;
        app.Monitoring.ProcessCheckIntervalSeconds = 1;
        app.Monitoring.GracefulTerminationTimeoutSeconds = 1;
        harness.Engine.ReloadConfiguration(harness.Config);

        harness.Processes.IgnoreGracefulClose = true;

        var t0 = DateTimeOffset.UtcNow;
        harness.Health.Enqueue(
            FakeHealthChecker.Fail(t0, "down"),
            FakeHealthChecker.Fail(t0.AddSeconds(2), "down"),
            FakeHealthChecker.Fail(t0.AddSeconds(3), "down"));
        harness.Health.SetDefault(FakeHealthChecker.Fail(message: "down"));

        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => harness.Processes.StartCallCount >= 1);

        await WaitForAsync(
            () => harness.Health.CallCount >= 2
                  && (harness.Processes.KillCallCount >= 1 || harness.Processes.StartCallCount >= 2),
            TimeSpan.FromSeconds(15));

        await harness.Engine.StopAsync(CancellationToken.None);

        Assert.True(harness.Health.CallCount >= 2);
        Assert.True(harness.Processes.KillCallCount >= 1 || harness.Processes.StartCallCount >= 2);
    }

    [Fact]
    public async Task Restart_limit_prevents_infinite_loop()
    {
        var harness = CreateHarness();
        var app = harness.Config.Primary();
        app.Restart.MaxRestarts = 2;
        app.Restart.RestartDelaySeconds = 0;
        app.Monitoring.ProcessCheckIntervalSeconds = 1;
        harness.Engine.ReloadConfiguration(harness.Config);
        harness.Processes.FailNextStart = true;

        await harness.Engine.StartAsync(CancellationToken.None);

        _ = Task.Run(async () =>
        {
            while (harness.PrimaryStatus().Status != ApplicationStatus.RestartLimitReached)
            {
                harness.Processes.FailNextStart = true;
                await Task.Delay(50);
            }
        });

        await WaitForAsync(
            () => harness.PrimaryStatus().Status == ApplicationStatus.RestartLimitReached,
            TimeSpan.FromSeconds(10));

        var startsAtLimit = harness.Processes.StartCallCount;
        await Task.Delay(500);
        await harness.Engine.StopAsync(CancellationToken.None);

        Assert.Equal(ApplicationStatus.RestartLimitReached, harness.PrimaryStatus().Status);
        Assert.True(harness.PrimaryStatus().RestartLimitReached);
        Assert.True(harness.Processes.StartCallCount <= startsAtLimit + 1);
    }

    [Fact]
    public async Task Manual_reset_allows_restarts_again()
    {
        var harness = CreateHarness();
        var app = harness.Config.Primary();
        app.Restart.MaxRestarts = 1;
        app.Restart.RestartDelaySeconds = 0;
        app.Monitoring.ProcessCheckIntervalSeconds = 1;
        harness.Engine.ReloadConfiguration(harness.Config);
        harness.Processes.FailNextStart = true;

        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(
            () => harness.PrimaryStatus().Status == ApplicationStatus.RestartLimitReached,
            TimeSpan.FromSeconds(10));

        harness.Processes.FailNextStart = false;
        harness.Engine.ResetRestartCounter();
        await harness.Engine.StartApplicationAsync();

        Assert.Equal(ApplicationStatus.Running, harness.PrimaryStatus().Status);
        Assert.False(harness.PrimaryStatus().RestartLimitReached);
        await harness.Engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Monitoring_recovers_to_normal_after_restart()
    {
        var harness = CreateHarness();
        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => harness.PrimaryStatus().ProcessId is not null);

        var pid = harness.PrimaryStatus().ProcessId!.Value;
        harness.Processes.SimulateExit(pid);

        await WaitForAsync(() =>
            harness.PrimaryStatus().Status == ApplicationStatus.Running
            && harness.PrimaryStatus().ProcessId is not null
            && harness.PrimaryStatus().ProcessId != pid);

        Assert.Equal(ApplicationStatus.Running, harness.PrimaryStatus().Status);
        await harness.Engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Two_apps_restart_independently()
    {
        var harness = CreateMultiHarness();
        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() =>
            harness.Status.Get("app-a")?.ProcessId is not null
            && harness.Status.Get("app-b")?.ProcessId is not null);

        var statusBBefore = harness.Status.Get("app-b")!;
        var restartsB = statusBBefore.RestartCount;
        var pidA = harness.Status.Get("app-a")!.ProcessId!.Value;

        harness.Processes.SimulateExit(pidA);

        await WaitForAsync(() =>
            harness.Status.Get("app-a")?.RestartCount >= 1
            && harness.Status.Get("app-a")?.ProcessId is int pid
            && pid != pidA,
            TimeSpan.FromSeconds(5));

        var statusBAfter = harness.Status.Get("app-b")!;
        Assert.Equal(restartsB, statusBAfter.RestartCount);
        Assert.Equal(statusBBefore.ProcessId, statusBAfter.ProcessId);
        Assert.Equal(ApplicationStatus.Running, statusBAfter.Status);

        await harness.Engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Missing_exe_on_one_app_does_not_affect_the_other()
    {
        var harness = CreateMultiHarness(missingA: true);
        await harness.Engine.StartAsync(CancellationToken.None);

        await WaitForAsync(() =>
            harness.Status.Get("app-a")?.Status == ApplicationStatus.NotConfigured
            && harness.Status.Get("app-b")?.Status == ApplicationStatus.Running,
            TimeSpan.FromSeconds(5));

        Assert.Equal(ApplicationStatus.NotConfigured, harness.Status.Get("app-a")!.Status);
        Assert.Equal(ApplicationStatus.Running, harness.Status.Get("app-b")!.Status);
        Assert.True(harness.Processes.StartCallCount >= 1);

        await harness.Engine.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Http_app_starts_shell_command_when_health_is_down()
    {
        var harness = CreateHttpHarness();
        harness.Health.SetDefault(FakeHealthChecker.Fail(message: "down"));

        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => harness.Processes.ShellStartCallCount >= 1, TimeSpan.FromSeconds(5));
        await harness.Engine.StopAsync(CancellationToken.None);

        Assert.True(harness.Processes.ShellStartCallCount >= 1);
        Assert.Equal("npm start", harness.Processes.LastShellCommand);
        Assert.Equal(0, harness.Processes.StartCallCount);
    }

    [Fact]
    public async Task Http_app_does_not_start_when_health_already_ok()
    {
        var harness = CreateHttpHarness();
        harness.Health.SetDefault(FakeHealthChecker.Ok());

        await harness.Engine.StartApplicationAsync("site");
        await Task.Delay(200);

        Assert.Equal(0, harness.Processes.ShellStartCallCount);
        Assert.Equal(ApplicationStatus.Running, harness.Status.Get("site")!.Status);
    }

    [Fact]
    public async Task Http_app_stop_runs_stop_command_when_configured()
    {
        var harness = CreateHttpHarness();
        harness.Config.Primary().Http.StopCommand = "echo stop";
        harness.Engine.ReloadConfiguration(harness.Config);
        harness.Health.SetDefault(FakeHealthChecker.Fail(message: "down"));

        await harness.Engine.StartApplicationAsync("site");
        Assert.True(harness.Processes.ShellStartCallCount >= 1);

        await harness.Engine.StopApplicationAsync("site");
        Assert.Contains("echo stop", harness.Processes.LastShellCommand);
        Assert.Equal(ApplicationStatus.Stopped, harness.Status.Get("site")!.Status);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(50);
        }

        Assert.True(condition(), "Condition not met before timeout.");
    }

    private static Harness CreateHarness()
    {
        var config = ConfigValidatorTests.CreateValid();
        var exe = Path.Combine(Path.GetTempPath(), "FakeKiosk-" + Guid.NewGuid() + ".exe");
        File.WriteAllText(exe, "fake");
        var app = config.Primary();
        app.Application.ExecutablePath = exe;
        app.Application.WorkingDirectory = Path.GetDirectoryName(exe)!;
        app.Restart.RestartDelaySeconds = 0;
        app.Monitoring.ProcessCheckIntervalSeconds = 1;
        app.Monitoring.HealthCheckIntervalSeconds = 30;
        app.Monitoring.HealthTimeoutSeconds = 45;
        app.Health.Enabled = true;

        var clock = new SystemClock();
        var processes = new FakeProcessManager();
        var health = new FakeHealthChecker();
        health.SetDefault(FakeHealthChecker.Ok());
        var status = new WatchdogStatusStore();
        var configStore = new InMemoryConfigStore(config);
        var terminator = new ProcessTerminator(processes, NullLogger<ProcessTerminator>.Instance);

        var engine = new WatchdogEngine(
            processes,
            terminator,
            health,
            status,
            configStore,
            clock,
            NullLogger<WatchdogEngine>.Instance,
            config);

        return new Harness(engine, processes, health, status, config, exe);
    }

    private static Harness CreateMultiHarness(bool missingA = false)
    {
        var exeA = Path.Combine(Path.GetTempPath(), "FakeKioskA-" + Guid.NewGuid() + ".exe");
        var exeB = Path.Combine(Path.GetTempPath(), "FakeKioskB-" + Guid.NewGuid() + ".exe");
        File.WriteAllText(exeA, "fake-a");
        File.WriteAllText(exeB, "fake-b");

        var config = new WatchdogConfig
        {
            Applications =
            {
                new MonitoredApplicationConfig
                {
                    Id = "app-a",
                    Enabled = true,
                    Application =
                    {
                        ExecutablePath = missingA ? Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid() + ".exe") : exeA,
                        WorkingDirectory = Path.GetDirectoryName(exeA)!,
                        DisplayName = "App A"
                    },
                    Monitoring = { ProcessCheckIntervalSeconds = 1, HealthCheckIntervalSeconds = 30, HealthTimeoutSeconds = 45, GracefulTerminationTimeoutSeconds = 2 },
                    Restart = { RestartDelaySeconds = 0, MaxRestarts = 5, RestartWindowMinutes = 10 },
                    Health = { Enabled = false }
                },
                new MonitoredApplicationConfig
                {
                    Id = "app-b",
                    Enabled = true,
                    Application =
                    {
                        ExecutablePath = exeB,
                        WorkingDirectory = Path.GetDirectoryName(exeB)!,
                        DisplayName = "App B"
                    },
                    Monitoring = { ProcessCheckIntervalSeconds = 1, HealthCheckIntervalSeconds = 30, HealthTimeoutSeconds = 45, GracefulTerminationTimeoutSeconds = 2 },
                    Restart = { RestartDelaySeconds = 0, MaxRestarts = 5, RestartWindowMinutes = 10 },
                    Health = { Enabled = false }
                }
            }
        };

        var clock = new SystemClock();
        var processes = new FakeProcessManager();
        var health = new FakeHealthChecker();
        health.SetDefault(FakeHealthChecker.Ok());
        var status = new WatchdogStatusStore();
        var configStore = new InMemoryConfigStore(config);
        var terminator = new ProcessTerminator(processes, NullLogger<ProcessTerminator>.Instance);

        var engine = new WatchdogEngine(
            processes,
            terminator,
            health,
            status,
            configStore,
            clock,
            NullLogger<WatchdogEngine>.Instance,
            config);

        return new Harness(engine, processes, health, status, config, exeB);
    }

    private static Harness CreateHttpHarness()
    {
        var config = ConfigValidatorTests.CreateValidHttp();
        var clock = new SystemClock();
        var processes = new FakeProcessManager();
        var health = new FakeHealthChecker();
        health.SetDefault(FakeHealthChecker.Fail(message: "down"));
        var status = new WatchdogStatusStore();
        var configStore = new InMemoryConfigStore(config);
        var terminator = new ProcessTerminator(processes, NullLogger<ProcessTerminator>.Instance);

        var engine = new WatchdogEngine(
            processes,
            terminator,
            health,
            status,
            configStore,
            clock,
            NullLogger<WatchdogEngine>.Instance,
            config);

        return new Harness(engine, processes, health, status, config, config.Primary().Http.WorkingDirectory);
    }

    private sealed class Harness
    {
        public Harness(
            WatchdogEngine engine,
            FakeProcessManager processes,
            FakeHealthChecker health,
            IWatchdogStatusStore status,
            WatchdogConfig config,
            string exePath)
        {
            Engine = engine;
            Processes = processes;
            Health = health;
            Status = status;
            Config = config;
            ExePath = exePath;
        }

        public WatchdogEngine Engine { get; }
        public FakeProcessManager Processes { get; }
        public FakeHealthChecker Health { get; }
        public IWatchdogStatusStore Status { get; }
        public WatchdogConfig Config { get; }
        public string ExePath { get; }

        public WatchdogStatus PrimaryStatus()
            => Status.Get(WatchdogConfig.DefaultApplicationId)
               ?? Status.All.FirstOrDefault()
               ?? new WatchdogStatus();
    }

    private sealed class InMemoryConfigStore : IConfigStore
    {
        private WatchdogConfig _config;
        public InMemoryConfigStore(WatchdogConfig config) => _config = config;
        public string ConfigPath => "memory://config";
        public WatchdogConfig Load() => _config;
        public void Save(WatchdogConfig config) => _config = config;
    }
}
