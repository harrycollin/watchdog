using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Notifications;
using KioskWatchdog.Core.Status;
using KioskWatchdog.Core.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;

namespace KioskWatchdog.Core.Tests.Notifications;

public class WebhookNotificationServiceTests
{
    [Fact]
    public async Task Enqueues_enabled_transition_events()
    {
        var config = CreateConfig(webhookEnabled: true, restart: true);
        var store = new FakeConfigStore(config);
        var statusStore = new WatchdogStatusStore();
        var client = new FakeWebhookClient();
        using var service = new WebhookNotificationService(
            store,
            statusStore,
            client,
            NullLogger<WebhookNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        statusStore.Upsert("kiosk", s =>
        {
            s.ApplicationName = "Kiosk";
            s.Status = ApplicationStatus.Running;
        });

        statusStore.Upsert("kiosk", s => s.Status = ApplicationStatus.Restarting);

        await WaitForAsync(() => client.Posts.Count >= 1, TimeSpan.FromSeconds(5));

        await service.StopAsync(CancellationToken.None);

        var post = Assert.Single(client.Posts);
        Assert.Equal("event", post.Payload.Type);
        Assert.Equal(WebhookEventType.Restart, post.Payload.Event);
        Assert.Equal("https://example.test/hook", post.Url);
        Assert.NotNull(post.Payload.Application);
        Assert.Equal("kiosk", post.Payload.Application!.Id);
    }

    [Fact]
    public async Task Filters_disabled_event_types()
    {
        var config = CreateConfig(webhookEnabled: true, restart: false);
        var store = new FakeConfigStore(config);
        var statusStore = new WatchdogStatusStore();
        var client = new FakeWebhookClient();
        using var service = new WebhookNotificationService(
            store,
            statusStore,
            client,
            NullLogger<WebhookNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);

        statusStore.Upsert("kiosk", s => s.Status = ApplicationStatus.Running);
        statusStore.Upsert("kiosk", s => s.Status = ApplicationStatus.Restarting);

        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        Assert.Empty(client.Posts);
    }

    [Fact]
    public async Task Status_report_includes_all_configured_apps()
    {
        var config = CreateConfig(webhookEnabled: false, statusReport: true, intervalMinutes: 60);
        config.Applications.Add(new MonitoredApplicationConfig
        {
            Id = "other",
            Enabled = false,
            Application = { DisplayName = "Other", ExecutablePath = @"C:\Other\App.exe" }
        });

        var store = new FakeConfigStore(config);
        var statusStore = new WatchdogStatusStore();
        statusStore.Upsert("kiosk", s =>
        {
            s.ApplicationName = "Kiosk";
            s.Status = ApplicationStatus.Running;
        });

        var client = new FakeWebhookClient();
        using var service = new WebhookNotificationService(
            store,
            statusStore,
            client,
            NullLogger<WebhookNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);
        service.EnqueueStatusReportForTests();

        await WaitForAsync(() => client.Posts.Count >= 1, TimeSpan.FromSeconds(2));
        await service.StopAsync(CancellationToken.None);

        var post = Assert.Single(client.Posts);
        Assert.Equal("statusReport", post.Payload.Type);
        Assert.Null(post.Payload.Event);
        Assert.NotNull(post.Payload.Applications);
        Assert.Equal(2, post.Payload.Applications!.Count);
        Assert.Contains(post.Payload.Applications, a => a.Id == "kiosk" && a.Enabled);
        Assert.Contains(post.Payload.Applications, a => a.Id == "other" && !a.Enabled);
    }

    [Fact]
    public async Task Hanging_client_does_not_block_status_upserts()
    {
        var config = CreateConfig(webhookEnabled: true, error: true);
        var store = new FakeConfigStore(config);
        var statusStore = new WatchdogStatusStore();
        var client = new FakeWebhookClient { Hang = true };
        using var service = new WebhookNotificationService(
            store,
            statusStore,
            client,
            NullLogger<WebhookNotificationService>.Instance);

        await service.StartAsync(CancellationToken.None);

        statusStore.Upsert("kiosk", s => s.Status = ApplicationStatus.Running);
        statusStore.Upsert("kiosk", s => s.Status = ApplicationStatus.Error);

        // Give the worker a moment to pick up the first hung POST.
        await Task.Delay(50);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            statusStore.Upsert("kiosk", s =>
                s.Status = i % 2 == 0 ? ApplicationStatus.Running : ApplicationStatus.Error);
        }

        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Upserts blocked for {sw.Elapsed}");

        client.ReleaseHang();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Payload_factory_status_report_shape()
    {
        var apps = new[]
        {
            new WebhookApplicationPayload { Id = "a", Name = "A", Enabled = true, Status = ApplicationStatus.Running },
            new WebhookApplicationPayload { Id = "b", Name = "B", Enabled = false, Status = ApplicationStatus.Stopped }
        };

        var payload = WebhookPayloadFactory.CreateStatusReport(apps);
        Assert.Equal("statusReport", payload.Type);
        Assert.Null(payload.Event);
        Assert.Null(payload.Application);
        Assert.Equal(2, payload.Applications!.Count);
    }

    private static WatchdogConfig CreateConfig(
        bool webhookEnabled = false,
        bool statusReport = false,
        int intervalMinutes = 60,
        bool restart = false,
        bool error = true)
    {
        return new WatchdogConfig
        {
            Notifications = new NotificationsConfig
            {
                Webhook = new WebhookConfig
                {
                    Enabled = webhookEnabled,
                    Url = "https://example.test/hook",
                    TimeoutSeconds = 5,
                    Events = new WebhookEventsConfig
                    {
                        RestartLimitReached = true,
                        Error = error,
                        Restart = restart,
                        Unhealthy = false,
                        Recovered = false
                    },
                    StatusReport = new StatusReportConfig
                    {
                        Enabled = statusReport,
                        IntervalMinutes = intervalMinutes
                    }
                }
            },
            Applications =
            {
                new MonitoredApplicationConfig
                {
                    Id = "kiosk",
                    Enabled = true,
                    Application =
                    {
                        DisplayName = "Kiosk",
                        ExecutablePath = @"C:\Kiosk\App.exe"
                    }
                }
            }
        };
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }

        Assert.Fail($"Condition not met within {timeout}.");
    }
}
