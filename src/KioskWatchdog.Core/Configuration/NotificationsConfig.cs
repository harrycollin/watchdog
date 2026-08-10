using System.ComponentModel.DataAnnotations;

namespace KioskWatchdog.Core.Configuration;

public sealed class NotificationsConfig
{
    public WebhookConfig Webhook { get; set; } = new();
}

public sealed class WebhookConfig
{
    public bool Enabled { get; set; }

    public string Url { get; set; } = string.Empty;

    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 10;

    public WebhookEventsConfig Events { get; set; } = new();

    public StatusReportConfig StatusReport { get; set; } = new();
}

public sealed class WebhookEventsConfig
{
    public bool RestartLimitReached { get; set; } = true;
    public bool Error { get; set; } = true;
    public bool Restart { get; set; }
    public bool Unhealthy { get; set; }
    public bool Recovered { get; set; }

    public bool IsEnabled(WebhookEventType type) => type switch
    {
        WebhookEventType.RestartLimitReached => RestartLimitReached,
        WebhookEventType.Error => Error,
        WebhookEventType.Restart => Restart,
        WebhookEventType.Unhealthy => Unhealthy,
        WebhookEventType.Recovered => Recovered,
        _ => false
    };
}

public sealed class StatusReportConfig
{
    public bool Enabled { get; set; }

    [Range(1, 1440)]
    public int IntervalMinutes { get; set; } = 60;
}

public enum WebhookEventType
{
    RestartLimitReached,
    Error,
    Restart,
    Unhealthy,
    Recovered
}
