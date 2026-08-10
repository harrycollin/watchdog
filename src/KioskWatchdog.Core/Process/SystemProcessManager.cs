using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Process;

public sealed class SystemProcessManager : IProcessManager
{
    private readonly ILogger<SystemProcessManager>? _logger;

    public SystemProcessManager(ILogger<SystemProcessManager>? logger = null)
    {
        _logger = logger;
    }

    public IReadOnlyList<ProcessInfo> FindByExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var targetFullPath = Path.GetFullPath(executablePath);
        var targetFileName = Path.GetFileNameWithoutExtension(targetFullPath);
        var results = new List<ProcessInfo>();

        foreach (var process in System.Diagnostics.Process.GetProcessesByName(targetFileName))
        {
            try
            {
                string? processPath = null;
                try
                {
                    processPath = process.MainModule?.FileName;
                }
                catch (Exception)
                {
                    // Access denied or process exited — skip path match, fall back to name-only match carefully.
                }

                if (processPath is not null)
                {
                    if (!PathsEqual(processPath, targetFullPath))
                        continue;
                }
                else
                {
                    // Without a reliable path, only match when the executable name is unique enough.
                    // Prefer not matching unknown processes blindly.
                    continue;
                }

                results.Add(ToProcessInfo(process));
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Skipping process while enumerating for {Path}", executablePath);
            }
            finally
            {
                process.Dispose();
            }
        }

        return results;
    }

    public ProcessInfo? GetById(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return ToProcessInfo(process);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public ProcessInfo Start(string executablePath, string arguments, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!File.Exists(executablePath))
            throw new FileNotFoundException("Configured executable was not found.", executablePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = ResolveWorkingDirectory(executablePath, workingDirectory)
        };

        var process = System.Diagnostics.Process.Start(startInfo)
                      ?? throw new InvalidOperationException($"Failed to start process: {executablePath}");

        try
        {
            _logger?.LogInformation(
                "Started process {Name} (PID {Pid}) from {Path}",
                process.ProcessName,
                process.Id,
                executablePath);

            return ToProcessInfo(process);
        }
        finally
        {
            process.Dispose();
        }
    }

    public bool TryCloseMainWindow(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited)
                return true;

            return process.CloseMainWindow();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "CloseMainWindow failed for PID {Pid}", processId);
            return false;
        }
    }

    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited)
                return true;

            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    public void Kill(int processId, bool entireProcessTree)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            if (process.HasExited)
                return;

            process.Kill(entireProcessTree);
            _logger?.LogWarning(
                "Force-terminated process PID {Pid} (entireTree={EntireTree})",
                processId,
                entireProcessTree);
        }
        catch (ArgumentException)
        {
            // Already gone
        }
        catch (InvalidOperationException)
        {
            // Already gone
        }
    }

    public bool IsRunning(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string ResolveWorkingDirectory(string executablePath, string workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            return workingDirectory;

        return Path.GetDirectoryName(Path.GetFullPath(executablePath))
               ?? Environment.CurrentDirectory;
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static ProcessInfo ToProcessInfo(System.Diagnostics.Process process)
    {
        DateTimeOffset? startTime = null;
        try
        {
            startTime = process.StartTime.ToUniversalTime();
        }
        catch (Exception)
        {
            // May fail for some system processes
        }

        var hasExited = false;
        int? exitCode = null;
        try
        {
            hasExited = process.HasExited;
            if (hasExited)
                exitCode = process.ExitCode;
        }
        catch (Exception)
        {
            // Ignore
        }

        return new ProcessInfo
        {
            Id = process.Id,
            ProcessName = process.ProcessName,
            StartTime = startTime,
            HasExited = hasExited,
            ExitCode = exitCode
        };
    }
}
