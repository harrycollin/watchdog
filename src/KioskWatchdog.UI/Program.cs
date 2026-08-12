using System.Runtime.Versioning;
using System.Windows;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Hosting;
using KioskWatchdog.Core.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (ShouldRunAsService(args))
        {
            RunService(args).GetAwaiter().GetResult();
            return;
        }

        if (!SingleInstanceGuard.TryEnter(out var singleInstance) || singleInstance is null)
            return;

        try
        {
            var app = new App();
            app.SingleInstance = singleInstance;
            app.InitializeComponent();
            app.Run();
        }
        finally
        {
            singleInstance.Dispose();
        }
    }

    private static bool ShouldRunAsService(string[] args)
        => args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));

    private static async Task RunService(string[] args)
    {
        var logDirectory = WatchdogConfig.DefaultLogsDirectory;
        WatchdogLogging.EnsureLogDirectory(logDirectory);

        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "KioskWatchdog";
        });

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
            options.SingleLine = true;
        });

        AddWindowsEventLog(builder.Logging);
        builder.Logging.AddProvider(new RollingFileLoggerProvider(logDirectory, "watchdog"));

        var configPath = args.SkipWhile(a => !string.Equals(a, "--config", StringComparison.OrdinalIgnoreCase))
                             .Skip(1)
                             .FirstOrDefault()
                         ?? WatchdogConfig.DefaultConfigPath;

        builder.Services.AddKioskWatchdogCore(configPath);

        var host = builder.Build();
        await host.RunAsync().ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static void AddWindowsEventLog(ILoggingBuilder logging)
    {
        logging.AddEventLog(settings =>
        {
            settings.SourceName = "KioskWatchdog";
            settings.LogName = "Application";
            settings.Filter = (_, level) => level >= LogLevel.Warning;
        });
    }
}
