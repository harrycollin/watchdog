using KioskWatchdog.Core.Process;
using KioskWatchdog.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace KioskWatchdog.Core.Tests.Process;

public class ProcessTerminatorTests
{
    [Fact]
    public async Task Application_terminates_successfully_via_graceful_close()
    {
        var processes = new FakeProcessManager();
        var started = processes.Start(@"C:\App\app.exe", "", @"C:\App");
        var terminator = new ProcessTerminator(processes, NullLogger<ProcessTerminator>.Instance);

        await terminator.TerminateAsync(started.Id, TimeSpan.FromSeconds(2));

        Assert.False(processes.IsRunning(started.Id));
        Assert.Equal(0, processes.KillCallCount);
    }

    [Fact]
    public async Task Force_termination_after_graceful_timeout()
    {
        var processes = new FakeProcessManager
        {
            IgnoreGracefulClose = true
        };
        var started = processes.Start(@"C:\App\app.exe", "", @"C:\App");
        var terminator = new ProcessTerminator(processes, NullLogger<ProcessTerminator>.Instance);

        await terminator.TerminateAsync(started.Id, TimeSpan.FromMilliseconds(300));

        Assert.False(processes.IsRunning(started.Id));
        Assert.Equal(1, processes.KillCallCount);
    }
}
