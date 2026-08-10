using KioskWatchdog.Core.Abstractions;
using KioskWatchdog.Core.Configuration;
using KioskWatchdog.Core.Engine;
using KioskWatchdog.Core.Health;
using KioskWatchdog.Core.Process;
using KioskWatchdog.Core.Restart;
using KioskWatchdog.Core.Status;
using Microsoft.Extensions.Logging.Abstractions;

namespace KioskWatchdog.Integration.Tests.Support;

internal sealed class EngineHarness : IAsyncDisposable
{
    private readonly string _configPath;
    private readonly CancellationTokenSource _cts = new();

    public WatchdogEngine Engine { get; }
    public IWatchdogStatusStore Status { get; }
    public WatchdogConfig Config { get; }
    public string ExecutablePath { get; }

    private EngineHarness(
        WatchdogEngine engine,
        IWatchdogStatusStore status,
        WatchdogConfig config,
        string executablePath,
        string configPath)
    {
        Engine = engine;
        Status = status;
        Config = config;
        ExecutablePath = executablePath;
        _configPath = configPath;
    }

    public static EngineHarness Create(
        string executablePath,
        string arguments,
        string? workingDirectory = null,
        Action<WatchdogConfig>? configure = null)
    {
        var configPath = Path.Combine(Path.GetTempPath(), "kw-int-" + Guid.NewGuid().ToString("N") + ".json");
        var config = new WatchdogConfig
        {
            Application =
            {
                ExecutablePath = executablePath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Path.GetDirectoryName(executablePath) ?? "",
                DisplayName = Path.GetFileNameWithoutExtension(executablePath)
            },
            Monitoring =
            {
                ProcessCheckIntervalSeconds = 1,
                HealthCheckIntervalSeconds = 1,
                HealthTimeoutSeconds = 3,
                GracefulTerminationTimeoutSeconds = 2
            },
            Restart =
            {
                RestartOnExit = true,
                RestartOnUnhealthy = true,
                RestartDelaySeconds = 1,
                MaxRestarts = 5,
                RestartWindowMinutes = 10
            },
            Health =
            {
                Enabled = false,
                Type = "http",
                Url = ""
            }
        };

        configure?.Invoke(config);

        var store = new JsonConfigStore(configPath);
        store.Save(config);

        var clock = new SystemClock();
        var processes = new SystemProcessManager();
        var terminator = new ProcessTerminator(processes, NullLogger<ProcessTerminator>.Instance);
        var health = new HttpHealthChecker(new HttpClient { Timeout = TimeSpan.FromSeconds(2) }, clock);
        var healthMonitor = new HealthMonitor(clock);
        var restart = new RestartManager(clock);
        var status = new WatchdogStatusStore();

        var engine = new WatchdogEngine(
            processes,
            terminator,
            health,
            healthMonitor,
            restart,
            status,
            store,
            clock,
            NullLogger<WatchdogEngine>.Instance,
            config);

        return new EngineHarness(engine, status, config, executablePath, configPath);
    }

    public Task StartEngineAsync() => Engine.StartAsync(_cts.Token);

    public async Task StopEngineAsync()
    {
        await Engine.StopAsync(CancellationToken.None);
        try
        {
            await Engine.StopApplicationAsync();
        }
        catch
        {
            // best effort cleanup
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopEngineAsync();
        }
        catch
        {
            // ignore
        }

        _cts.Cancel();
        _cts.Dispose();

        try
        {
            if (File.Exists(_configPath))
                File.Delete(_configPath);
        }
        catch
        {
            // ignore
        }
    }
}

internal static class WaitHelper
{
    public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string? message = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(100);
        }

        Assert.True(condition(), message ?? "Condition not met before timeout.");
    }
}

internal static class TestPaths
{
    public static string RequireTestAppExe()
    {
        var path = TryFindTestAppExe();
        if (path is null)
        {
            throw new FileNotFoundException(
                "KioskWatchdog.TestApp.exe was not found. Publish/build the TestApp for win-x64 first.");
        }

        return path;
    }

    public static string? TryFindTestAppExe()
    {
        var dir = AppContext.BaseDirectory;
        var candidates = new List<string>
        {
            Path.Combine(dir, "KioskWatchdog.TestApp.exe"),
            Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "artifacts", "testapp", "KioskWatchdog.TestApp.exe")),
            Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "src", "KioskWatchdog.TestApp", "bin", "Release", "net10.0", "win-x64", "KioskWatchdog.TestApp.exe")),
            Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "src", "KioskWatchdog.TestApp", "bin", "Release", "net10.0", "KioskWatchdog.TestApp.exe")),
            Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "..", "src", "KioskWatchdog.TestApp", "bin", "Debug", "net10.0", "KioskWatchdog.TestApp.exe")),
        };

        var env = Environment.GetEnvironmentVariable("KIOSK_WATCHDOG_TESTAPP");
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Insert(0, env);

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? TryFindElectronExe()
    {
        var root = FindRepoRoot();
        if (root is null)
            return null;

        var electronExe = Path.Combine(
            root,
            "fixtures",
            "electron-health-app",
            "node_modules",
            "electron",
            "dist",
            "electron.exe");

        return File.Exists(electronExe) ? electronExe : null;
    }

    public static string? TryFindElectronMain()
    {
        var root = FindRepoRoot();
        if (root is null)
            return null;

        var main = Path.Combine(root, "fixtures", "electron-health-app", "main.js");
        return File.Exists(main) ? main : null;
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KioskWatchdog.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }
}
