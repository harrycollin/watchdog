using System.Diagnostics;
using System.IO;

namespace KioskWatchdog;

/// <summary>Launches the downloaded Inno Setup installer elevated, then exits the UI.</summary>
internal static class UpdateInstaller
{
    public static bool TryLaunch(string setupPath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(setupPath) || !File.Exists(setupPath))
        {
            error = "Installer file was not found.";
            return false;
        }

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = setupPath,
                // Quiet upgrade; Inno stops the service and replaces files in place.
                Arguments = "/SILENT /CLOSEAPPLICATIONS /NORESTART",
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(start);
            if (process is null)
            {
                error = "Could not start the installer.";
                return false;
            }

            return true;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "Update cancelled (administrator approval was declined).";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
