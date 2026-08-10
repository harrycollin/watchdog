using KioskWatchdog.Core.Configuration;

namespace KioskWatchdog.Core.Logging;

/// <summary>Reads rolling watchdog log files for the in-app viewer.</summary>
public static class LogHistoryReader
{
    public static string LogDirectory => WatchdogConfig.DefaultLogsDirectory;

    public static IReadOnlyList<LogFileInfo> ListFiles(string? directory = null)
    {
        var dir = directory ?? LogDirectory;
        if (!Directory.Exists(dir))
            return [];

        return Directory.EnumerateFiles(dir, "watchdog-*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => new LogFileInfo(f.FullName, f.Name, f.Length, f.LastWriteTime))
            .ToList();
    }

    /// <summary>Reads the newest log file (or a specific path), returning the last <paramref name="maxLines"/> lines.</summary>
    public static string ReadTail(string? path = null, int maxLines = 500, string? directory = null)
    {
        path ??= ListFiles(directory).FirstOrDefault()?.Path;
        if (path is null || !File.Exists(path))
            return "No log files yet. The service writes logs after it starts.";

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            if (maxLines <= 0)
                return reader.ReadToEnd();

            var ring = new Queue<string>(maxLines);
            while (reader.ReadLine() is { } line)
            {
                if (ring.Count == maxLines)
                    ring.Dequeue();
                ring.Enqueue(line);
            }

            return ring.Count == 0 ? "(empty log file)" : string.Join(Environment.NewLine, ring);
        }
        catch (Exception ex)
        {
            return $"Could not read log file: {ex.Message}";
        }
    }
}

public sealed record LogFileInfo(string Path, string Name, long LengthBytes, DateTime LastWriteTime);
