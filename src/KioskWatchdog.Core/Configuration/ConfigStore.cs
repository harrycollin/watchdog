using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace KioskWatchdog.Core.Configuration;

public interface IConfigStore
{
    WatchdogConfig Load();
    void Save(WatchdogConfig config);
    string ConfigPath { get; }
}

public sealed class JsonConfigStore : IConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ILogger<JsonConfigStore>? _logger;

    public string ConfigPath { get; }

    public JsonConfigStore(string? configPath = null, ILogger<JsonConfigStore>? logger = null)
    {
        ConfigPath = configPath ?? WatchdogConfig.DefaultConfigPath;
        _logger = logger;
    }

    public WatchdogConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            _logger?.LogInformation("Configuration file not found at {Path}; using defaults.", ConfigPath);
            var defaults = WatchdogConfig.CreateDefault();
            defaults.Normalize();
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var config = JsonSerializer.Deserialize<WatchdogConfig>(json, SerializerOptions)
                         ?? WatchdogConfig.CreateDefault();
            config.Normalize();

            _logger?.LogInformation(
                "Configuration loaded from {Path} ({Count} application(s)).",
                ConfigPath,
                config.Applications.Count);
            return config;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load configuration from {Path}; using defaults.", ConfigPath);
            var defaults = WatchdogConfig.CreateDefault();
            defaults.Normalize();
            return defaults;
        }
    }

    public void Save(WatchdogConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Normalize();

        var validation = ConfigValidator.Validate(config);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                "Cannot save invalid configuration: " + string.Join("; ", validation.Errors));
        }

        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var json = JsonSerializer.Serialize(config, SerializerOptions);
        var tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);

        if (File.Exists(ConfigPath))
            File.Replace(tempPath, ConfigPath, null);
        else
            File.Move(tempPath, ConfigPath);

        _logger?.LogInformation("Configuration saved to {Path}.", ConfigPath);
    }
}
