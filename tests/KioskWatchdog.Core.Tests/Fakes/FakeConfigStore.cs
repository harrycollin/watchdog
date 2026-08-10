using KioskWatchdog.Core.Configuration;

namespace KioskWatchdog.Core.Tests.Fakes;

internal sealed class FakeConfigStore : IConfigStore
{
    private WatchdogConfig _config;

    public FakeConfigStore(WatchdogConfig? config = null)
    {
        _config = config ?? WatchdogConfig.CreateDefault();
        _config.Normalize();
        ConfigPath = Path.Combine(Path.GetTempPath(), "kw-fake-config.json");
    }

    public string ConfigPath { get; }

    public WatchdogConfig Load()
    {
        // Tests mutate via Save; return the live instance after Normalize.
        _config.Normalize();
        return _config;
    }

    public void Save(WatchdogConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Normalize();
        _config = config;
    }
}
