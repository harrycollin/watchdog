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
        config.Primary().Application.ExecutablePath = "";
        config.Primary().Health.Enabled = true;
        config.Primary().Health.Url = "http://example.com/health";

        var result = ConfigValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Executable path", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, e => e.Contains("localhost", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Empty_applications_is_valid_idle_config()
    {
        var result = ConfigValidator.Validate(WatchdogConfig.CreateDefault());
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Webhook_enabled_requires_url()
    {
        var config = CreateValid();
        config.Notifications.Webhook.Enabled = true;
        config.Notifications.Webhook.Url = "";

        var result = ConfigValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Webhook URL", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Status_report_enabled_requires_absolute_http_url()
    {
        var config = CreateValid();
        config.Notifications.Webhook.StatusReport.Enabled = true;
        config.Notifications.Webhook.Url = "not-a-url";

        var result = ConfigValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("http", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Valid_webhook_configuration_passes()
    {
        var config = CreateValid();
        config.Notifications.Webhook.Enabled = true;
        config.Notifications.Webhook.Url = "https://hooks.example.com/watchdog";
        config.Notifications.Webhook.StatusReport.Enabled = true;
        config.Notifications.Webhook.StatusReport.IntervalMinutes = 30;

        Assert.True(ConfigValidator.Validate(config).IsValid);
    }

    [Fact]
    public void Monitored_application_defaults_are_sensible()
    {
        var app = new MonitoredApplicationConfig();
        Assert.False(app.Health.Enabled);
        Assert.True(string.IsNullOrEmpty(app.Health.Url));
        Assert.Equal(5, app.Monitoring.ProcessCheckIntervalSeconds);
        Assert.Equal(5, app.Restart.MaxRestarts);
    }

    [Fact]
    public void Duplicate_application_ids_rejected()
    {
        var config = CreateValid();
        config.Applications.Add(CreateValid().Primary());
        config.Applications[1].Id = config.Applications[0].Id;
        config.Applications[1].Application.ExecutablePath = @"C:\Other\App.exe";

        var result = ConfigValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Disabled_app_skips_executable_validation()
    {
        var config = CreateValid();
        config.Primary().Enabled = false;
        config.Primary().Application.ExecutablePath = "";

        Assert.True(ConfigValidator.Validate(config).IsValid);
    }

    [Fact]
    public void Configuration_persists_applications_array()
    {
        var path = Path.Combine(Path.GetTempPath(), "kw-config-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new JsonConfigStore(path);
            var config = CreateValid();
            config.Primary().Application.DisplayName = "Persisted Kiosk";
            config.Primary().Restart.MaxRestarts = 7;

            store.Save(config);
            var loaded = store.Load();

            Assert.Single(loaded.Applications);
            Assert.Equal("Persisted Kiosk", loaded.Primary().Application.DisplayName);
            Assert.Equal(7, loaded.Primary().Restart.MaxRestarts);
            Assert.Equal(config.Primary().Application.ExecutablePath, loaded.Primary().Application.ExecutablePath);
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
        Assert.Empty(loaded.Applications);
    }

    [Fact]
    public void Valid_tcp_app_passes()
    {
        var config = new WatchdogConfig
        {
            Applications =
            {
                new MonitoredApplicationConfig
                {
                    Id = "tcp1",
                    Kind = ApplicationKind.Tcp,
                    Application = { DisplayName = "TCP" },
                    Tcp = { Host = "127.0.0.1", Port = 8080 },
                    Monitoring = { HealthTimeoutSeconds = 10, HealthCheckIntervalSeconds = 5 }
                }
            }
        };
        Assert.True(ConfigValidator.Validate(config).IsValid);
    }

    [Fact]
    public void Windows_service_requires_name()
    {
        var config = new WatchdogConfig
        {
            Applications =
            {
                new MonitoredApplicationConfig
                {
                    Id = "svc",
                    Kind = ApplicationKind.WindowsService,
                    Application = { DisplayName = "Svc" },
                    WindowsService = { ServiceName = "" }
                }
            }
        };
        var result = ConfigValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("service name", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Http_app_requires_start_command_and_health_url()
    {
        var config = new WatchdogConfig
        {
            Applications =
            {
                new MonitoredApplicationConfig
                {
                    Id = "site",
                    Kind = ApplicationKind.Http,
                    Application = { DisplayName = "Site" },
                    Http = { StartCommand = "" },
                    Health = { Enabled = true, Url = "" },
                    Monitoring = { HealthTimeoutSeconds = 10, HealthCheckIntervalSeconds = 5 }
                }
            }
        };

        var result = ConfigValidator.Validate(config);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Health URL", StringComparison.OrdinalIgnoreCase)
                                            || e.Contains("health", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Valid_http_app_passes()
    {
        var config = CreateValidHttp();
        Assert.True(ConfigValidator.Validate(config).IsValid);
    }

    internal static WatchdogConfig CreateValid()
    {
        var config = new WatchdogConfig
        {
            Applications =
            {
                new MonitoredApplicationConfig
                {
                    Id = WatchdogConfig.DefaultApplicationId,
                    Enabled = true,
                    Kind = ApplicationKind.Process,
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
                }
            }
        };
        return config;
    }

    internal static WatchdogConfig CreateValidHttp()
    {
        var work = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        return new WatchdogConfig
        {
            Applications =
            {
                new MonitoredApplicationConfig
                {
                    Id = "site",
                    Enabled = true,
                    Kind = ApplicationKind.Http,
                    Application = { DisplayName = "Local site" },
                    Http =
                    {
                        StartCommand = "npm start",
                        StopCommand = "",
                        WorkingDirectory = work
                    },
                    Monitoring =
                    {
                        ProcessCheckIntervalSeconds = 1,
                        HealthCheckIntervalSeconds = 1,
                        HealthTimeoutSeconds = 2,
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
                }
            }
        };
    }
}

internal static class ConfigTestExtensions
{
    public static MonitoredApplicationConfig Primary(this WatchdogConfig config)
    {
        config.Normalize();
        Assert.NotEmpty(config.Applications);
        return config.Applications[0];
    }
}
