using KioskWatchdog.Core.Abstractions;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Health;
using KioskWatchdog.Core.Process;
using KioskWatchdog.Core.Restart;
using KioskWatchdog.Core.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Engine;

public sealed class WatchdogEngine : BackgroundService
{
    private readonly IProcessManager _processManager;
    private readonly ProcessTerminator _terminator;
    private readonly IHealthChecker _healthChecker;
    private readonly HealthMonitor _healthMonitor;
    private readonly RestartManager _restartManager;
    private readonly IWatchdogStatusStore _statusStore;
    private readonly IConfigStore _configStore;
    private readonly IClock _clock;
    private readonly ILogger<WatchdogEngine> _logger;
    private readonly object _configGate = new();
    private readonly SemaphoreSlim _controlLock = new(1, 1);

    private WatchdogConfig _config;
    private int? _trackedPid;
    private DateTimeOffset? _nextAllowedStartAt;
    private DateTimeOffset _lastHealthProbeAt = DateTimeOffset.MinValue;
    private bool _manualStopRequested;

    public WatchdogEngine(
        IProcessManager processManager,
        ProcessTerminator terminator,
        IHealthChecker healthChecker,
        HealthMonitor healthMonitor,
        RestartManager restartManager,
        IWatchdogStatusStore statusStore,
        IConfigStore configStore,
        IClock clock,
        ILogger<WatchdogEngine> logger,
        WatchdogConfig? initialConfig = null)
    {
        _processManager = processManager;
        _terminator = terminator;
        _healthChecker = healthChecker;
        _healthMonitor = healthMonitor;
        _restartManager = restartManager;
        _statusStore = statusStore;
        _configStore = configStore;
        _clock = clock;
        _logger = logger;
        _config = initialConfig ?? configStore.Load();
    }

    public WatchdogConfig CurrentConfig
    {
        get
        {
            lock (_configGate)
            {
                return CloneConfig(_config);
            }
        }
    }

