namespace KioskWatchdog.Core.Configuration;

/// <summary>Next schedule boundary relative to "now".</summary>
public readonly record struct ScheduleTransition(
    DateTimeOffset AtUtc,
    DateTimeOffset AtLocal,
    bool BecomesActive);

/// <summary>
/// Pure schedule evaluation against a clock instant (converted to a time zone).
/// </summary>
public static class ScheduleEvaluator
{
    /// <summary>
    /// When schedule is disabled, always returns true (no restriction).
    /// Uses <paramref name="timeZone"/> (default: machine local) for day/time-of-day.
    /// </summary>
    public static bool IsWithinSchedule(
        ScheduleConfig? schedule,
        DateTimeOffset utcNow,
        TimeZoneInfo? timeZone = null)
    {
        if (schedule is null || !schedule.Enabled)
            return true;

        if (!TryGetWindow(schedule, out var start, out var end, out var days))
            return false;

        var tz = timeZone ?? TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(utcNow, tz);
        return IsActiveAt(local, start, end, days);
    }

    /// <summary>
    /// Next time the active/inactive state flips. Null when schedule is off or always-on.
    /// </summary>
    public static ScheduleTransition? GetNextTransition(
        ScheduleConfig? schedule,
        DateTimeOffset utcNow,
        TimeZoneInfo? timeZone = null)
    {
        if (schedule is null || !schedule.Enabled)
            return null;

        if (!TryGetWindow(schedule, out var start, out var end, out var days))
            return null;

        var tz = timeZone ?? TimeZoneInfo.Local;
        var localNow = TimeZoneInfo.ConvertTime(utcNow, tz);
        var currentlyActive = IsActiveAt(localNow, start, end, days);

        // Scan minute boundaries for up to 8 days — simple and correct across DST.
        var cursor = FloorToMinute(localNow).AddMinutes(1);
        var limit = localNow.AddDays(8);
        while (cursor <= limit)
        {
            var active = IsActiveAt(cursor, start, end, days);
            if (active != currentlyActive)
            {
                return new ScheduleTransition(
                    cursor.ToUniversalTime(),
                    cursor,
                    BecomesActive: active);
            }

            cursor = cursor.AddMinutes(1);
        }

        return null;
    }

    public static string FormatTransition(ScheduleTransition? transition, DateTimeOffset utcNow)
    {
        if (transition is null)
            return "—";

        var t = transition.Value;
        var remaining = t.AtUtc - utcNow;
        if (remaining < TimeSpan.Zero)
            remaining = TimeSpan.Zero;

        var when = t.AtLocal.ToString("ddd HH:mm");
        var rel = FormatRemaining(remaining);
        return t.BecomesActive
            ? $"Starts {when} (in {rel})"
            : $"Stops {when} (in {rel})";
    }

    public static string FormatUptime(TimeSpan? uptime)
    {
        if (uptime is null || uptime.Value < TimeSpan.Zero)
            return "—";

        var t = uptime.Value;
        if (t.TotalDays >= 1)
            return $"{(int)t.TotalDays}d {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}";
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    private static bool TryGetWindow(
        ScheduleConfig schedule,
        out TimeSpan start,
        out TimeSpan end,
        out IReadOnlyCollection<DayOfWeek> days)
    {
        start = default;
        end = default;
        days = AllDays;

        if (!ScheduleConfig.TryParseTime(schedule.StartTime, out start)
            || !ScheduleConfig.TryParseTime(schedule.EndTime, out end))
        {
            return false;
        }

        days = schedule.DaysOfWeek is { Count: > 0 } ? schedule.DaysOfWeek : AllDays;
        return true;
    }

    private static bool IsActiveAt(
        DateTimeOffset local,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyCollection<DayOfWeek> days)
    {
        var tod = local.TimeOfDay;
        var day = local.DayOfWeek;

        if (start == end)
            return days.Contains(day);

        if (start < end)
            return tod >= start && tod < end && days.Contains(day);

        if (tod >= start && days.Contains(day))
            return true;

        return tod < end && days.Contains(PreviousDay(day));
    }

    private static DateTimeOffset FloorToMinute(DateTimeOffset value)
        => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Offset);

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        if (remaining.TotalMinutes >= 1)
            return $"{(int)remaining.TotalMinutes}m";
        return $"{Math.Max(0, (int)remaining.TotalSeconds)}s";
    }

    private static readonly DayOfWeek[] AllDays =
    [
        DayOfWeek.Sunday,
        DayOfWeek.Monday,
        DayOfWeek.Tuesday,
        DayOfWeek.Wednesday,
        DayOfWeek.Thursday,
        DayOfWeek.Friday,
        DayOfWeek.Saturday
    ];

    private static DayOfWeek PreviousDay(DayOfWeek day)
        => day == DayOfWeek.Sunday ? DayOfWeek.Saturday : day - 1;
}
