using KioskWatchdog.Core.Health;
using KioskWatchdog.Core.Tests.Fakes;

namespace KioskWatchdog.Core.Tests.Health;

public class HealthMonitorTests
{
    [Fact]
    public void Http_200_is_healthy()
    {
        var clock = new FakeClock();
        var monitor = new HealthMonitor(clock);
        var evaluation = monitor.Evaluate(FakeHealthChecker.Ok(clock.UtcNow), TimeSpan.FromSeconds(45));

        Assert.Equal(HealthStatus.Healthy, evaluation.Status);
        Assert.False(evaluation.ShouldRestart);
        Assert.Equal(0, evaluation.ConsecutiveFailures);
    }

    [Fact]
    public void Http_failure_is_unhealthy_candidate_but_not_immediate_restart()
    {
        var clock = new FakeClock();
        var monitor = new HealthMonitor(clock);
        var evaluation = monitor.Evaluate(FakeHealthChecker.Fail(clock.UtcNow), TimeSpan.FromSeconds(45));

        Assert.Equal(HealthStatus.Unhealthy, evaluation.Status);
        Assert.False(evaluation.ShouldRestart);
        Assert.Equal(1, evaluation.ConsecutiveFailures);
    }

    [Fact]
    public void Temporary_failure_does_not_immediately_restart()
    {
        var clock = new FakeClock();
        var monitor = new HealthMonitor(clock);
        var timeout = TimeSpan.FromSeconds(45);

        monitor.Evaluate(FakeHealthChecker.Fail(clock.UtcNow), timeout);
        clock.Advance(TimeSpan.FromSeconds(10));
        var second = monitor.Evaluate(FakeHealthChecker.Fail(clock.UtcNow), timeout);

        Assert.False(second.ShouldRestart);
    }

    [Fact]
    public void Repeated_failures_beyond_timeout_trigger_restart()
    {
        var clock = new FakeClock();
        var monitor = new HealthMonitor(clock);
        var timeout = TimeSpan.FromSeconds(45);

        monitor.Evaluate(FakeHealthChecker.Fail(clock.UtcNow), timeout);
        clock.Advance(TimeSpan.FromSeconds(20));
        monitor.Evaluate(FakeHealthChecker.Fail(clock.UtcNow), timeout);
        clock.Advance(TimeSpan.FromSeconds(30));
        var final = monitor.Evaluate(FakeHealthChecker.Fail(clock.UtcNow), timeout);

        Assert.True(final.ShouldRestart);
        Assert.True(final.ConsecutiveFailures >= 3);
    }

    [Fact]
    public void Recovery_before_timeout_remains_healthy()
    {
        var clock = new FakeClock();
        var monitor = new HealthMonitor(clock);
        var timeout = TimeSpan.FromSeconds(45);

        monitor.Evaluate(FakeHealthChecker.Fail(clock.UtcNow), timeout);
        clock.Advance(TimeSpan.FromSeconds(20));
        var recovered = monitor.Evaluate(FakeHealthChecker.Ok(clock.UtcNow), timeout);

        Assert.Equal(HealthStatus.Healthy, recovered.Status);
        Assert.False(recovered.ShouldRestart);
        Assert.Equal(0, monitor.ConsecutiveFailures);
    }
}
