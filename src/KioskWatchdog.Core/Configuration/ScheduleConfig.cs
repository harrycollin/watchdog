using System.Globalization;

namespace KioskWatchdog.Core.Configuration;

/// <summary>
/// Optional local-time window when an application should be running.
/// Evaluated each monitor tick (desired-state), not as one-shot timers.
/// </summary>
public sealed class ScheduleConfig
{
    public bool Enabled { get; set; }

    /// <summary>Local start time of day, <c>HH:mm</c> (inclusive).</summary>
    public string StartTime { get; set; } = "09:00";

    /// <summary>
    /// Local end time of day, <c>HH:mm</c> (exclusive).
    /// When earlier than <see cref="StartTime"/>, the window wraps past midnight.
    /// </summary>
    public string EndTime { get; set; } = "18:00";

    /// <summary>
    /// Days the window applies. Empty means every day once normalized.
    /// For overnight windows, the day is the day the window <em>starts</em>.
    /// </summary>
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();

    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim();
        if (TimeSpan.TryParseExact(value, @"h\:mm", CultureInfo.InvariantCulture, out time)
            || TimeSpan.TryParseExact(value, @"hh\:mm", CultureInfo.InvariantCulture, out time)
            || TimeSpan.TryParseExact(value, @"h\:mm\:ss", CultureInfo.InvariantCulture, out time)
            || TimeSpan.TryParseExact(value, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out time))
        {
            if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
                return false;
            return true;
        }

        return false;
    }

    public static string FormatTime(TimeSpan time)
        => $"{(int)time.TotalHours:00}:{time.Minutes:00}";
}
