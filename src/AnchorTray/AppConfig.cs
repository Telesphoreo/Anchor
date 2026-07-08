using System.Text.Json;
using System.Text.Json.Serialization;
using SolixBle;

namespace Anchor;

/// <summary>
/// Application configuration persisted as JSON at %APPDATA%\Anchor\config.json.
/// Loads defaults when the file is missing or corrupt; saves atomically.
/// </summary>
public sealed class AppConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    /// <summary>MAC address of the power station ("AA:BB:CC:DD:EE:FF"); empty = unconfigured.</summary>
    public string DeviceMac { get; set; } = "";

    /// <summary>Name of the <see cref="SolixDeviceModel"/> to monitor.</summary>
    public string DeviceModel { get; set; } = "C1000";

    /// <summary>Battery percentage at (or below) which the shutdown countdown arms.</summary>
    public int BatteryFloorPercent { get; set; } = 15;

    /// <summary>Consecutive low-battery samples required before starting the countdown.</summary>
    public int DebounceSamples { get; set; } = 3;

    /// <summary>Length of the shutdown countdown in seconds.</summary>
    public int CountdownSeconds { get; set; } = 60;

    /// <summary>When true, log and notify instead of actually shutting Windows down.</summary>
    public bool DryRun { get; set; }

    /// <summary>True when a device MAC has been configured.</summary>
    [JsonIgnore]
    public bool Configured => !string.IsNullOrWhiteSpace(DeviceMac);

    /// <summary>Directory holding the config file and logs.</summary>
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Anchor");

    private static string ConfigPath => Path.Combine(ConfigDirectory, "config.json");

    /// <summary>True when a saved configuration file already exists (i.e. not a first run).</summary>
    public static bool Exists => File.Exists(ConfigPath);

    /// <summary>
    /// Parse <see cref="DeviceModel"/> into a factory model, defaulting to C1000
    /// when the value is unknown (or is Generic, which has no UPS telemetry surface).
    /// </summary>
    public SolixDeviceModel ParseModel() =>
        Enum.TryParse<SolixDeviceModel>(DeviceModel, ignoreCase: true, out var model)
        && model != SolixDeviceModel.Generic
            ? model
            : SolixDeviceModel.C1000;

    /// <summary>Load the configuration, falling back to defaults on a missing or corrupt file.</summary>
    public static AppConfig Load(FileLog log)
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions);
                if (config is not null)
                {
                    config.Normalize();
                    return config;
                }

                log.Warning($"Config file '{ConfigPath}' was empty; using defaults.");
            }
        }
        catch (Exception ex)
        {
            log.Warning($"Could not read config '{ConfigPath}' ({ex.Message}); using defaults.");
        }

        return new AppConfig();
    }

    /// <summary>Save atomically: write a temp file, then move it over the config file.</summary>
    public void Save(FileLog log)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var tempPath = ConfigPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tempPath, ConfigPath, overwrite: true);
            log.Info($"Saved configuration to '{ConfigPath}'.");
        }
        catch (Exception ex)
        {
            log.Error($"Failed to save config '{ConfigPath}': {ex.Message}");
        }
    }

    private void Normalize()
    {
        DeviceMac = string.IsNullOrWhiteSpace(DeviceMac) ? "" : DeviceMac.Trim();
        DeviceModel = ParseModel().ToString();
        BatteryFloorPercent = Math.Clamp(BatteryFloorPercent, 1, 95);
        DebounceSamples = Math.Clamp(DebounceSamples, 1, 10);
        CountdownSeconds = Math.Clamp(CountdownSeconds, 0, 600);
    }
}
