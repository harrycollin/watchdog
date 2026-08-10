using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Notifications;
using KioskWatchdog.Core.Status;

namespace KioskWatchdog.Core.Tests.Notifications;

public class WebhookEventMapperTests
{
    [Theory]
    [InlineData(ApplicationStatus.Running, ApplicationStatus.RestartLimitReached, WebhookEventType.RestartLimitReached)]
    [InlineData(ApplicationStatus.Starting, ApplicationStatus.Error, WebhookEventType.Error)]
    [InlineData(ApplicationStatus.Running, ApplicationStatus.Restarting, WebhookEventType.Restart)]
    [InlineData(ApplicationStatus.Running, ApplicationStatus.Unhealthy, WebhookEventType.Unhealthy)]
    [InlineData(ApplicationStatus.Unhealthy, ApplicationStatus.Running, WebhookEventType.Recovered)]
    [InlineData(ApplicationStatus.Restarting, ApplicationStatus.Running, WebhookEventType.Recovered)]
    [InlineData(ApplicationStatus.Error, ApplicationStatus.Running, WebhookEventType.Recovered)]
    [InlineData(ApplicationStatus.RestartLimitReached, ApplicationStatus.Running, WebhookEventType.Recovered)]
    public void Maps_transitions(ApplicationStatus previous, ApplicationStatus current, WebhookEventType expected)
    {
        Assert.Equal(expected, WebhookEventMapper.MapTransition(previous, current));
    }

    [Theory]
    [InlineData(ApplicationStatus.Running, ApplicationStatus.Running)]
    [InlineData(ApplicationStatus.Stopped, ApplicationStatus.Starting)]
    [InlineData(ApplicationStatus.Starting, ApplicationStatus.Running)]
    public void Ignores_noisy_or_same_transitions(ApplicationStatus previous, ApplicationStatus current)
    {
        Assert.Null(WebhookEventMapper.MapTransition(previous, current));
    }

    [Fact]
    public void Ignores_transition_from_unknown_previous()
    {
        Assert.Null(WebhookEventMapper.MapTransition(null, ApplicationStatus.Running));
    }
}
