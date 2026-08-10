using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Engine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Ipc;

public sealed class CommandListenerService : BackgroundService
{
    private readonly CommandFileQueue _queue;
    private readonly WatchdogEngine _engine;
    private readonly IConfigStore _configStore;
    private readonly ILogger<CommandListenerService> _logger;

    public CommandListenerService(
        CommandFileQueue queue,
        WatchdogEngine engine,
        IConfigStore configStore,
        ILogger<CommandListenerService> logger)
    {
        _queue = queue;
        _engine = engine;
        _configStore = configStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var command = _queue.TryDequeue();
                if (command is not null)
                {
                    _logger.LogInformation(
                        "Received command {Type} for app '{AppId}' ({Id}).",
                        command.Type,
                        command.ApplicationId ?? "(default)",
                        command.Id);
                    await DispatchAsync(command, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing control command.");
            }

            try
            {
                await Task.Delay(500, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task DispatchAsync(WatchdogCommand command, CancellationToken cancellationToken)
    {
        switch (command.Type)
        {
            case WatchdogCommandType.Start:
                await _engine.StartApplicationAsync(command.ApplicationId, cancellationToken).ConfigureAwait(false);
                break;
            case WatchdogCommandType.Stop:
                await _engine.StopApplicationAsync(command.ApplicationId, cancellationToken).ConfigureAwait(false);
                break;
            case WatchdogCommandType.Restart:
                await _engine.RestartApplicationAsync(command.ApplicationId, cancellationToken).ConfigureAwait(false);
                break;
            case WatchdogCommandType.ResetRestartCounter:
                _engine.ResetRestartCounter(command.ApplicationId);
                break;
            case WatchdogCommandType.ReloadConfig:
                _engine.ReloadConfiguration(_configStore.Load());
                break;
        }
    }
}
