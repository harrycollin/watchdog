using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
        var matched = new List<(ProcessInfo Info, int Pid, int? ParentPid)>();

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
                    // Access denied or process exited
                }

                if (processPath is null || !PathsEqual(processPath, targetFullPath))
                    continue;

                var parentPid = TryGetParentProcessId(process.Id);
                matched.Add((ToProcessInfo(process), process.Id, parentPid));
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

        // Electron/Chromium spawn helper processes with the same .exe path.
        // Only treat processes whose parent is NOT also this executable as app instances.
        var matchedIds = matched.Select(m => m.Pid).ToHashSet();
        var roots = matched
            .Where(m => m.ParentPid is null || !matchedIds.Contains(m.ParentPid.Value))
            .Select(m => m.Info)
            .ToList();

        if (matched.Count > roots.Count)
        {
            _logger?.LogDebug(
                "Matched {Total} processes for {Path}; {Roots} root instance(s) after filtering helpers.",
                matched.Count,
                executablePath,
                roots.Count);
        }

        return roots;
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

        var workDir = ResolveWorkingDirectory(executablePath, workingDirectory);

        if (OperatingSystem.IsWindows() && InteractiveSessionLauncher.IsRunningInSession0())
        {
            _logger?.LogInformation(
                "Watchdog is in Session 0; launching {Path} into the active interactive session.",
                executablePath);
            return InteractiveSessionLauncher.Start(executablePath, arguments ?? string.Empty, workDir, _logger);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = workDir
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

    private static int? TryGetParentProcessId(int processId)
    {
        if (OperatingSystem.IsWindows())
            return TryGetParentProcessIdWindows(processId);

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static int? TryGetParentProcessIdWindows(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            var pbi = new PROCESS_BASIC_INFORMATION();
            var status = NtQueryInformationProcess(
                process.Handle,
                0, // ProcessBasicInformation
                ref pbi,
                Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(),
                out _);

            if (status != 0)
                return null;

            var parent = (int)pbi.InheritedFromUniqueProcessId.ToInt64();
            return parent > 0 ? parent : null;
        }
        catch
        {
            return null;
        }
    }

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

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}
