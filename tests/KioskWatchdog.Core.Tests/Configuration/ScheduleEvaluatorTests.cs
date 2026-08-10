using KioskWatchdog.Core.Configuration;

namespace KioskWatchdog.Core.Tests.Configuration;

public class ScheduleEvaluatorTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    [Theory]
    [InlineData("2026-03-09T10:00:00Z", true)]  // Monday 10:00
    [InlineData("2026-03-09T08:59:00Z", false)] // Monday 08:59
    [InlineData("2026-03-09T18:00:00Z", false)] // Monday 18:00 exclusive end
    [InlineData("2026-03-14T10:00:00Z", false)] // Saturday
    public void Weekday_daytime_window(string utc, bool expected)
    {
        var schedule = new ScheduleConfig
        {
            Enabled = true,
            StartTime = "09:00",
            EndTime = "18:00",
            DaysOfWeek =
            [
                DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                DayOfWeek.Thursday, DayOfWeek.Friday
            ]
        };

        var actual = ScheduleEvaluator.IsWithinSchedule(schedule, DateTimeOffset.Parse(utc), Utc);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("2026-03-09T22:30:00Z", true)]  // Mon 22:30
    [InlineData("2026-03-10T03:00:00Z", true)]  // Tue 03:00 (Mon overnight)
    [InlineData("2026-03-10T06:00:00Z", false)] // Tue 06:00 end
    [InlineData("2026-03-10T22:30:00Z", false)] // Tue night — Tue not selected
    public void Overnight_window_uses_start_day(string utc, bool expected)
    {
        var schedule = new ScheduleConfig
        {
            Enabled = true,
            StartTime = "22:00",
            EndTime = "06:00",
            DaysOfWeek = [DayOfWeek.Monday]
        };

        var actual = ScheduleEvaluator.IsWithinSchedule(schedule, DateTimeOffset.Parse(utc), Utc);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Disabled_schedule_is_always_active()
    {
        var schedule = new ScheduleConfig { Enabled = false, StartTime = "09:00", EndTime = "10:00" };
        Assert.True(ScheduleEvaluator.IsWithinSchedule(
            schedule, DateTimeOffset.Parse("2026-03-09T03:00:00Z"), Utc));
    }

    [Fact]
    public void Equal_start_and_end_means_full_selected_days()
    {
        var schedule = new ScheduleConfig
        {
            Enabled = true,
            StartTime = "00:00",
            EndTime = "00:00",
            DaysOfWeek = [DayOfWeek.Monday]
        };

        Assert.True(ScheduleEvaluator.IsWithinSchedule(
            schedule, DateTimeOffset.Parse("2026-03-09T15:00:00Z"), Utc));
        Assert.False(ScheduleEvaluator.IsWithinSchedule(
            schedule, DateTimeOffset.Parse("2026-03-10T15:00:00Z"), Utc));
    }

    [Fact]
    public void Next_transition_reports_stop_while_inside_window()
    {
        var schedule = new ScheduleConfig
        {
            Enabled = true,
            StartTime = "09:00",
            EndTime = "18:00",
            DaysOfWeek = [DayOfWeek.Monday]
        };

        var now = DateTimeOffset.Parse("2026-03-09T10:00:00Z");
        var next = ScheduleEvaluator.GetNextTransition(schedule, now, Utc);
        Assert.NotNull(next);
        Assert.False(next.Value.BecomesActive);
        Assert.Equal(DateTimeOffset.Parse("2026-03-09T18:00:00Z"), next.Value.AtUtc);
        Assert.Contains("Stops", ScheduleEvaluator.FormatTransition(next, now), StringComparison.Ordinal);
    }

    [Fact]
    public void Next_transition_reports_start_while_outside_window()
    {
        var schedule = new ScheduleConfig
        {
            Enabled = true,
            StartTime = "09:00",
            EndTime = "18:00",
            DaysOfWeek = [DayOfWeek.Monday, DayOfWeek.Tuesday]
        };

        var now = DateTimeOffset.Parse("2026-03-09T19:00:00Z"); // Monday evening
        var next = ScheduleEvaluator.GetNextTransition(schedule, now, Utc);
        Assert.NotNull(next);
        Assert.True(next.Value.BecomesActive);
        Assert.Equal(DateTimeOffset.Parse("2026-03-10T09:00:00Z"), next.Value.AtUtc);
    }

    [Fact]
    public void FormatUptime_includes_hours_minutes_seconds()
    {
        Assert.Equal("01:02:03", ScheduleEvaluator.FormatUptime(new TimeSpan(1, 2, 3)));
        Assert.Equal("—", ScheduleEvaluator.FormatUptime(null));
    }
}
