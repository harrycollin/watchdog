using System.Text.Json;
using System.Text.Json.Serialization;
using KioskWatchdog.Core.Status;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Ipc;

public sealed class StatusFilePublisher : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IWatchdogStatusStore _statusStore;
    private readonly string _statusPath;
    private readonly ILogger<StatusFilePublisher>? _logger;
    private readonly object _gate = new();

    public StatusFilePublisher(
        IWatchdogStatusStore statusStore,
        string? statusPath = null,
        ILogger<StatusFilePublisher>? logger = null)
    {
        _statusStore = statusStore;
        _statusPath = statusPath ?? Path.Combine(
            Configuration.WatchdogConfig.DefaultConfigDirectory,
            "status.json");
        _logger = logger;
        _statusStore.Changed += OnChanged;
        Publish();
    }

    private void OnChanged(object? sender, EventArgs e) => Publish();

    public void Publish()
    {
        try
        {
            var directory = Path.GetDirectoryName(_statusPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var snapshot = _statusStore.CreateSnapshot();
            var json = JsonSerializer.Serialize(snapshot, Options);
            var temp = _statusPath + ".tmp";

            lock (_gate)
            {
                File.WriteAllText(temp, json);
                if (File.Exists(_statusPath))
                    File.Replace(temp, _statusPath, null);
                else
                    File.Move(temp, _statusPath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to publish status file.");
        }
    }

    public static WatchdogStatusSnapshot? ReadSnapshot(string? statusPath = null)
    {
        var path = statusPath ?? Path.Combine(
            Configuration.WatchdogConfig.DefaultConfigDirectory,
            "status.json");

        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<WatchdogStatusSnapshot>(json, Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Convenience for single-app / selected-app UI.</summary>
    public static WatchdogStatus? Read(string? statusPath = null, string? applicationId = null)
    {
        var snapshot = ReadSnapshot(statusPath);
        if (snapshot is null || snapshot.Applications.Count == 0)
            return null;

        if (string.IsNullOrWhiteSpace(applicationId))
            return snapshot.Applications[0];

        return snapshot.Applications.FirstOrDefault(a =>
            string.Equals(a.Id, applicationId, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        _statusStore.Changed -= OnChanged;
    }
}

public enum WatchdogCommandType
{
    Start,
    Stop,
    Restart,
    ResetRestartCounter,
    ReloadConfig
}

public sealed class WatchdogCommand
{
    public WatchdogCommandType Type { get; set; }
    public string? ApplicationId { get; set; }
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
}

public sealed class CommandFileQueue
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _commandPath;
    private readonly object _gate = new();

    public CommandFileQueue(string? commandPath = null)
    {
        _commandPath = commandPath ?? Path.Combine(
            Configuration.WatchdogConfig.DefaultConfigDirectory,
            "command.json");
    }

    public string CommandPath => _commandPath;

    public void Enqueue(WatchdogCommandType type, string? applicationId = null)
    {
        var directory = Path.GetDirectoryName(_commandPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var command = new WatchdogCommand
        {
            Type = type,
            ApplicationId = applicationId
        };
        var json = JsonSerializer.Serialize(command, Options);
        lock (_gate)
        {
            File.WriteAllText(_commandPath, json);
        }
    }

    public WatchdogCommand? TryDequeue()
    {
        lock (_gate)
        {
            if (!File.Exists(_commandPath))
                return null;

            try
            {
                var json = File.ReadAllText(_commandPath);
                File.Delete(_commandPath);
                return JsonSerializer.Deserialize<WatchdogCommand>(json, Options);
            }
            catch
            {
                try { File.Delete(_commandPath); } catch { /* ignore */ }
                return null;
            }
        }
    }
}
