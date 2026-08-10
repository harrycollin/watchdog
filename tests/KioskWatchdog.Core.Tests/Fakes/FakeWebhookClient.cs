using KioskWatchdog.Core.Notifications;

namespace KioskWatchdog.Core.Tests.Fakes;

internal sealed class FakeWebhookClient : IWebhookClient
{
    private readonly List<WebhookPost> _posts = new();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _hangGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Hang { get; set; }
    public Exception? ThrowOnPost { get; set; }

    public IReadOnlyList<WebhookPost> Posts
    {
        get
        {
            lock (_gate)
                return _posts.ToList();
        }
    }

    public void ReleaseHang() => _hangGate.TrySetResult();

    public async Task PostAsync(
        string url,
        WebhookPayload payload,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (Hang)
        {
            await _hangGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (ThrowOnPost is not null)
            throw ThrowOnPost;

        lock (_gate)
        {
            _posts.Add(new WebhookPost(url, payload, timeout));
        }
    }

    public sealed record WebhookPost(string Url, WebhookPayload Payload, TimeSpan Timeout);
}
