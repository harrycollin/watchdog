using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Process;

/// <summary>Helpers for monitoring and restarting Windows Services.</summary>
public static class WindowsServiceControl
{
    public static bool IsSupported => OperatingSystem.IsWindows();

    [SupportedOSPlatform("windows")]
    public static bool TryGetStatus(string serviceName, out ServiceControllerStatus status, out string? error)
    {
        status = default;
        error = null;

        if (!IsSupported)
        {
            error = "Windows Services are only supported on Windows.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            error = "Service name is required.";
            return false;
        }

        try
        {
            using var sc = new ServiceController(serviceName.Trim());
            status = sc.Status;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool IsRunning(string serviceName, out string? error)
    {
        if (!OperatingSystem.IsWindows())
        {
            error = "Windows Services are only supported on Windows.";
            return false;
        }

        return TryGetStatus(serviceName, out var status, out error)
               && status == ServiceControllerStatus.Running;
    }

    public static void Restart(string serviceName, TimeSpan stopTimeout, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Services are only supported on Windows.");

        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        using var sc = new ServiceController(serviceName.Trim());
        logger?.LogInformation("Restarting Windows service {Service} (status {Status}).", serviceName, sc.Status);

        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, stopTimeout);
        }

        sc.Refresh();
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, stopTimeout);
        }
    }

    public static void Start(string serviceName, TimeSpan timeout, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Services are only supported on Windows.");

        using var sc = new ServiceController(serviceName.Trim());
        sc.Refresh();
        if (sc.Status == ServiceControllerStatus.Running)
            return;

        logger?.LogInformation("Starting Windows service {Service}.", serviceName);
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    public static void Stop(string serviceName, TimeSpan timeout, ILogger? logger = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Services are only supported on Windows.");

        using var sc = new ServiceController(serviceName.Trim());
        sc.Refresh();
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            return;

        logger?.LogInformation("Stopping Windows service {Service}.", serviceName);
        sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }
}
