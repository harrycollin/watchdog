using KioskWatchdog.Core.Configuration;

namespace KioskWatchdog.Core.Tests.Configuration;

public class ConfigValidatorTests
{
    [Fact]
    public void Valid_configuration_passes()
    {
        var config = CreateValid();
        var result = ConfigValidator.Validate(config);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Invalid_configuration_rejected()
    {
        var config = CreateValid();
        config.Application.ExecutablePath = "";
        config.Health.Url = "http://example.com/health";

        var result = ConfigValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Executable path", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, e => e.Contains("localhost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_configuration_gets_sensible_defaults()
    {
        var config = WatchdogConfig.CreateDefault();
        Assert.False(config.Health.Enabled);
        Assert.True(string.IsNullOrEmpty(config.Health.Url));
        Assert.Equal(5, config.Monitoring.ProcessCheckIntervalSeconds);
    }

    [Fact]
    public void Configuration_persists_correctly()
    {
        var path = Path.Combine(Path.GetTempPath(), "kw-config-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new JsonConfigStore(path);
            var config = CreateValid();
            config.Application.DisplayName = "Persisted Kiosk";
            config.Restart.MaxRestarts = 7;

            store.Save(config);
            var loaded = store.Load();

            Assert.Equal("Persisted Kiosk", loaded.Application.DisplayName);
            Assert.Equal(7, loaded.Restart.MaxRestarts);
            Assert.Equal(config.Application.ExecutablePath, loaded.Application.ExecutablePath);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_missing_file_returns_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "kw-missing-" + Guid.NewGuid() + ".json");
        var store = new JsonConfigStore(path);
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(5, config_defaults_match(loaded));
    }

    private static int config_defaults_match(WatchdogConfig config) => config.Restart.MaxRestarts;

    internal static WatchdogConfig CreateValid() => new()
    {
        Application =
        {
            ExecutablePath = @"C:\Kiosk\MyApp\MyApp.exe",
            Arguments = "--kiosk",
            WorkingDirectory = @"C:\Kiosk\MyApp",
            DisplayName = "My Kiosk"
        },
        Monitoring =
        {
            ProcessCheckIntervalSeconds = 5,
            HealthCheckIntervalSeconds = 10,
            HealthTimeoutSeconds = 45,
            GracefulTerminationTimeoutSeconds = 2
        },
        Restart =
        {
            RestartOnExit = true,
            RestartOnUnhealthy = true,
            RestartDelaySeconds = 0,
            MaxRestarts = 5,
            RestartWindowMinutes = 10
        },
        Health =
        {
            Enabled = true,
            Type = "http",
            Url = "http://127.0.0.1:3000/health"
        }
    };
}
