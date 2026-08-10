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
    private readonly IWatchdogStatusStore _statusStore;
    private readonly IConfigStore _configStore;
    private readonly IClock _clock;
    private readonly ILogger<WatchdogEngine> _logger;
    private readonly object _configGate = new();
    private readonly SemaphoreSlim _controlLock = new(1, 1);
    private readonly Dictionary<string, AppSlot> _slots = new(StringComparer.OrdinalIgnoreCase);

    private WatchdogConfig _config;

    public WatchdogEngine(
        IProcessManager processManager,
        ProcessTerminator terminator,
        IHealthChecker healthChecker,
        IWatchdogStatusStore statusStore,
        IConfigStore configStore,
        IClock clock,
        ILogger<WatchdogEngine> logger,
        WatchdogConfig? initialConfig = null)
    {
        _processManager = processManager;
        _terminator = terminator;
        _healthChecker = healthChecker;
        _statusStore = statusStore;
        _configStore = configStore;
        _clock = clock;
        _logger = logger;
        _config = initialConfig ?? configStore.Load();
        _config.Normalize();
        ReconcileSlots(_config);
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
        config.Normalize();
        var validation = ConfigValidator.Validate(config);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "Invalid configuration: " + string.Join("; ", validation.Errors));
        }

        lock (_configGate)
        {
            _config = CloneConfig(config);
            ReconcileSlots(_config);
        }

        _logger.LogInformation(
            "Configuration changed ({Count} application(s)).",
            _config.Applications.Count);
    }

    public Task StartApplicationAsync(string? applicationId = null, CancellationToken cancellationToken = default)
        => RunForAppAsync(applicationId, async (slot, app, ct) =>
        {
            slot.ManualStopRequested = false;
            await EnsureApplicationRunningAsync(slot, app, forceStart: true, ct).ConfigureAwait(false);
        }, cancellationToken);

    public Task StopApplicationAsync(string? applicationId = null, CancellationToken cancellationToken = default)
        => RunForAppAsync(applicationId, async (slot, app, ct) =>
        {
            slot.ManualStopRequested = true;
            await StopTrackedProcessAsync(slot, app, ct).ConfigureAwait(false);
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = app.Application.DisplayName;
                s.Status = ApplicationStatus.Stopped;
                s.ProcessId = null;
                s.ProcessStartTime = null;
                s.LastError = null;
            });
        }, cancellationToken);

    public Task RestartApplicationAsync(string? applicationId = null, CancellationToken cancellationToken = default)
        => RunForAppAsync(applicationId, async (slot, app, ct) =>
        {
            slot.ManualStopRequested = false;
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = app.Application.DisplayName;
                s.Status = ApplicationStatus.Restarting;
            });
            await StopTrackedProcessAsync(slot, app, ct).ConfigureAwait(false);
            await DelayRestartAsync(app, ct).ConfigureAwait(false);
            await EnsureApplicationRunningAsync(slot, app, forceStart: true, ct).ConfigureAwait(false);
        }, cancellationToken);

    public void ResetRestartCounter(string? applicationId = null)
    {
        var app = ResolveApp(applicationId);
        if (app is null)
            return;

        if (!_slots.TryGetValue(app.Id, out var slot))
            return;

        slot.RestartManager.Reset();
        UpdateStatus(app.Id, s =>
        {
            s.RestartCount = 0;
            s.RestartLimitReached = false;
            if (s.Status == ApplicationStatus.RestartLimitReached)
                s.Status = s.ProcessId is null ? ApplicationStatus.Stopped : ApplicationStatus.Running;
        });
    }

    public async Task<HealthCheckResult> TestHealthCheckAsync(
        string? applicationId = null,
        CancellationToken cancellationToken = default)
    {
        var app = ResolveApp(applicationId);
        if (app is null)
        {
            return new HealthCheckResult
            {
                Status = HealthStatus.Unknown,
                CheckedAt = _clock.UtcNow,
                Message = "No application configured."
            };
        }

        if (!app.Health.Enabled && !app.IsTcp && !app.IsWindowsService)
        {
            return new HealthCheckResult
            {
                Status = HealthStatus.Unknown,
                CheckedAt = _clock.UtcNow,
                Message = "Health checks are disabled."
            };
        }

        if (app.IsTcp)
        {
            return await _healthChecker.CheckTcpAsync(
                app.Tcp.Host,
                app.Tcp.Port,
                cancellationToken).ConfigureAwait(false);
        }

        if (app.IsWindowsService)
        {
            var running = WindowsServiceControl.IsRunning(app.WindowsService.ServiceName, out var error);
            return new HealthCheckResult
            {
                Status = running ? HealthStatus.Healthy : HealthStatus.Unhealthy,
                CheckedAt = _clock.UtcNow,
                Message = running ? "Service running" : (error ?? "Service not running")
            };
        }

        return await _healthChecker.CheckHttpAsync(
            app.Health.Url,
            app.Health.ExpectedStatusCode,
            cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Watchdog started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _controlLock.WaitAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    var config = CurrentConfig;
                    foreach (var app in config.EnabledApplications())
                    {
                        if (!_slots.TryGetValue(app.Id, out var slot))
                            continue;

                        await MonitorOnceAsync(slot, app, stoppingToken).ConfigureAwait(false);
                    }
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
            }

            var delaySeconds = CurrentConfig.EnabledApplications()
                .Select(a => a.Monitoring.ProcessCheckIntervalSeconds)
                .DefaultIfEmpty(5)
                .Min();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, delaySeconds)), stoppingToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Watchdog stopped.");
    }

    private async Task RunForAppAsync(
        string? applicationId,
        Func<AppSlot, MonitoredApplicationConfig, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await _controlLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var app = ResolveApp(applicationId)
                      ?? throw new InvalidOperationException("Application not found in configuration.");

            if (!_slots.TryGetValue(app.Id, out var slot))
                throw new InvalidOperationException($"No monitoring slot for '{app.Id}'.");

            await action(slot, app, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlLock.Release();
        }
    }

    private MonitoredApplicationConfig? ResolveApp(string? applicationId)
    {
        lock (_configGate)
        {
            return _config.FindApplication(applicationId);
        }
    }

    private void ReconcileSlots(WatchdogConfig config)
    {
        var activeIds = config.Applications.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var app in config.Applications)
        {
            if (_slots.TryGetValue(app.Id, out var existing))
            {
                // Keep PID / counters when id survives reload.
                UpdateStatus(app.Id, s => s.ApplicationName = app.Application.DisplayName);
                continue;
            }

            _slots[app.Id] = new AppSlot(app.Id, _clock);
            UpdateStatus(app.Id, s =>
            {
                s.ApplicationName = app.Application.DisplayName;
                s.Status = ApplicationStatus.Unknown;
            });
        }

        foreach (var id in _slots.Keys.Where(id => !activeIds.Contains(id)).ToList())
            _slots.Remove(id);

        _statusStore.RemoveMissing(activeIds);
    }

    private async Task MonitorOnceAsync(AppSlot slot, MonitoredApplicationConfig app, CancellationToken cancellationToken)
    {
        switch (app.Kind)
        {
            case ApplicationKind.Http:
                await MonitorProbeAppOnceAsync(slot, app, ProbeKind.Http, cancellationToken).ConfigureAwait(false);
                return;
            case ApplicationKind.Tcp:
                await MonitorProbeAppOnceAsync(slot, app, ProbeKind.Tcp, cancellationToken).ConfigureAwait(false);
                return;
            case ApplicationKind.WindowsService:
                await MonitorWindowsServiceOnceAsync(slot, app, cancellationToken).ConfigureAwait(false);
                return;
            default:
                await MonitorProcessAppOnceAsync(slot, app, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private enum ProbeKind { Http, Tcp }

    private async Task<HealthCheckResult> ProbeAsync(
        MonitoredApplicationConfig app,
        ProbeKind kind,
        CancellationToken cancellationToken)
    {
        return kind == ProbeKind.Tcp
            ? await _healthChecker.CheckTcpAsync(app.Tcp.Host, app.Tcp.Port, cancellationToken).ConfigureAwait(false)
            : await _healthChecker.CheckHttpAsync(
                app.Health.Url,
                app.Health.ExpectedStatusCode,
                cancellationToken).ConfigureAwait(false);
    }

    private static string? StartCommandFor(MonitoredApplicationConfig app)
        => app.Kind switch
        {
            ApplicationKind.Http => app.Http.StartCommand,
            ApplicationKind.Tcp => app.Tcp.StartCommand,
            _ => null
        };

    private static string? StopCommandFor(MonitoredApplicationConfig app)
        => app.Kind switch
        {
            ApplicationKind.Http => app.Http.StopCommand,
            ApplicationKind.Tcp => app.Tcp.StopCommand,
            _ => null
        };

    private static string WorkDirFor(MonitoredApplicationConfig app)
        => app.Kind switch
        {
            ApplicationKind.Http => FirstNonEmpty(app.Http.WorkingDirectory, app.Application.WorkingDirectory)
                                    ?? Environment.CurrentDirectory,
            ApplicationKind.Tcp => FirstNonEmpty(app.Tcp.WorkingDirectory, app.Application.WorkingDirectory)
                                   ?? Environment.CurrentDirectory,
            _ => FirstNonEmpty(app.Application.WorkingDirectory) ?? Environment.CurrentDirectory
        };

    private async Task MonitorProbeAppOnceAsync(
        AppSlot slot,
        MonitoredApplicationConfig app,
        ProbeKind kind,
        CancellationToken cancellationToken)
    {
        var display = app.Application.DisplayName;
        var startCommand = StartCommandFor(app);

        if (kind == ProbeKind.Http && string.IsNullOrWhiteSpace(app.Health.Url))
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = display;
                s.Status = ApplicationStatus.NotConfigured;
                s.ProcessId = null;
                s.ProcessStartTime = null;
                s.LastError = "Configure a health URL, then save.";
            });
            return;
        }

        if (kind == ProbeKind.Tcp && (app.Tcp.Port < 1 || string.IsNullOrWhiteSpace(app.Tcp.Host)))
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = display;
                s.Status = ApplicationStatus.NotConfigured;
                s.ProcessId = null;
                s.ProcessStartTime = null;
                s.LastError = "Configure TCP host and port, then save.";
            });
            return;
        }

        if (slot.ManualStopRequested)
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = display;
                s.Status = ApplicationStatus.Stopped;
                s.ProcessId = null;
                s.ProcessStartTime = null;
            });
            return;
        }

        if (slot.TrackedPid is int tracked && !_processManager.IsRunning(tracked))
            slot.TrackedPid = null;

        var interval = TimeSpan.FromSeconds(app.Monitoring.HealthCheckIntervalSeconds);
        if (_clock.UtcNow - slot.LastHealthProbeAt < interval && slot.LastHealthProbeAt != DateTimeOffset.MinValue)
            return;

        slot.LastHealthProbeAt = _clock.UtcNow;
        var result = await ProbeAsync(app, kind, cancellationToken).ConfigureAwait(false);
        var evaluation = slot.HealthMonitor.Evaluate(
            result,
            TimeSpan.FromSeconds(app.Monitoring.HealthTimeoutSeconds));

        UpdateStatus(slot.Id, s =>
        {
            s.ApplicationName = display;
            s.LastHealthCheckAt = result.CheckedAt;
            s.LastHealthCheckSucceeded = result.IsSuccess;
            s.ProcessId = slot.TrackedPid;
            if (slot.TrackedPid is int pid)
                s.ProcessStartTime = _processManager.GetById(pid)?.StartTime;
            else
                s.ProcessStartTime = null;
        });

        if (result.IsSuccess)
        {
            UpdateStatus(slot.Id, s =>
            {
                s.Status = ApplicationStatus.Running;
                s.LastError = null;
            });
            return;
        }

        UpdateStatus(slot.Id, s => s.Status = ApplicationStatus.Unhealthy);

        var canStart = !string.IsNullOrWhiteSpace(startCommand);
        if (!canStart)
            return;

        if (slot.TrackedPid is null
            && (app.Restart.RestartOnExit || app.Restart.RestartOnUnhealthy))
        {
            await TryRestartAsync(slot, app, kind == ProbeKind.Tcp ? "tcp not responding" : "http not responding", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!evaluation.ShouldRestart || !app.Restart.RestartOnUnhealthy)
            return;

        _logger.LogWarning(
            "[{AppId}] Probe unhealthy ({Message}); restarting via start command.",
            slot.Id,
            result.Message);

        UpdateStatus(slot.Id, s => s.Status = ApplicationStatus.Restarting);
        await StopTrackedProcessAsync(slot, app, cancellationToken).ConfigureAwait(false);
        slot.HealthMonitor.Reset();
        await TryRestartAsync(slot, app, "unhealthy probe", cancellationToken).ConfigureAwait(false);
    }

    private async Task MonitorWindowsServiceOnceAsync(
        AppSlot slot,
        MonitoredApplicationConfig app,
        CancellationToken cancellationToken)
    {
        var display = app.Application.DisplayName;
        var serviceName = app.WindowsService.ServiceName;

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = display;
                s.Status = ApplicationStatus.NotConfigured;
                s.LastError = "Configure a Windows service name and save.";
            });
            return;
        }

        if (slot.ManualStopRequested)
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = display;
                s.Status = ApplicationStatus.Stopped;
            });
            return;
        }

        var running = WindowsServiceControl.IsRunning(serviceName, out var error);
        if (running)
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = display;
                s.Status = ApplicationStatus.Running;
                s.LastError = null;
                s.LastHealthCheckAt = _clock.UtcNow;
                s.LastHealthCheckSucceeded = true;
            });
            return;
        }

        UpdateStatus(slot.Id, s =>
        {
            s.ApplicationName = display;
            s.Status = ApplicationStatus.Stopped;
            s.LastError = error;
            s.LastHealthCheckAt = _clock.UtcNow;
            s.LastHealthCheckSucceeded = false;
        });

        if (app.Restart.RestartOnExit || app.Restart.RestartOnUnhealthy)
            await TryRestartAsync(slot, app, "windows service not running", cancellationToken).ConfigureAwait(false);

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task MonitorProcessAppOnceAsync(
        AppSlot slot,
        MonitoredApplicationConfig app,
        CancellationToken cancellationToken)
    {
        var display = app.Application.DisplayName;

        if (string.IsNullOrWhiteSpace(app.Application.ExecutablePath))
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = display;
                s.Status = ApplicationStatus.NotConfigured;
                s.ProcessId = null;
                s.ProcessStartTime = null;
                s.LastError = "Configure an executable path and save before starting.";
            });
            return;
        }

        if (!File.Exists(app.Application.ExecutablePath))
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = display;
                s.Status = ApplicationStatus.NotConfigured;
                s.ProcessId = null;
                s.ProcessStartTime = null;
                s.LastError = $"Executable not found: {app.Application.ExecutablePath}";
            });
            return;
        }

        if (slot.ManualStopRequested)
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = display;
                s.Status = ApplicationStatus.Stopped;
                s.ProcessId = null;
                s.ProcessStartTime = null;
            });
            return;
        }

        var instances = _processManager.FindByExecutablePath(app.Application.ExecutablePath);

        if (instances.Count > 1)
        {
            _logger.LogWarning(
                "[{AppId}] Multiple root instances ({Count}) of {Path} detected. Tracking PID {Pid}.",
                slot.Id,
                instances.Count,
                app.Application.ExecutablePath,
                instances[0].Id);

            slot.TrackedPid = instances[0].Id;
            UpdateRunningStatus(slot, app, instances[0], ApplicationStatus.Running);
        }
        else if (instances.Count == 1)
        {
            var process = instances[0];
            if (slot.TrackedPid is int previous && previous != process.Id)
            {
                _logger.LogInformation(
                    "[{AppId}] Tracking newly observed process PID {Pid} (previously {Previous}).",
                    slot.Id,
                    process.Id,
                    previous);
            }

            slot.TrackedPid = process.Id;
            UpdateRunningStatus(slot, app, process, ApplicationStatus.Running);
        }
        else
        {
            if (slot.TrackedPid is int exitedPid)
            {
                _logger.LogWarning("[{AppId}] Application exited (last PID {Pid}).", slot.Id, exitedPid);
                slot.TrackedPid = null;
            }

            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = display;
                s.Status = ApplicationStatus.Stopped;
                s.ProcessId = null;
                s.ProcessStartTime = null;
            });

            if (app.Restart.RestartOnExit)
                await TryRestartAsync(slot, app, "process missing/exited", cancellationToken).ConfigureAwait(false);

            return;
        }

        if (app.Health.Enabled && slot.TrackedPid is not null)
            await MaybeRunHealthCheckAsync(slot, app, cancellationToken).ConfigureAwait(false);
    }

    private async Task MaybeRunHealthCheckAsync(
        AppSlot slot,
        MonitoredApplicationConfig app,
        CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(app.Monitoring.HealthCheckIntervalSeconds);
        if (_clock.UtcNow - slot.LastHealthProbeAt < interval)
            return;

        slot.LastHealthProbeAt = _clock.UtcNow;
        var result = await _healthChecker.CheckHttpAsync(
            app.Health.Url,
            app.Health.ExpectedStatusCode,
            cancellationToken).ConfigureAwait(false);
        var evaluation = slot.HealthMonitor.Evaluate(
            result,
            TimeSpan.FromSeconds(app.Monitoring.HealthTimeoutSeconds));

        UpdateStatus(slot.Id, s =>
        {
            s.ApplicationName = app.Application.DisplayName;
            s.LastHealthCheckAt = result.CheckedAt;
            s.LastHealthCheckSucceeded = result.IsSuccess;
            if (!result.IsSuccess)
                s.Status = ApplicationStatus.Unhealthy;
            else if (s.Status == ApplicationStatus.Unhealthy)
                s.Status = ApplicationStatus.Running;
        });

        if (evaluation.ShouldRestart && app.Restart.RestartOnUnhealthy && slot.TrackedPid is int pid)
        {
            _logger.LogError("[{AppId}] Application marked unhealthy; terminating PID {Pid}.", slot.Id, pid);
            UpdateStatus(slot.Id, s => s.Status = ApplicationStatus.Restarting);

            try
            {
                await _terminator.TerminateAsync(pid, app.Monitoring, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("[{AppId}] Application terminated after unhealthy state.", slot.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{AppId}] Failed to terminate unhealthy application PID {Pid}.", slot.Id, pid);
            }

            slot.TrackedPid = null;
            slot.HealthMonitor.Reset();
            await TryRestartAsync(slot, app, "unhealthy", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryRestartAsync(
        AppSlot slot,
        MonitoredApplicationConfig app,
        string reason,
        CancellationToken cancellationToken)
    {
        var window = TimeSpan.FromMinutes(app.Restart.RestartWindowMinutes);

        if (!slot.RestartManager.CanRestart(app.Restart.MaxRestarts, window))
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = app.Application.DisplayName;
                s.Status = ApplicationStatus.RestartLimitReached;
                s.RestartLimitReached = true;
                s.RestartCount = slot.RestartManager.GetCountInWindow(window);
                s.LastRestartAt = slot.RestartManager.LastRestartAt;
                s.LastError = "Restart limit reached.";
            });
            return;
        }

        if (slot.NextAllowedStartAt is DateTimeOffset notBefore && _clock.UtcNow < notBefore)
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = app.Application.DisplayName;
                s.Status = ApplicationStatus.Restarting;
                s.RestartCount = slot.RestartManager.GetCountInWindow(window);
            });
            return;
        }

        UpdateStatus(slot.Id, s =>
        {
            s.ApplicationName = app.Application.DisplayName;
            s.Status = ApplicationStatus.Starting;
        });
        await DelayRestartAsync(app, cancellationToken).ConfigureAwait(false);

        try
        {
            if (app.IsWindowsService)
            {
                StartOrRecover(app);
                slot.RestartManager.RecordRestart(app.Restart.MaxRestarts, window);
                slot.TrackedPid = null;
                slot.HealthMonitor.Reset();
                _logger.LogInformation(
                    "[{AppId}] Windows service restarted after {Reason}: {Service}",
                    slot.Id,
                    reason,
                    app.WindowsService.ServiceName);

                UpdateStatus(slot.Id, s =>
                {
                    s.ApplicationName = app.Application.DisplayName;
                    s.Status = ApplicationStatus.Running;
                    s.ProcessId = null;
                    s.ProcessStartTime = _clock.UtcNow;
                    s.LastRestartAt = slot.RestartManager.LastRestartAt;
                    s.RestartCount = slot.RestartManager.GetCountInWindow(window);
                    s.RestartLimitReached = slot.RestartManager.LimitReached;
                    s.LastError = null;
                });

                if (slot.RestartManager.LimitReached)
                    UpdateStatus(slot.Id, s => s.Status = ApplicationStatus.RestartLimitReached);
                return;
            }

            var started = StartProcess(app);
            slot.RestartManager.RecordRestart(app.Restart.MaxRestarts, window);
            slot.TrackedPid = started.Id;
            slot.HealthMonitor.Reset();
            _logger.LogInformation(
                "[{AppId}] Application restarted after {Reason} (PID {Pid}).",
                slot.Id,
                reason,
                started.Id);

            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = app.Application.DisplayName;
                s.Status = ApplicationStatus.Running;
                s.ProcessId = started.Id;
                s.ProcessStartTime = started.StartTime ?? _clock.UtcNow;
                s.LastRestartAt = slot.RestartManager.LastRestartAt;
                s.RestartCount = slot.RestartManager.GetCountInWindow(window);
                s.RestartLimitReached = slot.RestartManager.LimitReached;
                s.LastError = null;
            });

            if (slot.RestartManager.LimitReached)
                UpdateStatus(slot.Id, s => s.Status = ApplicationStatus.RestartLimitReached);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{AppId}] Application failed to start.", slot.Id);
            slot.RestartManager.RecordRestart(app.Restart.MaxRestarts, window);
            slot.NextAllowedStartAt = _clock.UtcNow + TimeSpan.FromSeconds(app.Restart.RestartDelaySeconds);
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = app.Application.DisplayName;
                s.Status = ApplicationStatus.Error;
                s.LastError = ex.Message;
                s.RestartCount = slot.RestartManager.GetCountInWindow(window);
                s.LastRestartAt = slot.RestartManager.LastRestartAt;
                s.RestartLimitReached = slot.RestartManager.LimitReached;
            });
        }
    }

    private async Task EnsureApplicationRunningAsync(
        AppSlot slot,
        MonitoredApplicationConfig app,
        bool forceStart,
        CancellationToken cancellationToken)
    {
        if (app.IsHttp || app.IsTcp)
        {
            await EnsureProbeAppRunningAsync(slot, app, forceStart, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (app.IsWindowsService)
        {
            await EnsureWindowsServiceRunningAsync(slot, app, forceStart, cancellationToken).ConfigureAwait(false);
            return;
        }

        var instances = _processManager.FindByExecutablePath(app.Application.ExecutablePath);

        if (instances.Count >= 1)
        {
            slot.TrackedPid = instances[0].Id;
            UpdateRunningStatus(slot, app, instances[0], ApplicationStatus.Running);
            if (instances.Count > 1)
            {
                _logger.LogWarning(
                    "[{AppId}] Multiple instances ({Count}) already running; not starting another.",
                    slot.Id,
                    instances.Count);
            }

            return;
        }

        if (!forceStart && !app.Restart.RestartOnExit)
            return;

        UpdateStatus(slot.Id, s =>
        {
            s.ApplicationName = app.Application.DisplayName;
            s.Status = ApplicationStatus.Starting;
        });
        var started = StartProcess(app);
        slot.TrackedPid = started.Id;
        slot.HealthMonitor.Reset();
        _logger.LogInformation("[{AppId}] Application started (PID {Pid}).", slot.Id, started.Id);
        UpdateRunningStatus(slot, app, started, ApplicationStatus.Running);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task EnsureProbeAppRunningAsync(
        AppSlot slot,
        MonitoredApplicationConfig app,
        bool forceStart,
        CancellationToken cancellationToken)
    {
        var kind = app.IsTcp ? ProbeKind.Tcp : ProbeKind.Http;
        try
        {
            var probe = await ProbeAsync(app, kind, cancellationToken).ConfigureAwait(false);
            if (probe.IsSuccess)
            {
                UpdateStatus(slot.Id, s =>
                {
                    s.ApplicationName = app.Application.DisplayName;
                    s.Status = ApplicationStatus.Running;
                    s.LastHealthCheckAt = probe.CheckedAt;
                    s.LastHealthCheckSucceeded = true;
                    s.LastError = null;
                });
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[{AppId}] Pre-start probe failed (will start if command set).", slot.Id);
        }

        var startCommand = StartCommandFor(app);
        if (string.IsNullOrWhiteSpace(startCommand))
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = app.Application.DisplayName;
                s.Status = ApplicationStatus.Unhealthy;
                s.LastError = "Probe failed and no start command is configured (probe-only).";
            });
            return;
        }

        if (!forceStart && !app.Restart.RestartOnExit && !app.Restart.RestartOnUnhealthy)
            return;

        UpdateStatus(slot.Id, s =>
        {
            s.ApplicationName = app.Application.DisplayName;
            s.Status = ApplicationStatus.Starting;
        });
        var started = StartProcess(app);
        slot.TrackedPid = started.Id;
        slot.HealthMonitor.Reset();
        slot.LastHealthProbeAt = DateTimeOffset.MinValue;
        _logger.LogInformation(
            "[{AppId}] Start command launched (PID {Pid}): {Command}",
            slot.Id,
            started.Id,
            startCommand);
        UpdateRunningStatus(slot, app, started, ApplicationStatus.Starting);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task EnsureWindowsServiceRunningAsync(
        AppSlot slot,
        MonitoredApplicationConfig app,
        bool forceStart,
        CancellationToken cancellationToken)
    {
        var name = app.WindowsService.ServiceName;
        if (WindowsServiceControl.IsRunning(name, out _) && !forceStart)
        {
            UpdateStatus(slot.Id, s =>
            {
                s.ApplicationName = app.Application.DisplayName;
                s.Status = ApplicationStatus.Running;
                s.LastError = null;
            });
            return;
        }

        if (!forceStart && !app.Restart.RestartOnExit && !app.Restart.RestartOnUnhealthy)
            return;

        UpdateStatus(slot.Id, s =>
        {
            s.ApplicationName = app.Application.DisplayName;
            s.Status = ApplicationStatus.Starting;
        });

        var timeout = TimeSpan.FromSeconds(Math.Max(10, app.Monitoring.GracefulTerminationTimeoutSeconds));
        if (forceStart && WindowsServiceControl.IsRunning(name, out _))
            WindowsServiceControl.Restart(name, timeout, _logger);
        else
            WindowsServiceControl.Start(name, timeout, _logger);

        UpdateStatus(slot.Id, s =>
        {
            s.ApplicationName = app.Application.DisplayName;
            s.Status = ApplicationStatus.Running;
            s.LastError = null;
            s.ProcessStartTime = _clock.UtcNow;
        });
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void StartOrRecover(MonitoredApplicationConfig app)
    {
        var requireExe = app.Kind == ApplicationKind.Process;
        var validation = ConfigValidator.ValidateApp(app, requireExistingExecutable: requireExe);
        if (!validation.IsValid)
            throw new InvalidOperationException(string.Join("; ", validation.Errors));

        if (app.IsWindowsService)
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(10, app.Monitoring.GracefulTerminationTimeoutSeconds));
            WindowsServiceControl.Restart(app.WindowsService.ServiceName, timeout, _logger);
        }
    }

    private ProcessInfo StartProcess(MonitoredApplicationConfig app)
    {
        var requireExe = app.Kind == ApplicationKind.Process;
        var validation = ConfigValidator.ValidateApp(app, requireExistingExecutable: requireExe);
        if (!validation.IsValid)
            throw new InvalidOperationException(string.Join("; ", validation.Errors));

        if (app.IsHttp || app.IsTcp)
        {
            var command = StartCommandFor(app);
            if (string.IsNullOrWhiteSpace(command))
                throw new InvalidOperationException("No start command configured.");

            return _processManager.StartShellCommand(command.Trim(), WorkDirFor(app));
        }

        return _processManager.Start(
            app.Application.ExecutablePath,
            app.Application.Arguments,
            app.Application.WorkingDirectory);
    }

    private async Task StopTrackedProcessAsync(
        AppSlot slot,
        MonitoredApplicationConfig app,
        CancellationToken cancellationToken)
    {
        if (app.IsHttp || app.IsTcp)
        {
            await StopProbeAppAsync(slot, app, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (app.IsWindowsService)
        {
            try
            {
                var timeout = TimeSpan.FromSeconds(Math.Max(5, app.Monitoring.GracefulTerminationTimeoutSeconds));
                WindowsServiceControl.Stop(app.WindowsService.ServiceName, timeout, _logger);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{AppId}] Failed stopping Windows service {Service}.",
                    slot.Id, app.WindowsService.ServiceName);
            }

            slot.TrackedPid = null;
            return;
        }

        if (string.IsNullOrWhiteSpace(app.Application.ExecutablePath))
        {
            slot.TrackedPid = null;
            return;
        }

        var instances = _processManager.FindByExecutablePath(app.Application.ExecutablePath);
        foreach (var instance in instances)
        {
            try
            {
                await _terminator.TerminateAsync(instance.Id, app.Monitoring, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{AppId}] Failed stopping PID {Pid}.", slot.Id, instance.Id);
            }
        }

        slot.TrackedPid = null;
    }

    private async Task StopProbeAppAsync(
        AppSlot slot,
        MonitoredApplicationConfig app,
        CancellationToken cancellationToken)
    {
        var workDir = WorkDirFor(app);
        var stopCommand = StopCommandFor(app);

        if (!string.IsNullOrWhiteSpace(stopCommand))
        {
            try
            {
                _logger.LogInformation("[{AppId}] Running stop command: {Command}", slot.Id, stopCommand);
                var stopper = _processManager.StartShellCommand(stopCommand.Trim(), workDir);
                _processManager.WaitForExit(
                    stopper.Id,
                    TimeSpan.FromSeconds(Math.Max(5, app.Monitoring.GracefulTerminationTimeoutSeconds)));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{AppId}] Stop command failed.", slot.Id);
            }
        }

        if (slot.TrackedPid is int pid && _processManager.IsRunning(pid))
        {
            try
            {
                await _terminator.TerminateAsync(pid, app.Monitoring, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{AppId}] Failed stopping shell PID {Pid}.", slot.Id, pid);
            }
        }

        slot.TrackedPid = null;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private async Task DelayRestartAsync(MonitoredApplicationConfig app, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(0, app.Restart.RestartDelaySeconds));
        if (delay > TimeSpan.Zero)
        {
            _logger.LogInformation("[{AppId}] Waiting {Delay} before start/restart.", app.Id, delay);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private void UpdateRunningStatus(
        AppSlot slot,
        MonitoredApplicationConfig app,
        ProcessInfo process,
        ApplicationStatus status)
    {
        var window = TimeSpan.FromMinutes(app.Restart.RestartWindowMinutes);
        UpdateStatus(slot.Id, s =>
        {
            s.ApplicationName = app.Application.DisplayName;
            s.Status = slot.RestartManager.LimitReached ? ApplicationStatus.RestartLimitReached : status;
            s.ProcessId = process.Id;
            s.ProcessStartTime = process.StartTime;
            s.RestartCount = slot.RestartManager.GetCountInWindow(window);
            s.LastRestartAt = slot.RestartManager.LastRestartAt;
            s.RestartLimitReached = slot.RestartManager.LimitReached;
            s.LastError = null;
        });
    }

    private void UpdateStatus(string applicationId, Action<WatchdogStatus> mutate)
        => _statusStore.Upsert(applicationId, mutate);

    private static WatchdogConfig CloneConfig(WatchdogConfig source)
    {
        source.Normalize();
        return new WatchdogConfig
        {
            Notifications = new NotificationsConfig
            {
                Webhook = new WebhookConfig
                {
                    Enabled = source.Notifications.Webhook.Enabled,
                    Url = source.Notifications.Webhook.Url,
                    TimeoutSeconds = source.Notifications.Webhook.TimeoutSeconds,
                    Events = new WebhookEventsConfig
                    {
                        RestartLimitReached = source.Notifications.Webhook.Events.RestartLimitReached,
                        Error = source.Notifications.Webhook.Events.Error,
                        Restart = source.Notifications.Webhook.Events.Restart,
                        Unhealthy = source.Notifications.Webhook.Events.Unhealthy,
                        Recovered = source.Notifications.Webhook.Events.Recovered
                    },
                    StatusReport = new StatusReportConfig
                    {
                        Enabled = source.Notifications.Webhook.StatusReport.Enabled,
                        IntervalMinutes = source.Notifications.Webhook.StatusReport.IntervalMinutes
                    }
                }
            },
            Applications = source.Applications.Select(a => new MonitoredApplicationConfig
            {
                Id = a.Id,
                Enabled = a.Enabled,
                Kind = a.Kind,
                Application = new ApplicationConfig
                {
                    ExecutablePath = a.Application.ExecutablePath,
                    Arguments = a.Application.Arguments,
                    WorkingDirectory = a.Application.WorkingDirectory,
                    DisplayName = a.Application.DisplayName
                },
                Http = new HttpAppConfig
                {
                    StartCommand = a.Http.StartCommand,
                    StopCommand = a.Http.StopCommand,
                    WorkingDirectory = a.Http.WorkingDirectory
                },
                Tcp = new TcpAppConfig
                {
                    Host = a.Tcp.Host,
                    Port = a.Tcp.Port,
                    StartCommand = a.Tcp.StartCommand,
                    StopCommand = a.Tcp.StopCommand,
                    WorkingDirectory = a.Tcp.WorkingDirectory
                },
                WindowsService = new WindowsServiceAppConfig
                {
                    ServiceName = a.WindowsService.ServiceName
                },
                Monitoring = new MonitoringConfig
                {
                    ProcessCheckIntervalSeconds = a.Monitoring.ProcessCheckIntervalSeconds,
                    HealthCheckIntervalSeconds = a.Monitoring.HealthCheckIntervalSeconds,
                    HealthTimeoutSeconds = a.Monitoring.HealthTimeoutSeconds,
                    GracefulTerminationTimeoutSeconds = a.Monitoring.GracefulTerminationTimeoutSeconds
                },
                Restart = new RestartConfig
                {
                    RestartOnExit = a.Restart.RestartOnExit,
                    RestartOnUnhealthy = a.Restart.RestartOnUnhealthy,
                    RestartDelaySeconds = a.Restart.RestartDelaySeconds,
                    MaxRestarts = a.Restart.MaxRestarts,
                    RestartWindowMinutes = a.Restart.RestartWindowMinutes
                },
                Health = new HealthConfig
                {
                    Enabled = a.Health.Enabled,
                    Type = a.Health.Type,
                    Url = a.Health.Url,
                    ExpectedStatusCode = a.Health.ExpectedStatusCode
                },
                Launch = new LaunchConfig
                {
                    Mode = a.Launch.Mode
                }
            }).ToList()
        };
    }

    private sealed class AppSlot
    {
        public AppSlot(string id, IClock clock)
        {
            Id = id;
            HealthMonitor = new HealthMonitor(clock);
            RestartManager = new RestartManager(clock);
        }

        public string Id { get; }
        public int? TrackedPid { get; set; }
        public DateTimeOffset? NextAllowedStartAt { get; set; }
        public DateTimeOffset LastHealthProbeAt { get; set; } = DateTimeOffset.MinValue;
        public bool ManualStopRequested { get; set; }
        public HealthMonitor HealthMonitor { get; }
        public RestartManager RestartManager { get; }
    }
}
