namespace KioskWatchdog.Core.Notifications;

public interface IWebhookClient
{
    Task PostAsync(string url, WebhookPayload payload, TimeSpan timeout, CancellationToken cancellationToken);
}
