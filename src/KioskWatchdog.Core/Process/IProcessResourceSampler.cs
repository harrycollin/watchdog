namespace KioskWatchdog.Core.Process;

public sealed class ProcessResourceSample
{
    public long WorkingSetBytes { get; init; }
    public TimeSpan TotalProcessorTime { get; init; }
    public int ProcessCount { get; init; }

    public double MemoryMegabytes => WorkingSetBytes / (1024.0 * 1024.0);
}

public interface IProcessResourceSampler
{
    /// <summary>Sample a single process by PID.</summary>
    ProcessResourceSample? SampleByPid(int processId);

    /// <summary>
    /// Sample all processes whose main module matches <paramref name="executablePath"/>
    /// (includes Electron/Chromium helpers).
    /// </summary>
    ProcessResourceSample? SampleByExecutablePath(string executablePath);
}
