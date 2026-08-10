using System.Collections.ObjectModel;

namespace KioskWatchdog.Core.Configuration;

public sealed class ConfigValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public IReadOnlyList<string> Errors { get; }

    public ConfigValidationResult(IEnumerable<string> errors)
    {
        Errors = new ReadOnlyCollection<string>(errors.ToList());
    }

    public static ConfigValidationResult Success() => new([]);
    public static ConfigValidationResult Failure(params string[] errors) => new(errors);
}

public static class ConfigValidator
{
    public static ConfigValidationResult Validate(WatchdogConfig config, bool requireExistingExecutable = false)
    {
        var errors = new List<string>();

        if (config is null)
            return ConfigValidationResult.Failure("Configuration is null.");

        config.Normalize();

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in config.Applications)
        {
            if (string.IsNullOrWhiteSpace(app.Id))
            {
                errors.Add("Each application must have a non-empty id.");
                continue;
            }

            if (!ids.Add(app.Id.Trim()))
                errors.Add($"Duplicate application id '{app.Id}'.");

            if (!app.Enabled)
            {
                // Disabled drafts only need a unique id.
                continue;
            }

            var prefix = $"[{app.Id}] ";
            switch (app.Kind)
            {
                case ApplicationKind.Http:
                    ValidateHttpApp(app, requireExistingExecutable, errors, prefix);
                    ValidateHealth(app.Health, errors, prefix);
                    if (!app.Health.Enabled)
                        errors.Add(prefix + "HTTP apps require an enabled localhost health URL for liveness.");
                    break;
                case ApplicationKind.Tcp:
                    ValidateTcpApp(app, requireExistingExecutable, errors, prefix);
                    break;
                case ApplicationKind.WindowsService:
                    ValidateWindowsServiceApp(app, errors, prefix);
                    break;
                default:
                    ValidateApplication(app.Application, requireExistingExecutable, errors, prefix);
                    ValidateHealth(app.Health, errors, prefix);
                    break;
            }

            ValidateMonitoring(app.Monitoring, errors, prefix);
            ValidateRestart(app.Restart, errors, prefix);
        }

        ValidateNotifications(config.Notifications, errors);

