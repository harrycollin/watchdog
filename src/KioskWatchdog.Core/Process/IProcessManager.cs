namespace KioskWatchdog.Core.Process;

public sealed class ProcessInfo
{
    public required int Id { get; init; }
    public required string ProcessName { get; init; }
    public DateTimeOffset? StartTime { get; init; }
    public bool HasExited { get; init; }
    public int? ExitCode { get; init; }
}

public interface IProcessManager
{
    IReadOnlyList<ProcessInfo> FindByExecutablePath(string executablePath);
    ProcessInfo? GetById(int processId);
    ProcessInfo Start(string executablePath, string arguments, string workingDirectory);
    bool TryCloseMainWindow(int processId);
    bool WaitForExit(int processId, TimeSpan timeout);
    void Kill(int processId, bool entireProcessTree);
    bool IsRunning(int processId);
}