    public void ReloadConfiguration(WatchdogConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var validation = ConfigValidator.Validate(config);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "Invalid configuration: " + string.Join("; ", validation.Errors));
        }

        lock (_configGate)
        {
            _config = CloneConfig(config);
        }

        _logger.LogInformation("Configuration changed.");
        UpdateStatus(s => s.ApplicationName = config.Application.DisplayName);
    }

    public async Task StartApplicationAsync(CancellationToken cancellationToken = default)
    {
        await _controlLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _manualStopRequested = false;
            await EnsureApplicationRunningAsync(forceStart: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlLock.Release();
        }
    }

    public async Task StopApplicationAsync(CancellationToken cancellationToken = default)
    {
        await _controlLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _manualStopRequested = true;
            var config = CurrentConfig;
            await StopTrackedProcessAsync(config, cancellationToken).ConfigureAwait(false);
            UpdateStatus(s =>
            {
                s.Status = ApplicationStatus.Stopped;
                s.ProcessId = null;
                s.ProcessStartTime = null;
                s.LastError = null;
            });
        }
        finally
        {
            _controlLock.Release();
        }
    }

    public async Task RestartApplicationAsync(CancellationToken cancellationToken = default)
    {
        await _controlLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _manualStopRequested = false;
            var config = CurrentConfig;
            UpdateStatus(s => s.Status = ApplicationStatus.Restarting);
            await StopTrackedProcessAsync(config, cancellationToken).ConfigureAwait(false);
            await DelayRestartAsync(config, cancellationToken).ConfigureAwait(false);
            await EnsureApplicationRunningAsync(forceStart: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlLock.Release();
        }
    }

    public void ResetRestartCounter()
    {
        _restartManager.Reset();
        UpdateStatus(s =>
        {
            s.RestartCount = 0;
            s.RestartLimitReached = false;
            if (s.Status == ApplicationStatus.RestartLimitReached)
                s.Status = s.ProcessId is null ? ApplicationStatus.Stopped : ApplicationStatus.Running;
        });
    }

    public async Task<HealthCheckResult> TestHealthCheckAsync(CancellationToken cancellationToken = default)
    {
        var config = CurrentConfig;
        if (!config.Health.Enabled)
        {
            return new HealthCheckResult
            {
                Status = HealthStatus.Unknown,
                CheckedAt = _clock.UtcNow,
                Message = "Health checks are disabled."
            };
        }

        return await _healthChecker.CheckAsync(config.Health.Url, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Watchdog started.");
        UpdateStatus(s =>
        {
            s.ApplicationName = CurrentConfig.Application.DisplayName;
            s.Status = ApplicationStatus.Unknown;
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _controlLock.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    await MonitorOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                finally
                {
                    _controlLock.Release();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in monitoring loop.");
                UpdateStatus(s =>
                {
                    s.Status = ApplicationStatus.Error;
                    s.LastError = ex.Message;
                });
            }

            var delay = TimeSpan.FromSeconds(Math.Max(1, CurrentConfig.Monitoring.ProcessCheckIntervalSeconds));
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Watchdog stopped.");
    }

    private async Task MonitorOnceAsync(CancellationToken cancellationToken)
    {
        var config = CurrentConfig;

        if (string.IsNullOrWhiteSpace(config.Application.ExecutablePath))
        {
            UpdateStatus(s =>
            {
                s.Status = ApplicationStatus.NotConfigured;
                s.ProcessId = null;
                s.ProcessStartTime = null;
                s.LastError = "Configure an executable path and save before starting.";
            });
            return;
        }

        if (!File.Exists(config.Application.ExecutablePath))
        {
            UpdateStatus(s =>
            {
                s.Status = ApplicationStatus.NotConfigured;
                s.ProcessId = null;
                s.ProcessStartTime = null;
                s.LastError = $"Executable not found: {config.Application.ExecutablePath}";
            });
            // Do not auto-restart — bad/sample paths must not burn the restart budget.
            return;
        }

        if (_manualStopRequested)
        {
            UpdateStatus(s =>
            {
                s.Status = ApplicationStatus.Stopped;
                s.ProcessId = null;
                s.ProcessStartTime = null;
            });
            return;
        }

        var instances = _processManager.FindByExecutablePath(config.Application.ExecutablePath);

        if (instances.Count > 1)
        {
            _logger.LogWarning(
                "Multiple root instances ({Count}) of {Path} detected. Tracking PID {Pid}; not launching another copy.",
                instances.Count,
                config.Application.ExecutablePath,
                instances[0].Id);

            _trackedPid = instances[0].Id;
            UpdateRunningStatus(instances[0], ApplicationStatus.Running);
            // Still allow health monitoring against the first instance.
        }
        else if (instances.Count == 1)
        {
            var process = instances[0];
            if (_trackedPid is int previous && previous != process.Id)
            {
                _logger.LogInformation(
                    "Tracking newly observed process PID {Pid} (previously {Previous}).",
                    process.Id,
                    previous);
            }

            _trackedPid = process.Id;
            UpdateRunningStatus(process, ApplicationStatus.Running);
        }
        else
        {
            // Process missing
            if (_trackedPid is int exitedPid)
            {
                _logger.LogWarning("Application exited (last PID {Pid}).", exitedPid);
                _trackedPid = null;
            }

            UpdateStatus(s =>
            {
                s.Status = ApplicationStatus.Stopped;
                s.ProcessId = null;
                s.ProcessStartTime = null;
            });

            if (config.Restart.RestartOnExit)
            {
                await TryRestartAsync(config, "process missing/exited", cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (config.Health.Enabled && _trackedPid is not null)
        {
            await MaybeRunHealthCheckAsync(config, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MaybeRunHealthCheckAsync(WatchdogConfig config, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(config.Monitoring.HealthCheckIntervalSeconds);
        if (_clock.UtcNow - _lastHealthProbeAt < interval)
            return;

        _lastHealthProbeAt = _clock.UtcNow;
        var result = await _healthChecker.CheckAsync(config.Health.Url, cancellationToken).ConfigureAwait(false);
        var evaluation = _healthMonitor.Evaluate(
            result,
            TimeSpan.FromSeconds(config.Monitoring.HealthTimeoutSeconds));

        UpdateStatus(s =>
        {
            s.LastHealthCheckAt = result.CheckedAt;
            s.LastHealthCheckSucceeded = result.IsSuccess;
            if (!result.IsSuccess)
                s.Status = ApplicationStatus.Unhealthy;
            else if (s.Status == ApplicationStatus.Unhealthy)
                s.Status = ApplicationStatus.Running;
        });

        if (evaluation.ShouldRestart && config.Restart.RestartOnUnhealthy && _trackedPid is int pid)
        {
            _logger.LogError("Application marked unhealthy; terminating PID {Pid} for restart.", pid);
            UpdateStatus(s => s.Status = ApplicationStatus.Restarting);

            try
            {
                await _terminator.TerminateAsync(pid, config.Monitoring, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Application terminated after unhealthy state.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to terminate unhealthy application PID {Pid}.", pid);
            }

            _trackedPid = null;
            _healthMonitor.Reset();
            await TryRestartAsync(config, "unhealthy", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryRestartAsync(WatchdogConfig config, string reason, CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromMinutes(config.Restart.RestartWindowMinutes);

        if (!_restartManager.CanRestart(config.Restart.MaxRestarts, window))
        {
            UpdateStatus(s =>
            {
                s.Status = ApplicationStatus.RestartLimitReached;
                s.RestartLimitReached = true;
                s.RestartCount = _restartManager.GetCountInWindow(window);
                s.LastRestartAt = _restartManager.LastRestartAt;
                s.LastError = "Restart limit reached.";
            });
            return;
        }

        if (_nextAllowedStartAt is DateTimeOffset notBefore && _clock.UtcNow < notBefore)
        {
            UpdateStatus(s =>
            {
                s.Status = ApplicationStatus.Restarting;
                s.RestartCount = _restartManager.GetCountInWindow(window);
            });
            return;
        }

        UpdateStatus(s => s.Status = ApplicationStatus.Starting);
        await DelayRestartAsync(config, cancellationToken).ConfigureAwait(false);

        try
        {
            var started = StartProcess(config);
            _restartManager.RecordRestart(config.Restart.MaxRestarts, window);
            _trackedPid = started.Id;
            _healthMonitor.Reset();
            _logger.LogInformation("Application restarted after {Reason} (PID {Pid}).", reason, started.Id);

            UpdateStatus(s =>
            {
                s.Status = ApplicationStatus.Running;
                s.ProcessId = started.Id;
                s.ProcessStartTime = started.StartTime ?? _clock.UtcNow;
                s.LastRestartAt = _restartManager.LastRestartAt;
                s.RestartCount = _restartManager.GetCountInWindow(window);
                s.RestartLimitReached = _restartManager.LimitReached;
                s.LastError = null;
            });

            if (_restartManager.LimitReached)
            {
                UpdateStatus(s => s.Status = ApplicationStatus.RestartLimitReached);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Application failed to start.");
            _restartManager.RecordRestart(config.Restart.MaxRestarts, window);
            _nextAllowedStartAt = _clock.UtcNow + TimeSpan.FromSeconds(config.Restart.RestartDelaySeconds);
            UpdateStatus(s =>
            {
                s.Status = ApplicationStatus.Error;
                s.LastError = ex.Message;
                s.RestartCount = _restartManager.GetCountInWindow(window);
                s.LastRestartAt = _restartManager.LastRestartAt;
                s.RestartLimitReached = _restartManager.LimitReached;
            });
        }
    }

    private async Task EnsureApplicationRunningAsync(bool forceStart, CancellationToken cancellationToken)
    {
        var config = CurrentConfig;
        var instances = _processManager.FindByExecutablePath(config.Application.ExecutablePath);

        if (instances.Count >= 1)
        {
            _trackedPid = instances[0].Id;
            UpdateRunningStatus(instances[0], ApplicationStatus.Running);
            if (instances.Count > 1)
            {
                _logger.LogWarning(
                    "Multiple instances ({Count}) already running; not starting another.",
                    instances.Count);
            }

            return;
        }

        if (!forceStart && !config.Restart.RestartOnExit)
            return;

        UpdateStatus(s => s.Status = ApplicationStatus.Starting);
        var started = StartProcess(config);
        _trackedPid = started.Id;
        _healthMonitor.Reset();
        _logger.LogInformation("Application started (PID {Pid}).", started.Id);
        UpdateRunningStatus(started, ApplicationStatus.Running);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private ProcessInfo StartProcess(WatchdogConfig config)
    {
        var validation = ConfigValidator.Validate(config, requireExistingExecutable: true);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(string.Join("; ", validation.Errors));
        }

        return _processManager.Start(
            config.Application.ExecutablePath,
            config.Application.Arguments,
            config.Application.WorkingDirectory);
    }

    private async Task StopTrackedProcessAsync(WatchdogConfig config, CancellationToken cancellationToken)
    {
        var instances = _processManager.FindByExecutablePath(config.Application.ExecutablePath);
        foreach (var instance in instances)
        {
            try
            {
                await _terminator.TerminateAsync(instance.Id, config.Monitoring, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed stopping PID {Pid}.", instance.Id);
            }
        }

        _trackedPid = null;
    }

    private async Task DelayRestartAsync(WatchdogConfig config, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(0, config.Restart.RestartDelaySeconds));
        if (delay > TimeSpan.Zero)
        {
            _logger.LogInformation("Waiting {Delay} before start/restart.", delay);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void UpdateRunningStatus(ProcessInfo process, ApplicationStatus status)
    {
        var window = TimeSpan.FromMinutes(CurrentConfig.Restart.RestartWindowMinutes);
        UpdateStatus(s =>
        {
            s.Status = _restartManager.LimitReached ? ApplicationStatus.RestartLimitReached : status;
            s.ProcessId = process.Id;
            s.ProcessStartTime = process.StartTime;
            s.RestartCount = _restartManager.GetCountInWindow(window);
            s.LastRestartAt = _restartManager.LastRestartAt;
            s.RestartLimitReached = _restartManager.LimitReached;
            s.LastError = null;
        });
    }

    private void UpdateStatus(Action<WatchdogStatus> mutate) => _statusStore.Update(mutate);

    private static WatchdogConfig CloneConfig(WatchdogConfig source)
    {
        // Simple clone via JSON round-trip is fine for small config; keep explicit for clarity/tests.
        return new WatchdogConfig
        {
            Application = new ApplicationConfig
            {
                ExecutablePath = source.Application.ExecutablePath,
                Arguments = source.Application.Arguments,
                WorkingDirectory = source.Application.WorkingDirectory,
                DisplayName = source.Application.DisplayName
            },
            Monitoring = new MonitoringConfig
            {
                ProcessCheckIntervalSeconds = source.Monitoring.ProcessCheckIntervalSeconds,
                HealthCheckIntervalSeconds = source.Monitoring.HealthCheckIntervalSeconds,
                HealthTimeoutSeconds = source.Monitoring.HealthTimeoutSeconds,
                GracefulTerminationTimeoutSeconds = source.Monitoring.GracefulTerminationTimeoutSeconds
            },
            Restart = new RestartConfig
            {
                RestartOnExit = source.Restart.RestartOnExit,
                RestartOnUnhealthy = source.Restart.RestartOnUnhealthy,
                RestartDelaySeconds = source.Restart.RestartDelaySeconds,
                MaxRestarts = source.Restart.MaxRestarts,
                RestartWindowMinutes = source.Restart.RestartWindowMinutes
            },
            Health = new HealthConfig
            {
                Enabled = source.Health.Enabled,
                Type = source.Health.Type,
                Url = source.Health.Url
            },
            Launch = new LaunchConfig
            {
                Mode = source.Launch.Mode
            }
        };
    }
}
