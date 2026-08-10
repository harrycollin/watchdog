using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Status;

namespace KioskWatchdog.Core.Notifications;

public static class WebhookEventMapper
{
    private static readonly HashSet<ApplicationStatus> RecoverableFrom =
    [
        ApplicationStatus.Unhealthy,
        ApplicationStatus.Restarting,
        ApplicationStatus.Error,
        ApplicationStatus.RestartLimitReached
    ];

    /// <summary>
    /// Maps a status transition to at most one webhook event. Returns null when nothing should fire.
    /// </summary>
    public static WebhookEventType? MapTransition(ApplicationStatus? previous, ApplicationStatus current)
    {
        if (previous == current)
            return null;

        return current switch
        {
            ApplicationStatus.RestartLimitReached => WebhookEventType.RestartLimitReached,
            ApplicationStatus.Error => WebhookEventType.Error,
            ApplicationStatus.Restarting => WebhookEventType.Restart,
            ApplicationStatus.Unhealthy => WebhookEventType.Unhealthy,
            ApplicationStatus.Running when previous is { } prev && RecoverableFrom.Contains(prev)
                => WebhookEventType.Recovered,
            _ => null
        };
    }
}
