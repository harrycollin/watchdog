using KioskWatchdog.Core.Restart;
using KioskWatchdog.Core.Tests.Fakes;

namespace KioskWatchdog.Core.Tests.Restart;

public class RestartManagerTests
{
    [Fact]
    public void Restart_counter_increments()
    {
        var clock = new FakeClock();
        var manager = new RestartManager(clock);
        var window = TimeSpan.FromMinutes(10);

        manager.RecordRestart(5, window);
        manager.RecordRestart(5, window);

        Assert.Equal(2, manager.GetCountInWindow(window));
        Assert.NotNull(manager.LastRestartAt);
    }

    [Fact]
    public void Counter_resets_after_configured_window()
    {
        var clock = new FakeClock();
        var manager = new RestartManager(clock);
        var window = TimeSpan.FromMinutes(10);

        manager.RecordRestart(5, window);
        manager.RecordRestart(5, window);
        clock.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(0, manager.GetCountInWindow(window));
        Assert.True(manager.CanRestart(5, window));
    }

    [Fact]
    public void Maximum_restart_count_prevents_further_restarts()
    {
        var clock = new FakeClock();
        var manager = new RestartManager(clock);
        var window = TimeSpan.FromMinutes(10);

        for (var i = 0; i < 5; i++)
            manager.RecordRestart(5, window);

        Assert.False(manager.CanRestart(5, window));
        Assert.True(manager.LimitReached);
    }

    [Fact]
    public void Manual_reset_clears_the_counter()
    {
        var clock = new FakeClock();
        var manager = new RestartManager(clock);
        var window = TimeSpan.FromMinutes(10);

        for (var i = 0; i < 5; i++)
            manager.RecordRestart(5, window);

        manager.Reset();

        Assert.Equal(0, manager.GetCountInWindow(window));
        Assert.False(manager.LimitReached);
        Assert.True(manager.CanRestart(5, window));
    }
}
