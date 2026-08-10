using KioskWatchdog.Core.Abstractions;

namespace KioskWatchdog.Core.Tests.Fakes;

internal sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? start = null)
    {
        UtcNow = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan delta) => UtcNow = UtcNow.Add(delta);

    public void Set(DateTimeOffset value) => UtcNow = value;
}
