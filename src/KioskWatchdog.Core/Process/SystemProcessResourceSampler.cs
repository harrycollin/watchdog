using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Process;

public sealed class SystemProcessResourceSampler : IProcessResourceSampler
{
    private readonly ILogger<SystemProcessResourceSampler>? _logger;

    public SystemProcessResourceSampler(ILogger<SystemProcessResourceSampler>? logger = null)
    {
        _logger = logger;
    }

    public ProcessResourceSample? SampleByPid(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited)
                return null;

            return new ProcessResourceSample
            {
                WorkingSetBytes = SafeWorkingSet(process),
                TotalProcessorTime = SafeCpuTime(process),
                ProcessCount = 1
            };
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed sampling PID {Pid}.", processId);
            return null;
        }
    }

    public ProcessResourceSample? SampleByExecutablePath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        string targetFullPath;
        try
        {
            targetFullPath = Path.GetFullPath(executablePath);
        }
        catch
        {
            return null;
        }

        var targetFileName = Path.GetFileNameWithoutExtension(targetFullPath);
        long workingSet = 0;
        var cpu = TimeSpan.Zero;
        var count = 0;

        foreach (var process in System.Diagnostics.Process.GetProcessesByName(targetFileName))
        {
            try
            {
                if (process.HasExited)
                    continue;

                string? processPath = null;
                try
                {
                    processPath = process.MainModule?.FileName;
                }
                catch
                {
                    // Access denied / exited
                }

                if (processPath is null
                    || !string.Equals(
                        Path.GetFullPath(processPath),
                        targetFullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                workingSet += SafeWorkingSet(process);
                cpu += SafeCpuTime(process);
                count++;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Skipping process while sampling {Path}.", executablePath);
            }
            finally
            {
                process.Dispose();
            }
        }

        if (count == 0)
            return null;

        return new ProcessResourceSample
        {
            WorkingSetBytes = workingSet,
            TotalProcessorTime = cpu,
            ProcessCount = count
        };
    }

    private static long SafeWorkingSet(System.Diagnostics.Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    private static TimeSpan SafeCpuTime(System.Diagnostics.Process process)
    {
        try
        {
            return process.TotalProcessorTime;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }
}
