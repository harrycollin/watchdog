namespace KioskWatchdog.Core.Configuration;

/// <summary>Global settings for the KioskWatchdog Windows Service itself.</summary>
public sealed class ServiceSettingsConfig
{
    public const string WindowsServiceName = "KioskWatchdog";

    /// <summary>
    /// When true, Windows starts the watchdog service automatically at boot
    /// (<c>sc start= auto</c>). When false, the service is manual (<c>demand</c>).
    /// Does not open the UI; only the background service.
    /// </summary>
    public bool StartOnBoot { get; set; } = true;
}
