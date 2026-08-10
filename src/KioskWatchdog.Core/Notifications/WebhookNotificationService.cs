using System.Threading.Channels;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Status;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Notifications;

/// <summary>
/// Observes status transitions and optionally posts periodic status reports.
/// HTTP I/O runs on a bounded background queue so monitoring is never blocked.
/// </summary>
public sealed class WebhookNotificationService : BackgroundService
{
    private const int QueueCapacity = 32;
    private static readonly TimeSpan InitialStatusReportDelay = TimeSpan.FromSeconds(30);

    private readonly IConfigStore _configStore;
    private readonly IWatchdogStatusStore _statusStore;
    private readonly IWebhookClient _webhookClient;
    private readonly ILogger<WebhookNotificationService> _logger;
    private readonly Channel<QueuedSend> _queue;
    private readonly Dictionary<string, ApplicationStatus> _lastStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private DateTimeOffset _nextStatusReportAt = DateTimeOffset.MaxValue;
    private int _lastStatusReportIntervalMinutes = -1;
    private bool _initialStatusReportScheduled;

    public WebhookNotificationService(
        IConfigStore configStore,
        IWatchdogStatusStore statusStore,
        IWebhookClient webhookClient,
        ILogger<WebhookNotificationService> logger)
    {
        _configStore = configStore;
        _statusStore = statusStore;
        _webhookClient = webhookClient;
        _logger = logger;
        _queue = Channel.CreateBounded<QueuedSend>(new BoundedChannelOptions(QueueCapacity)
        {
            // DropWrite: TryWrite returns false when full so we can log without blocking.
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Subscribe before the background loop starts so callers after StartAsync never race.
        _statusStore.Changed += OnStatusChanged;

        lock (_gate)
        {
            foreach (var status in _statusStore.All)
                _lastStatus[status.Id] = status.Status;
        }

        RefreshStatusReportSchedule(force: true);
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _statusStore.Changed -= OnStatusChanged;
        _queue.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var worker = DrainQueueAsync(stoppingToken);
        var ticker = StatusReportLoopAsync(stoppingToken);
        await Task.WhenAll(worker, ticker).ConfigureAwait(false);
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        try
        {
            var config = _configStore.Load();
            config.Normalize();
            var webhook = config.Notifications.Webhook;
            if (!webhook.Enabled || string.IsNullOrWhiteSpace(webhook.Url))
                return;

            List<(WatchdogStatus Status, ApplicationStatus? Previous)> transitions;
            lock (_gate)
            {
                transitions = new List<(WatchdogStatus, ApplicationStatus?)>();
                foreach (var status in _statusStore.All)
                {
                    _lastStatus.TryGetValue(status.Id, out var previous);
                    var hadPrevious = _lastStatus.ContainsKey(status.Id);
                    var prev = hadPrevious ? previous : (ApplicationStatus?)null;

                    if (!hadPrevious || previous != status.Status)
                        _lastStatus[status.Id] = status.Status;

                    if (hadPrevious && previous != status.Status)
                        transitions.Add((status.Clone(), prev));
                }
            }

            foreach (var (status, previous) in transitions)
            {
                var eventType = WebhookEventMapper.MapTransition(previous, status.Status);
                if (eventType is null)
                    continue;

                if (!webhook.Events.IsEnabled(eventType.Value))
                    continue;

                var enabled = config.FindApplication(status.Id)?.Enabled ?? true;
                var payload = WebhookPayloadFactory.CreateEvent(
                    eventType.Value,
                    WebhookPayloadFactory.FromStatus(status, enabled));

                Enqueue(new QueuedSend(webhook.Url.Trim(), payload, TimeSpan.FromSeconds(webhook.TimeoutSeconds)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed while evaluating webhook status transitions.");
        }
    }

    private async Task StatusReportLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RefreshStatusReportSchedule(force: false);

                var delay = _nextStatusReportAt - DateTimeOffset.UtcNow;
                if (delay < TimeSpan.Zero)
                    delay = TimeSpan.Zero;

                // Cap wait so config reloads are noticed promptly.
                if (delay > TimeSpan.FromSeconds(5))
                    delay = TimeSpan.FromSeconds(5);

                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);

                if (DateTimeOffset.UtcNow < _nextStatusReportAt)
                    continue;

                TryEnqueueStatusReport();
                ScheduleNextStatusReport();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook status report loop failed.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private void RefreshStatusReportSchedule(bool force)
    {
        var config = _configStore.Load();
        config.Normalize();
        var report = config.Notifications.Webhook.StatusReport;
        var webhook = config.Notifications.Webhook;

        if (!report.Enabled || string.IsNullOrWhiteSpace(webhook.Url))
        {
            _nextStatusReportAt = DateTimeOffset.MaxValue;
            _lastStatusReportIntervalMinutes = -1;
            _initialStatusReportScheduled = false;
            return;
        }

        if (force
            || !_initialStatusReportScheduled
            || _lastStatusReportIntervalMinutes != report.IntervalMinutes)
        {
            _lastStatusReportIntervalMinutes = report.IntervalMinutes;
            _initialStatusReportScheduled = true;
            _nextStatusReportAt = DateTimeOffset.UtcNow + InitialStatusReportDelay;
        }
    }

    private void ScheduleNextStatusReport()
    {
        var config = _configStore.Load();
        config.Normalize();
        var report = config.Notifications.Webhook.StatusReport;
        if (!report.Enabled)
        {
            _nextStatusReportAt = DateTimeOffset.MaxValue;
            return;
        }

        _nextStatusReportAt = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(report.IntervalMinutes);
        _lastStatusReportIntervalMinutes = report.IntervalMinutes;
    }

    /// <summary>Test hook: enqueue a status report if configured.</summary>
    internal void EnqueueStatusReportForTests() => TryEnqueueStatusReport();

    private void TryEnqueueStatusReport()
    {
        var config = _configStore.Load();
        config.Normalize();
        var webhook = config.Notifications.Webhook;
        if (!webhook.StatusReport.Enabled || string.IsNullOrWhiteSpace(webhook.Url))
            return;

        var apps = config.Applications
            .Select(app => WebhookPayloadFactory.FromConfig(app, _statusStore.Get(app.Id)))
            .ToList();

        var payload = WebhookPayloadFactory.CreateStatusReport(apps);
        Enqueue(new QueuedSend(webhook.Url.Trim(), payload, TimeSpan.FromSeconds(webhook.TimeoutSeconds)));
    }

    private void Enqueue(QueuedSend item)
    {
        if (_queue.Writer.TryWrite(item))
            return;

        _logger.LogWarning(
            "Webhook send queue full; dropping {Type} notification (capacity {Capacity}).",
            item.Payload.Type,
            QueueCapacity);
    }

    private async Task DrainQueueAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await _webhookClient
                        .PostAsync(item.Url, item.Payload, item.Timeout, stoppingToken)
                        .ConfigureAwait(false);

                    _logger.LogInformation(
                        "Webhook {Type} posted to {Url} (event={Event}).",
                        item.Payload.Type,
                        item.Url,
                        item.Payload.Event);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Webhook POST failed for {Type} to {Url}.",
                        item.Payload.Type,
                        item.Url);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    private sealed record QueuedSend(string Url, WebhookPayload Payload, TimeSpan Timeout);
}
