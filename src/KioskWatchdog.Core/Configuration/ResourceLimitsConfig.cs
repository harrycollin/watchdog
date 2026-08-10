namespace KioskWatchdog.Core.Configuration;

/// <summary>
/// Optional sustained RAM/CPU limits. Useful for long-running Electron/Chromium kiosks.
/// A limit of 0 disables that metric.
/// </summary>
public sealed class ResourceLimitsConfig
{
    public bool Enabled { get; set; }

    /// <summary>Restart when working set stays at/above this many MB. 0 = ignore memory.</summary>
    public int MaxMemoryMegabytes { get; set; }

    /// <summary>
    /// Restart when CPU stays at/above this percent of the machine (0–100+ for multi-core trees).
    /// 0 = ignore CPU.
    /// </summary>
    public int MaxCpuPercent { get; set; }

    /// <summary>How long a metric must stay over its limit before restarting.</summary>
    public int BreachDurationSeconds { get; set; } = 300;

    /// <summary>
    /// When true (default), sum memory/CPU across all processes sharing the executable path
    /// (Electron/Chromium helpers). When false, only the tracked root PID is measured.
    /// </summary>
    public bool IncludeChildProcesses { get; set; } = true;
}