        return new ConfigValidationResult(errors);
    }

    /// <summary>Validate a single monitored app (e.g. before start).</summary>
    public static ConfigValidationResult ValidateApp(
        MonitoredApplicationConfig app,
        bool requireExistingExecutable = false)
    {
        var errors = new List<string>();
        if (app is null)
            return ConfigValidationResult.Failure("Application is null.");

        if (string.IsNullOrWhiteSpace(app.Id))
            errors.Add("Application id is required.");

        var prefix = string.IsNullOrWhiteSpace(app.Id) ? "" : $"[{app.Id}] ";
        switch (app.Kind)
        {
            case ApplicationKind.Http:
                ValidateHttpApp(app, requireExistingExecutable, errors, prefix);
                ValidateHealth(app.Health, errors, prefix);
                if (!app.Health.Enabled)
                    errors.Add(prefix + "HTTP apps require an enabled localhost health URL for liveness.");
                break;
            case ApplicationKind.Tcp:
                ValidateTcpApp(app, requireExistingExecutable, errors, prefix);
                break;
            case ApplicationKind.WindowsService:
                ValidateWindowsServiceApp(app, errors, prefix);
                break;
            default:
                ValidateApplication(app.Application, requireExistingExecutable, errors, prefix);
                ValidateHealth(app.Health, errors, prefix);
                break;
        }

        ValidateMonitoring(app.Monitoring, errors, prefix);
        ValidateRestart(app.Restart, errors, prefix);

        return new ConfigValidationResult(errors);
    }

    private static void ValidateHttpApp(
        MonitoredApplicationConfig app,
        bool requireExistingPaths,
        List<string> errors,
        string prefix)
    {
        // Start command optional — empty means probe-only (status / alerts via health URL).
        var workDir = FirstNonEmpty(app.Http.WorkingDirectory, app.Application.WorkingDirectory);
        ValidateOptionalWorkDir(workDir, requireExistingPaths, errors, prefix);
    }

    private static void ValidateTcpApp(
        MonitoredApplicationConfig app,
        bool requireExistingPaths,
        List<string> errors,
        string prefix)
    {
        if (string.IsNullOrWhiteSpace(app.Tcp.Host))
            errors.Add(prefix + "TCP host is required.");
        else if (!IsLocalhost(app.Tcp.Host))
            errors.Add(prefix + "TCP host must be localhost (127.0.0.1 or localhost).");

        if (app.Tcp.Port is < 1 or > 65535)
            errors.Add(prefix + "TCP port must be between 1 and 65535.");

        var workDir = FirstNonEmpty(app.Tcp.WorkingDirectory, app.Application.WorkingDirectory);
        ValidateOptionalWorkDir(workDir, requireExistingPaths, errors, prefix);
    }

    private static void ValidateWindowsServiceApp(MonitoredApplicationConfig app, List<string> errors, string prefix)
    {
        if (string.IsNullOrWhiteSpace(app.WindowsService.ServiceName))
            errors.Add(prefix + "Windows service name is required.");
    }

    private static void ValidateOptionalWorkDir(
        string? workDir,
        bool requireExistingPaths,
        List<string> errors,
        string prefix)
    {
        if (string.IsNullOrWhiteSpace(workDir))
            return;

        if (ContainsPathInjection(workDir))
            errors.Add(prefix + "Working directory contains invalid characters.");

        if (requireExistingPaths && !Directory.Exists(workDir))
            errors.Add(prefix + $"Working directory does not exist: {workDir}");
    }

    private static void ValidateApplication(
        ApplicationConfig app,
        bool requireExistingExecutable,
        List<string> errors,
        string prefix)
    {
        if (string.IsNullOrWhiteSpace(app.ExecutablePath))
        {
            errors.Add(prefix + "Executable path is required.");
        }
        else
        {
            if (!LooksLikeWindowsExecutable(app.ExecutablePath))
                errors.Add(prefix + "Executable path must point to a .exe file.");

            if (ContainsPathInjection(app.ExecutablePath))
                errors.Add(prefix + "Executable path contains invalid characters.");

            if (requireExistingExecutable && !File.Exists(app.ExecutablePath))
                errors.Add(prefix + $"Executable does not exist: {app.ExecutablePath}");
        }

        if (!string.IsNullOrWhiteSpace(app.WorkingDirectory))
        {
            if (ContainsPathInjection(app.WorkingDirectory))
                errors.Add(prefix + "Working directory contains invalid characters.");

            if (requireExistingExecutable && !Directory.Exists(app.WorkingDirectory))
                errors.Add(prefix + $"Working directory does not exist: {app.WorkingDirectory}");
        }

        if (!string.IsNullOrEmpty(app.Arguments)
            && (app.Arguments.Contains('&', StringComparison.Ordinal)
                || app.Arguments.Contains('|', StringComparison.Ordinal)
                || app.Arguments.Contains('>', StringComparison.Ordinal)
                || app.Arguments.Contains('<', StringComparison.Ordinal)))
        {
            errors.Add(prefix + "Arguments must not contain shell redirection or chaining characters.");
        }
    }

    private static void ValidateMonitoring(MonitoringConfig monitoring, List<string> errors, string prefix)
    {
        if (monitoring.ProcessCheckIntervalSeconds < 1)
            errors.Add(prefix + "Process check interval must be at least 1 second.");

        if (monitoring.HealthCheckIntervalSeconds < 1)
            errors.Add(prefix + "Health check interval must be at least 1 second.");

        if (monitoring.HealthTimeoutSeconds < 1)
            errors.Add(prefix + "Health timeout must be at least 1 second.");

        if (monitoring.HealthTimeoutSeconds < monitoring.HealthCheckIntervalSeconds)
            errors.Add(prefix + "Health timeout should be greater than or equal to the health check interval.");

        if (monitoring.GracefulTerminationTimeoutSeconds < 1)
            errors.Add(prefix + "Graceful termination timeout must be at least 1 second.");
    }

    private static void ValidateRestart(RestartConfig restart, List<string> errors, string prefix)
    {
        if (restart.RestartDelaySeconds < 0)
            errors.Add(prefix + "Restart delay cannot be negative.");

        if (restart.MaxRestarts < 1)
            errors.Add(prefix + "Maximum restarts must be at least 1.");

        if (restart.RestartWindowMinutes < 1)
            errors.Add(prefix + "Restart window must be at least 1 minute.");
    }

    private static void ValidateHealth(HealthConfig health, List<string> errors, string prefix)
    {
        if (!health.Enabled)
            return;

        if (!string.Equals(health.Type, "http", StringComparison.OrdinalIgnoreCase))
            errors.Add(prefix + "Only 'http' health check type is supported.");

        if (string.IsNullOrWhiteSpace(health.Url))
        {
            errors.Add(prefix + "Health URL is required when health checks are enabled.");
            return;
        }

        if (!Uri.TryCreate(health.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add(prefix + "Health URL must be a valid http or https URL.");
            return;
        }

        if (!IsLocalhost(uri.Host))
            errors.Add(prefix + "Health URL must target localhost (127.0.0.1 or localhost).");

        if (health.ExpectedStatusCode is < 100 or > 599)
            errors.Add(prefix + "Expected HTTP status code must be between 100 and 599.");
    }

    private static void ValidateNotifications(NotificationsConfig notifications, List<string> errors)
    {
        var webhook = notifications.Webhook;
        var needsUrl = webhook.Enabled || webhook.StatusReport.Enabled;
        if (!needsUrl)
            return;

        if (string.IsNullOrWhiteSpace(webhook.Url))
        {
            errors.Add("Webhook URL is required when webhook notifications or status reports are enabled.");
            return;
        }

        if (!Uri.TryCreate(webhook.Url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("Webhook URL must be a valid http or https URL.");
        }

        if (webhook.TimeoutSeconds is < 1 or > 120)
            errors.Add("Webhook timeoutSeconds must be between 1 and 120.");

        if (webhook.StatusReport.IntervalMinutes is < 1 or > 1440)
            errors.Add("Webhook statusReport.intervalMinutes must be between 1 and 1440.");
    }

    private static string? FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static bool LooksLikeWindowsExecutable(string path)
        => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalhost(string host)
        => string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reject path injection; allow normal Windows paths.</summary>
    private static bool ContainsPathInjection(string value)
        => value.Contains(';', StringComparison.Ordinal)
           || value.Contains('&', StringComparison.Ordinal)
           || value.Contains('|', StringComparison.Ordinal)
           || value.Contains('`', StringComparison.Ordinal)
           || value.Contains('$', StringComparison.Ordinal);
}
