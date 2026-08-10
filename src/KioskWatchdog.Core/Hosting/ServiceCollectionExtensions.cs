using KioskWatchdog.Core.Abstractions;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Engine;
using KioskWatchdog.Core.Health;
using KioskWatchdog.Core.Ipc;
using KioskWatchdog.Core.Notifications;
using KioskWatchdog.Core.Process;
using KioskWatchdog.Core.Status;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Hosting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddKioskWatchdogCore(
        this IServiceCollection services,
        string? configPath = null)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IConfigStore>(sp =>
            new JsonConfigStore(
                configPath,
                sp.GetService<ILogger<JsonConfigStore>>()));

        services.AddSingleton<IWatchdogStatusStore, WatchdogStatusStore>();
        services.AddSingleton<IProcessManager, SystemProcessManager>();
        services.AddSingleton<IProcessResourceSampler, SystemProcessResourceSampler>();
        services.AddSingleton<ProcessTerminator>();
        services.AddSingleton<CommandFileQueue>();
        services.AddSingleton<StatusFilePublisher>();

        services.AddHttpClient<IHealthChecker, HttpHealthChecker>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
        });

        // Per-request timeout is applied in HttpWebhookClient; keep HttpClient timeout high.
        services.AddHttpClient<IWebhookClient, HttpWebhookClient>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(2);
        });

        services.AddSingleton(sp =>
        {
            var store = sp.GetRequiredService<IConfigStore>();
            var config = store.Load();
            config.Normalize();
            return config;
        });

        services.AddSingleton<WatchdogEngine>();
        services.AddHostedService(sp => sp.GetRequiredService<WatchdogEngine>());
        services.AddHostedService<CommandListenerService>();
        services.AddHostedService<ConfigReloadService>();
        services.AddHostedService<StatusPublisherHostedService>();
        services.AddHostedService<WebhookNotificationService>();

        return services;
    }
}

internal sealed class StatusPublisherHostedService : IHostedService
{
    private readonly StatusFilePublisher _publisher;

    public StatusPublisherHostedService(StatusFilePublisher publisher)
    {
        _publisher = publisher;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _publisher.Dispose();
        return Task.CompletedTask;
    }
}
