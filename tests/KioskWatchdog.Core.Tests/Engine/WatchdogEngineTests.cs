using KioskWatchdog.Core.Abstractions;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Engine;
using KioskWatchdog.Core.Health;
using KioskWatchdog.Core.Process;
using KioskWatchdog.Core.Restart;
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
        Assert.Equal(ApplicationStatus.Running, harness.Status.Current.Status);
        Assert.NotNull(harness.Status.Current.ProcessId);
    }

    [Fact]
    public async Task Application_already_running_does_not_start_duplicate()
    {
        var harness = CreateHarness();
        var existing = harness.Processes.Start(harness.Config.Application.ExecutablePath, "", "");
        var startsBefore = harness.Processes.StartCallCount;

        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => harness.Status.Current.ProcessId == existing.Id);
        await Task.Delay(200);
        await harness.Engine.StopAsync(CancellationToken.None);

        Assert.Equal(startsBefore, harness.Processes.StartCallCount);
    }

    [Fact]
    public async Task Application_exits_then_restarts()
    {
        var harness = CreateHarness();
        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => harness.Status.Current.ProcessId is not null);

        var pid = harness.Status.Current.ProcessId!.Value;
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
        Assert.Equal(ApplicationStatus.Running, harness.Status.Current.Status);
    }

    [Fact]
    public async Task Application_fails_to_start_records_error()
    {
        var harness = CreateHarness();
        harness.Processes.FailNextStart = true;

        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() =>
            harness.Status.Current.Status is ApplicationStatus.Error
                or ApplicationStatus.RestartLimitReached
                or ApplicationStatus.Restarting
            || harness.Status.Current.LastError is not null,
            TimeSpan.FromSeconds(5));
        await harness.Engine.StopAsync(CancellationToken.None);

        Assert.NotNull(harness.Status.Current.LastError);
    }

    [Fact]
    public async Task Unhealthy_application_is_terminated_and_restarted()
    {
        var harness = CreateHarness();
        harness.Config.Monitoring.HealthCheckIntervalSeconds = 1;
        harness.Config.Monitoring.HealthTimeoutSeconds = 1;
        harness.Config.Monitoring.ProcessCheckIntervalSeconds = 1;
        harness.Config.Monitoring.GracefulTerminationTimeoutSeconds = 1;
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
        harness.Config.Restart.MaxRestarts = 2;
        harness.Config.Restart.RestartDelaySeconds = 0;
        harness.Config.Monitoring.ProcessCheckIntervalSeconds = 1;
        harness.Engine.ReloadConfiguration(harness.Config);
        harness.Processes.FailNextStart = true;

        // Make every start fail by exiting immediately after start — use fail start repeatedly
        await harness.Engine.StartAsync(CancellationToken.None);

        // Keep failing starts
        _ = Task.Run(async () =>
        {
            while (harness.Status.Current.Status != ApplicationStatus.RestartLimitReached)
            {
                harness.Processes.FailNextStart = true;
                await Task.Delay(50);
            }
        });

        await WaitForAsync(
            () => harness.Status.Current.Status == ApplicationStatus.RestartLimitReached,
            TimeSpan.FromSeconds(10));

        var startsAtLimit = harness.Processes.StartCallCount;
        await Task.Delay(500);
        await harness.Engine.StopAsync(CancellationToken.None);

        Assert.Equal(ApplicationStatus.RestartLimitReached, harness.Status.Current.Status);
        Assert.True(harness.Status.Current.RestartLimitReached);
        // Should not keep climbing unbounded
        Assert.True(harness.Processes.StartCallCount <= startsAtLimit + 1);
    }

    [Fact]
    public async Task Manual_reset_allows_restarts_again()
    {
        var harness = CreateHarness();
        harness.Config.Restart.MaxRestarts = 1;
        harness.Config.Restart.RestartDelaySeconds = 0;
        harness.Engine.ReloadConfiguration(harness.Config);

        harness.Restart.RecordRestart(1, TimeSpan.FromMinutes(10));
        Assert.False(harness.Restart.CanRestart(1, TimeSpan.FromMinutes(10)));

        harness.Engine.ResetRestartCounter();
        Assert.True(harness.Restart.CanRestart(1, TimeSpan.FromMinutes(10)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Monitoring_recovers_to_normal_after_restart()
    {
        var harness = CreateHarness();
        await harness.Engine.StartAsync(CancellationToken.None);
        await WaitForAsync(() => harness.Status.Current.ProcessId is not null);

        var pid = harness.Status.Current.ProcessId!.Value;
        harness.Processes.SimulateExit(pid);

        await WaitForAsync(() =>
            harness.Status.Current.Status == ApplicationStatus.Running
            && harness.Status.Current.ProcessId is not null
            && harness.Status.Current.ProcessId != pid);

        Assert.Equal(ApplicationStatus.Running, harness.Status.Current.Status);
        await harness.Engine.StopAsync(CancellationToken.None);
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
        // Point executable to a real temp file so requireExistingExecutable can pass when StartApplication validates
        var exe = Path.Combine(Path.GetTempPath(), "FakeKiosk-" + Guid.NewGuid() + ".exe");
        File.WriteAllText(exe, "fake");
        config.Application.ExecutablePath = exe;
        config.Application.WorkingDirectory = Path.GetDirectoryName(exe)!;
        config.Restart.RestartDelaySeconds = 0;
        config.Monitoring.ProcessCheckIntervalSeconds = 1;
        config.Monitoring.HealthCheckIntervalSeconds = 30;
        config.Monitoring.HealthTimeoutSeconds = 45;
        config.Health.Enabled = true;

        // Use system clock so monitoring/health intervals advance in real time.
        var clock = new SystemClock();
        var processes = new FakeProcessManager();
        var health = new FakeHealthChecker();
        health.SetDefault(FakeHealthChecker.Ok());
        var healthMonitor = new HealthMonitor(clock);
        var restart = new RestartManager(clock);
        var status = new WatchdogStatusStore();
        var configStore = new InMemoryConfigStore(config);
        var terminator = new ProcessTerminator(processes, NullLogger<ProcessTerminator>.Instance);

        var engine = new WatchdogEngine(
            processes,
            terminator,
            health,
            healthMonitor,
            restart,
            status,
            configStore,
            clock,
            NullLogger<WatchdogEngine>.Instance,
            config);

        return new Harness(engine, processes, health, restart, status, config, exe);
    }

    private sealed class Harness
    {
        public Harness(
            WatchdogEngine engine,
            FakeProcessManager processes,
            FakeHealthChecker health,
            RestartManager restart,
            IWatchdogStatusStore status,
            WatchdogConfig config,
            string exePath)
        {
            Engine = engine;
            Processes = processes;
            Health = health;
            Restart = restart;
            Status = status;
            Config = config;
            ExePath = exePath;
        }

        public WatchdogEngine Engine { get; }
        public FakeProcessManager Processes { get; }
        public FakeHealthChecker Health { get; }
        public RestartManager Restart { get; }
        public IWatchdogStatusStore Status { get; }
        public WatchdogConfig Config { get; }
        public string ExePath { get; }
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
