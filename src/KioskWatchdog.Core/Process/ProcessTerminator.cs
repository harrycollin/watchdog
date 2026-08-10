using Microsoft.Extensions.Logging;
using KioskWatchdog.Core.Configuration;

namespace KioskWatchdog.Core.Process;

public sealed class ProcessTerminator
{
    private readonly IProcessManager _processManager;
    private readonly ILogger<ProcessTerminator>? _logger;

    public ProcessTerminator(IProcessManager processManager, ILogger<ProcessTerminator>? logger = null)
    {
        _processManager = processManager;
        _logger = logger;
    }

    public async Task TerminateAsync(
        int processId,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        if (!_processManager.IsRunning(processId))
        {
            _logger?.LogInformation("Process PID {Pid} already exited before termination.", processId);
            return;
        }

        _logger?.LogInformation("Attempting graceful termination of PID {Pid}.", processId);
        _processManager.TryCloseMainWindow(processId);

        var gracefulDeadline = DateTime.UtcNow + gracefulTimeout;
        while (DateTime.UtcNow < gracefulDeadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_processManager.IsRunning(processId))
            {
                _logger?.LogInformation("Process PID {Pid} exited gracefully.", processId);
                return;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        if (_processManager.IsRunning(processId))
        {
            _logger?.LogWarning(
                "Process PID {Pid} did not exit within {Timeout}; force terminating process tree.",
                processId,
                gracefulTimeout);

            _processManager.Kill(processId, entireProcessTree: true);

            var forceDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < forceDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_processManager.IsRunning(processId))
                    break;

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        if (_processManager.IsRunning(processId))
        {
            throw new InvalidOperationException(
                $"Failed to confirm termination of process PID {processId}.");
        }

        _logger?.LogInformation("Process PID {Pid} terminated.", processId);
    }

    public Task TerminateAsync(
        int processId,
        MonitoringConfig monitoring,
        CancellationToken cancellationToken = default)
        => TerminateAsync(
            processId,
            TimeSpan.FromSeconds(monitoring.GracefulTerminationTimeoutSeconds),
            cancellationToken);
}
