using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Engine;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Hosting;

internal sealed class ConfigReloadService : BackgroundService
{
    private readonly IConfigStore _configStore;
    private readonly WatchdogEngine _engine;
    private readonly ILogger<ConfigReloadService> _logger;
    private DateTime _lastWriteTime = DateTime.MinValue;

    public ConfigReloadService(
        IConfigStore configStore,
        WatchdogEngine engine,
        ILogger<ConfigReloadService> logger)
    {
        _configStore = configStore;
        _engine = engine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(_configStore.ConfigPath))
                {
                    var writeTime = File.GetLastWriteTimeUtc(_configStore.ConfigPath);
                    if (writeTime > _lastWriteTime)
                    {
                        if (_lastWriteTime != DateTime.MinValue)
                        {
                            var config = _configStore.Load();
                            _engine.ReloadConfiguration(config);
                            _logger.LogInformation("Configuration reloaded from disk.");
                        }

                        _lastWriteTime = writeTime;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while watching configuration.");
            }

            try
            {
                await Task.Delay(1000, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
