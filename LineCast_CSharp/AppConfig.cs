using System.IO;
using Newtonsoft.Json;

namespace LineCast;

/// <summary>
/// Persisted user configuration. Mirrors config.json from the Python version.
/// </summary>
public class AppConfig
{
    [JsonProperty("monitor_device")]
    public string? MonitorDevice { get; set; }

    [JsonProperty("injection_device")]
    public string? InjectionDevice { get; set; }

    [JsonProperty("mic_device")]
    public string? MicDevice { get; set; }

    [JsonProperty("external_audio_device")]
    public string? ExternalAudioDevice { get; set; }

    [JsonProperty("monitor_volume")]
    public int MonitorVolume { get; set; } = 85;

    [JsonProperty("injection_volume")]
    public int InjectionVolume { get; set; } = 85;

    [JsonProperty("mic_volume")]
    public int MicVolume { get; set; } = 85;

    [JsonProperty("external_audio_volume")]
    public int ExternalAudioVolume { get; set; } = 85;

    [JsonProperty("mic_passthrough_enabled")]
    public bool MicPassthroughEnabled { get; set; } = true;

    [JsonProperty("external_audio_enabled")]
    public bool ExternalAudioEnabled { get; set; } = false;

    [JsonProperty("show_advanced_devices")]
    public bool ShowAdvancedDevices { get; set; } = false;

    [JsonProperty("sounds")]
    public List<SoundEntry> Sounds { get; set; } = new();

    // ── Persistence ──────────────────────────────────────────────────────────

    private static string ConfigPath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LineCast",
            "config.json");

    public static AppConfig Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path))
            return new AppConfig();

        try
        {
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save()
    {
        var path = ConfigPath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
    }
}

public class SoundEntry
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;
}
