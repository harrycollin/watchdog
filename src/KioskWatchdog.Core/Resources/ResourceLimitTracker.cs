using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Process;

namespace KioskWatchdog.Core.Resources;

public sealed class ResourceEvaluation
{
    public double? MemoryMegabytes { get; init; }
    public double? CpuPercent { get; init; }
    public int ProcessCount { get; init; }
    public bool MemoryBreaching { get; init; }
    public bool CpuBreaching { get; init; }
    public bool ShouldRestart { get; init; }
    public string? RestartReason { get; init; }
}

/// <summary>
/// Tracks sustained resource breaches across monitor ticks (needs two samples for CPU %).
/// </summary>
public sealed class ResourceLimitTracker
{
    private DateTimeOffset? _memoryBreachSince;
    private DateTimeOffset? _cpuBreachSince;
    private TimeSpan? _lastCpuTime;
    private DateTimeOffset? _lastSampleAt;

    public void Reset()
    {
        _memoryBreachSince = null;
        _cpuBreachSince = null;
        _lastCpuTime = null;
        _lastSampleAt = null;
    }

    public ResourceEvaluation Evaluate(
        ResourceLimitsConfig limits,
        ProcessResourceSample? sample,
        DateTimeOffset utcNow,
        int processorCount)
    {
        if (!limits.Enabled || sample is null)
        {
            Reset();
            return new ResourceEvaluation();
        }

        processorCount = Math.Max(1, processorCount);
        var memoryMb = sample.MemoryMegabytes;
        double? cpuPercent = null;

        if (_lastCpuTime is { } lastCpu && _lastSampleAt is { } lastAt)
        {
            var wall = utcNow - lastAt;
            var cpuDelta = sample.TotalProcessorTime - lastCpu;
            if (wall > TimeSpan.Zero && cpuDelta >= TimeSpan.Zero)
            {
                cpuPercent = 100.0 * cpuDelta.TotalSeconds / (wall.TotalSeconds * processorCount);
                if (cpuPercent < 0)
                    cpuPercent = 0;
            }
        }

        _lastCpuTime = sample.TotalProcessorTime;
        _lastSampleAt = utcNow;

        var memoryLimit = limits.MaxMemoryMegabytes;
        var cpuLimit = limits.MaxCpuPercent;
        var duration = TimeSpan.FromSeconds(Math.Max(1, limits.BreachDurationSeconds));

        var memoryOver = memoryLimit > 0 && memoryMb >= memoryLimit;
        var cpuOver = cpuLimit > 0 && cpuPercent is { } cpu && cpu >= cpuLimit;

        if (memoryOver)
            _memoryBreachSince ??= utcNow;
        else
            _memoryBreachSince = null;

        if (cpuOver)
            _cpuBreachSince ??= utcNow;
        else
            _cpuBreachSince = null;

        string? reason = null;
        if (_memoryBreachSince is { } memSince && utcNow - memSince >= duration)
            reason = $"Memory {memoryMb:0} MB ≥ {memoryLimit} MB for {limits.BreachDurationSeconds}s";
        else if (_cpuBreachSince is { } cpuSince && utcNow - cpuSince >= duration && cpuPercent is { } pct)
            reason = $"CPU {pct:0}% ≥ {cpuLimit}% for {limits.BreachDurationSeconds}s";

        return new ResourceEvaluation
        {
            MemoryMegabytes = memoryMb,
            CpuPercent = cpuPercent,
            ProcessCount = sample.ProcessCount,
            MemoryBreaching = memoryOver,
            CpuBreaching = cpuOver,
            ShouldRestart = reason is not null,
            RestartReason = reason
        };
    }
}
