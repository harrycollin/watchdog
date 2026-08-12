using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace KioskWatchdog.Core.Process;

/// <summary>
/// Reads/writes the KioskWatchdog Windows Service start type (auto vs manual).
/// </summary>
public static class WatchdogServiceManager
{
    public const string ServiceName = Configuration.ServiceSettingsConfig.WindowsServiceName;

    // SERVICE_AUTO_START = 2, SERVICE_DEMAND_START = 3, SERVICE_DISABLED = 4
    private const int StartTypeAuto = 2;
    private const int StartTypeDemand = 3;

    [SupportedOSPlatformGuard("windows")]
    public static bool IsSupported => OperatingSystem.IsWindows();

    public static bool TryGetIsRunning(out bool isRunning, out string? error)
    {
        isRunning = false;
        error = null;
        if (!IsSupported)
        {
            error = "Windows only.";
            return false;
        }

        try
        {
            using var sc = new ServiceController(ServiceName);
            isRunning = sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryGetStartOnBoot(out bool startOnBoot, out string? error)
    {
        startOnBoot = true;
        error = null;
        if (!IsSupported)
        {
            error = "Windows only.";
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{ServiceName}",
                writable: false);
            if (key?.GetValue("Start") is int start)
            {
                startOnBoot = start == StartTypeAuto;
                return true;
            }

            error = $"Service '{ServiceName}' was not found.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryStart(out string? error)
    {
        error = null;
        if (!IsSupported)
        {
            error = "Windows only.";
            return false;
        }

        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
                return true;

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryStop(out string? error)
    {
        error = null;
        if (!IsSupported)
        {
            error = "Windows only.";
            return false;
        }

        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                return true;

            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Sets SCM start type to automatic or demand (manual). Requires elevation.
    /// </summary>
    public static bool TrySetStartOnBoot(bool startOnBoot, out string? error, ILogger? logger = null)
    {
        error = null;
        if (!IsSupported)
        {
            error = "Windows only.";
            return false;
        }

        var mode = startOnBoot ? "auto" : "demand";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"config {ServiceName} start= {mode}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi)
                                ?? throw new InvalidOperationException("Failed to start sc.exe.");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15_000);

            if (process.ExitCode != 0)
            {
                error = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                if (string.IsNullOrWhiteSpace(error))
                    error = $"sc.exe exited with code {process.ExitCode}.";
                logger?.LogWarning("Failed to set service start type to {Mode}: {Error}", mode, error);
                return false;
            }

            logger?.LogInformation("Set {Service} start type to {Mode}.", ServiceName, mode);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger?.LogError(ex, "Failed to set service start type.");
            return false;
        }
    }
}
