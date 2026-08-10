using KioskWatchdog.Core.Process;

namespace KioskWatchdog.Core.Tests.Fakes;

internal sealed class FakeProcessManager : IProcessManager
{
    private readonly Dictionary<int, TrackedProcess> _processes = new();
    private int _nextPid = 1000;

    public int StartCallCount { get; private set; }
    public int KillCallCount { get; private set; }
    public bool FailNextStart { get; set; }
    public TimeSpan GracefulExitDelay { get; set; } = TimeSpan.Zero;
    public bool IgnoreGracefulClose { get; set; }

    public IReadOnlyList<ProcessInfo> FindByExecutablePath(string executablePath)
    {
        return _processes.Values
            .Where(p => !p.HasExited && PathsEqual(p.ExecutablePath, executablePath))
            .Select(ToInfo)
            .ToList();
    }

    public ProcessInfo? GetById(int processId)
        => _processes.TryGetValue(processId, out var p) ? ToInfo(p) : null;

    public ProcessInfo Start(string executablePath, string arguments, string workingDirectory)
    {
        StartCallCount++;
        if (FailNextStart)
        {
            FailNextStart = false;
            throw new InvalidOperationException("Simulated start failure.");
        }

        var pid = _nextPid++;
        var process = new TrackedProcess
        {
            Id = pid,
            ExecutablePath = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            StartTime = DateTimeOffset.UtcNow,
            ProcessName = Path.GetFileNameWithoutExtension(executablePath)
        };
        _processes[pid] = process;
        return ToInfo(process);
    }

    public bool TryCloseMainWindow(int processId)
    {
        if (!_processes.TryGetValue(processId, out var process) || process.HasExited)
            return true;

        if (IgnoreGracefulClose)
            return false;

        process.CloseRequestedAt = DateTimeOffset.UtcNow;
        if (GracefulExitDelay <= TimeSpan.Zero)
        {
            process.HasExited = true;
            process.ExitCode = 0;
        }

        return true;
    }

    public bool WaitForExit(int processId, TimeSpan timeout)
    {
        if (!_processes.TryGetValue(processId, out var process))
            return true;

        if (process.HasExited)
            return true;

        if (process.CloseRequestedAt is not null
            && GracefulExitDelay > TimeSpan.Zero
            && GracefulExitDelay <= timeout)
        {
            process.HasExited = true;
            process.ExitCode = 0;
            return true;
        }

        return process.HasExited;
    }

    public void Kill(int processId, bool entireProcessTree)
    {
        KillCallCount++;
        if (_processes.TryGetValue(processId, out var process))
        {
            process.HasExited = true;
            process.ExitCode = -1;
            process.Killed = true;
            process.EntireTree = entireProcessTree;
        }
    }

    public bool IsRunning(int processId)
        => _processes.TryGetValue(processId, out var process) && !process.HasExited;

    public void SimulateExit(int processId, int exitCode = 1)
    {
        if (_processes.TryGetValue(processId, out var process))
        {
            process.HasExited = true;
            process.ExitCode = exitCode;
        }
    }

    public void SimulateGracefulTimeoutElapsed(int processId)
    {
        if (_processes.TryGetValue(processId, out var process)
            && process.CloseRequestedAt is not null
            && !process.HasExited)
        {
            // Still running after graceful attempt — tests force-kill path
        }
    }

    private static ProcessInfo ToInfo(TrackedProcess process) => new()
    {
        Id = process.Id,
        ProcessName = process.ProcessName,
        StartTime = process.StartTime,
        HasExited = process.HasExited,
        ExitCode = process.ExitCode
    };

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private sealed class TrackedProcess
    {
        public required int Id { get; init; }
        public required string ExecutablePath { get; init; }
        public string Arguments { get; init; } = "";
        public string WorkingDirectory { get; init; } = "";
        public required string ProcessName { get; init; }
        public DateTimeOffset StartTime { get; init; }
        public bool HasExited { get; set; }
        public int? ExitCode { get; set; }
        public DateTimeOffset? CloseRequestedAt { get; set; }
        public bool Killed { get; set; }
        public bool EntireTree { get; set; }
    }
}
