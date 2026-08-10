using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Process;
using KioskWatchdog.Core.Resources;

namespace KioskWatchdog.Core.Tests.Resources;

public class ResourceLimitTrackerTests
{
    [Fact]
    public void Memory_breach_restarts_after_duration()
    {
        var tracker = new ResourceLimitTracker();
        var limits = new ResourceLimitsConfig
        {
            Enabled = true,
            MaxMemoryMegabytes = 1000,
            BreachDurationSeconds = 60
        };

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var sample = new ProcessResourceSample
        {
            WorkingSetBytes = 1500L * 1024 * 1024,
            TotalProcessorTime = TimeSpan.FromSeconds(1),
            ProcessCount = 1
        };

        var first = tracker.Evaluate(limits, sample, t0, processorCount: 4);
        Assert.True(first.MemoryBreaching);
        Assert.False(first.ShouldRestart);

        var early = tracker.Evaluate(limits, sample, t0.AddSeconds(30), processorCount: 4);
        Assert.False(early.ShouldRestart);

        var late = tracker.Evaluate(limits, sample, t0.AddSeconds(60), processorCount: 4);
        Assert.True(late.ShouldRestart);
        Assert.Contains("Memory", late.RestartReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Memory_breach_resets_when_usage_drops()
    {
        var tracker = new ResourceLimitTracker();
        var limits = new ResourceLimitsConfig
        {
            Enabled = true,
            MaxMemoryMegabytes = 1000,
            BreachDurationSeconds = 60
        };

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var high = new ProcessResourceSample
        {
            WorkingSetBytes = 1500L * 1024 * 1024,
            TotalProcessorTime = TimeSpan.Zero,
            ProcessCount = 1
        };
        var low = new ProcessResourceSample
        {
            WorkingSetBytes = 200L * 1024 * 1024,
            TotalProcessorTime = TimeSpan.Zero,
            ProcessCount = 1
        };

        tracker.Evaluate(limits, high, t0, 4);
        tracker.Evaluate(limits, low, t0.AddSeconds(30), 4);
        var again = tracker.Evaluate(limits, high, t0.AddSeconds(60), 4);
        Assert.False(again.ShouldRestart);
    }

    [Fact]
    public void Cpu_percent_uses_sample_delta()
    {
        var tracker = new ResourceLimitTracker();
        var limits = new ResourceLimitsConfig
        {
            Enabled = true,
            MaxCpuPercent = 50,
            BreachDurationSeconds = 10
        };

        var t0 = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        // 4 cores, 1s wall, 2s CPU time => 50% of machine
        var s0 = new ProcessResourceSample
        {
            WorkingSetBytes = 1,
            TotalProcessorTime = TimeSpan.FromSeconds(0),
            ProcessCount = 1
        };
        var s1 = new ProcessResourceSample
        {
            WorkingSetBytes = 1,
            TotalProcessorTime = TimeSpan.FromSeconds(2),
            ProcessCount = 1
        };

        var first = tracker.Evaluate(limits, s0, t0, processorCount: 4);
        Assert.Null(first.CpuPercent);

        var second = tracker.Evaluate(limits, s1, t0.AddSeconds(1), processorCount: 4);
        Assert.NotNull(second.CpuPercent);
        Assert.InRange(second.CpuPercent!.Value, 49, 51);
        Assert.True(second.CpuBreaching);
        Assert.False(second.ShouldRestart);

        var third = tracker.Evaluate(
            limits,
            new ProcessResourceSample
            {
                WorkingSetBytes = 1,
                TotalProcessorTime = TimeSpan.FromSeconds(4),
                ProcessCount = 1
            },
            t0.AddSeconds(2),
            processorCount: 4);
        // Still over; breach started at second sample (t0+1s), duration 10s — not yet
        Assert.False(third.ShouldRestart);

        var fourth = tracker.Evaluate(
            limits,
            new ProcessResourceSample
            {
                WorkingSetBytes = 1,
                TotalProcessorTime = TimeSpan.FromSeconds(24),
                ProcessCount = 1
            },
            t0.AddSeconds(12),
            processorCount: 4);
        Assert.True(fourth.ShouldRestart);
        Assert.Contains("CPU", fourth.RestartReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Disabled_limits_clear_state()
    {
        var tracker = new ResourceLimitTracker();
        var limits = new ResourceLimitsConfig { Enabled = false, MaxMemoryMegabytes = 1 };
        var sample = new ProcessResourceSample
        {
            WorkingSetBytes = 10L * 1024 * 1024,
            TotalProcessorTime = TimeSpan.Zero,
            ProcessCount = 1
        };

        var result = tracker.Evaluate(limits, sample, DateTimeOffset.UtcNow, 4);
        Assert.Null(result.MemoryMegabytes);
        Assert.False(result.ShouldRestart);
    }
}
