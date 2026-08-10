using System.Text.Json.Serialization;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Status;

namespace KioskWatchdog.Core.Notifications;

public sealed class WebhookPayload
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>"event" or "statusReport".</summary>
    public string Type { get; set; } = "event";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebhookEventType? Event { get; set; }

    public DateTimeOffset SentAt { get; set; }

    public string MachineName { get; set; } = Environment.MachineName;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebhookApplicationPayload? Application { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WebhookApplicationPayload>? Applications { get; set; }
}

public sealed class WebhookApplicationPayload
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }

    public ApplicationStatus Status { get; set; } = ApplicationStatus.Unknown;

    public int? ProcessId { get; set; }
    public int RestartCount { get; set; }
    public bool RestartLimitReached { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public static class WebhookPayloadFactory
{
    public static WebhookPayload CreateEvent(
        WebhookEventType eventType,
        WebhookApplicationPayload application,
        DateTimeOffset? sentAt = null)
        => new()
        {
            Type = "event",
            Event = eventType,
            SentAt = sentAt ?? DateTimeOffset.UtcNow,
            MachineName = Environment.MachineName,
            Application = application,
            Applications = null
        };

    public static WebhookPayload CreateStatusReport(
        IEnumerable<WebhookApplicationPayload> applications,
        DateTimeOffset? sentAt = null)
        => new()
        {
            Type = "statusReport",
            Event = null,
            SentAt = sentAt ?? DateTimeOffset.UtcNow,
            MachineName = Environment.MachineName,
            Application = null,
            Applications = applications.ToList()
        };

    public static WebhookApplicationPayload FromStatus(
        WatchdogStatus status,
        bool enabled)
        => new()
        {
            Id = status.Id,
            Name = status.ApplicationName,
            Enabled = enabled,
            Status = status.Status,
            ProcessId = status.ProcessId,
            RestartCount = status.RestartCount,
            RestartLimitReached = status.RestartLimitReached,
            LastError = status.LastError,
            UpdatedAt = status.UpdatedAt
        };

    public static WebhookApplicationPayload FromConfig(
        MonitoredApplicationConfig app,
        WatchdogStatus? status)
    {
        if (status is not null)
            return FromStatus(status, app.Enabled);

        return new WebhookApplicationPayload
        {
            Id = app.Id,
            Name = app.Application.DisplayName,
            Enabled = app.Enabled,
            Status = ApplicationStatus.Unknown,
            ProcessId = null,
            RestartCount = 0,
            RestartLimitReached = false,
            LastError = null,
            UpdatedAt = default
        };
    }
}
