using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KioskWatchdog.Core.Logging;

/// <summary>
/// Helpers for configuring file + event log style logging from hosts.
/// Core itself only depends on ILogger abstractions.
/// </summary>
public static class WatchdogLogging
{
    public static string DefaultLogDirectory => Configuration.WatchdogConfig.DefaultLogsDirectory;

    public static void EnsureLogDirectory(string? directory = null)
    {
        Directory.CreateDirectory(directory ?? DefaultLogDirectory);
    }

    public static ILoggerFactory CreateNullFactory() => NullLoggerFactory.Instance;
}
