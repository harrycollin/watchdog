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
        {
            return ConfigValidationResult.Failure("Configuration is null.");
        }

        ValidateApplication(config.Application, requireExistingExecutable, errors);
        ValidateMonitoring(config.Monitoring, errors);
        ValidateRestart(config.Restart, errors);
        ValidateHealth(config.Health, errors);

        return new ConfigValidationResult(errors);
    }

    private static void ValidateApplication(ApplicationConfig app, bool requireExistingExecutable, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(app.ExecutablePath))
        {
            errors.Add("Executable path is required.");
        }
        else
        {
            if (!LooksLikeWindowsExecutable(app.ExecutablePath))
            {
                errors.Add("Executable path must point to a .exe file.");
            }

            if (ContainsShellMetacharacters(app.ExecutablePath))
            {
                errors.Add("Executable path contains invalid characters.");
            }

            if (requireExistingExecutable && !File.Exists(app.ExecutablePath))
            {
                errors.Add($"Executable does not exist: {app.ExecutablePath}");
            }
        }

        if (!string.IsNullOrWhiteSpace(app.WorkingDirectory))
        {
            if (ContainsShellMetacharacters(app.WorkingDirectory))
            {
                errors.Add("Working directory contains invalid characters.");
            }

            if (requireExistingExecutable && !Directory.Exists(app.WorkingDirectory))
            {
                errors.Add($"Working directory does not exist: {app.WorkingDirectory}");
            }
        }

        if (ContainsShellMetacharacters(app.Arguments))
        {
            // Arguments may legitimately contain quotes/spaces; block only dangerous redirection/chaining.
            if (app.Arguments.Contains('&', StringComparison.Ordinal)
                || app.Arguments.Contains('|', StringComparison.Ordinal)
                || app.Arguments.Contains('>', StringComparison.Ordinal)
                || app.Arguments.Contains('<', StringComparison.Ordinal))
            {
                errors.Add("Arguments must not contain shell redirection or chaining characters.");
            }
        }
    }

    private static void ValidateMonitoring(MonitoringConfig monitoring, List<string> errors)
    {
        if (monitoring.ProcessCheckIntervalSeconds < 1)
            errors.Add("Process check interval must be at least 1 second.");

        if (monitoring.HealthCheckIntervalSeconds < 1)
            errors.Add("Health check interval must be at least 1 second.");

        if (monitoring.HealthTimeoutSeconds < 1)
            errors.Add("Health timeout must be at least 1 second.");

        if (monitoring.HealthTimeoutSeconds < monitoring.HealthCheckIntervalSeconds)
            errors.Add("Health timeout should be greater than or equal to the health check interval.");

        if (monitoring.GracefulTerminationTimeoutSeconds < 1)
            errors.Add("Graceful termination timeout must be at least 1 second.");
    }

    private static void ValidateRestart(RestartConfig restart, List<string> errors)
    {
        if (restart.RestartDelaySeconds < 0)
            errors.Add("Restart delay cannot be negative.");

        if (restart.MaxRestarts < 1)
            errors.Add("Maximum restarts must be at least 1.");

        if (restart.RestartWindowMinutes < 1)
            errors.Add("Restart window must be at least 1 minute.");
    }

    private static void ValidateHealth(HealthConfig health, List<string> errors)
    {
        if (!health.Enabled)
            return;

        if (!string.Equals(health.Type, "http", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Only 'http' health check type is supported.");
        }

        if (string.IsNullOrWhiteSpace(health.Url))
        {
            errors.Add("Health URL is required when health checks are enabled.");
            return;
        }

        if (!Uri.TryCreate(health.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("Health URL must be a valid http or https URL.");
            return;
        }

        if (!IsLocalhost(uri.Host))
        {
            errors.Add("Health URL must target localhost (127.0.0.1 or localhost).");
        }
    }

    private static bool LooksLikeWindowsExecutable(string path)
        => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalhost(string host)
        => string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsShellMetacharacters(string value)
        => value.Contains(';', StringComparison.Ordinal)
           || value.Contains('&', StringComparison.Ordinal)
           || value.Contains('|', StringComparison.Ordinal)
           || value.Contains('`', StringComparison.Ordinal)
           || value.Contains('$', StringComparison.Ordinal);
}
