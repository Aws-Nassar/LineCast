using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace MicDeck.Configuration;

public sealed class SoundDefinition
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>Playback gain in percent. 100 = unity, 200 = +6 dB.</summary>
    public int Gain { get; set; } = 100;

    /// <summary>Global hotkey gesture such as "Ctrl+Alt+1" or "F5". Empty = none.</summary>
    public string? Hotkey { get; set; }
}

public sealed class AppSettings
{
    public int Version { get; set; } = 2;

    // Stable WASAPI endpoint ids. Index-based PortAudio ids were the old bug:
    // they change whenever a device is plugged in or removed.
    public string? MonitorDeviceId { get; set; }
    public string? InjectionDeviceId { get; set; }
    public string? MicDeviceId { get; set; }
    public string? SystemDeviceId { get; set; }

    public int MonitorVolume { get; set; } = 85;
    public int InjectionVolume { get; set; } = 85;
    public int MicVolume { get; set; } = 95;
    public int SystemVolume { get; set; } = 70;

    public bool MicEnabled { get; set; } = true;
    public bool SystemEnabled { get; set; }
    public bool MonitorClips { get; set; } = true;

    public bool ShowAllDevices { get; set; }

    public string Theme { get; set; } = "dark";
    public string Accent { get; set; } = "cyan";

    public string HotkeyPlay { get; set; } = "F8";
    public string HotkeyStop { get; set; } = "F9";
    public string HotkeyToggleMic { get; set; } = "F10";

    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; }

    /// <summary>Requested WASAPI shared-mode buffer, in milliseconds.</summary>
    public int LatencyMs { get; set; } = 30;

    public List<SoundDefinition> Sounds { get; set; } = new();

    [JsonIgnore]
    public string? MigrationNotice { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static AppSettings _current = new();
    private static DispatcherTimer? _debounce;

    public static string Directory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MicDeck");

    public static string Path => System.IO.Path.Combine(Directory, "settings.json");

    private static string PreviousDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LineCast");

    private static string PreviousPath => System.IO.Path.Combine(PreviousDirectory, "settings.json");

    private static string LegacyPath
    {
        get
        {
            var local = System.IO.Path.Combine(Directory, "config.json");
            return File.Exists(local) ? local : System.IO.Path.Combine(PreviousDirectory, "config.json");
        }
    }

    public static AppSettings Load()
    {
        AdoptPreviousSettings();

        if (File.Exists(Path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), Options);
                if (loaded is not null)
                {
                    _current = loaded;
                    return _current;
                }
            }
            catch (Exception ex)
            {
                // A corrupt file must not silently wipe the library. Keep a copy
                // and fall through to migration instead of starting empty.
                TryBackupCorruptFile(ex);
            }
        }

        _current = MigrateFromV1();
        return _current;
    }

    /// <summary>Debounced save: sliders fire dozens of changes per second.</summary>
    public static void Save(AppSettings settings)
    {
        _current = settings;

        if (_debounce is null)
        {
            _debounce = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(600),
            };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                SaveNow();
            };
        }

        _debounce.Stop();
        _debounce.Start();
    }

    public static void SaveNow()
    {
        _debounce?.Stop();
        WriteAtomic(_current);
    }

    private static void WriteAtomic(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = JsonSerializer.Serialize(settings, Options);
            var temp = Path + ".tmp";

            // Write to a sibling file first, then swap. A crash mid-write used to
            // leave truncated JSON, which the loader treated as "no config".
            File.WriteAllText(temp, json);
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    private static void TryBackupCorruptFile(Exception ex)
    {
        try
        {
            CrashLog.Write(ex);
            var backup = Path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(Path, backup, overwrite: true);
        }
        catch
        {
            // Best effort only.
        }
    }

    /// <summary>
    /// The app used to be called LineCast. Copy its settings over once so a
    /// rename does not look like a wiped library on first launch.
    /// </summary>
    private static void AdoptPreviousSettings()
    {
        if (File.Exists(Path) || !File.Exists(PreviousPath))
        {
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.Copy(PreviousPath, Path);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }
    }

    /// <summary>
    /// Import the sound list from the Python app's config.json. Device indexes
    /// cannot be mapped to endpoint ids, so those are intentionally dropped.
    /// </summary>
    private static AppSettings MigrateFromV1()
    {
        var settings = new AppSettings();

        if (!File.Exists(LegacyPath))
        {
            return settings;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(LegacyPath));
            if (!document.RootElement.TryGetProperty("sounds", out var sounds) ||
                sounds.ValueKind != JsonValueKind.Array)
            {
                return settings;
            }

            foreach (var entry in sounds.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    var path = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        settings.Sounds.Add(new SoundDefinition
                        {
                            Name = System.IO.Path.GetFileNameWithoutExtension(path),
                            Path = path,
                        });
                    }
                }
                else if (entry.ValueKind == JsonValueKind.Object)
                {
                    var path = entry.TryGetProperty("path", out var p) ? p.GetString() : null;
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    var name = entry.TryGetProperty("name", out var n) ? n.GetString() : null;
                    settings.Sounds.Add(new SoundDefinition
                    {
                        Name = string.IsNullOrWhiteSpace(name)
                            ? System.IO.Path.GetFileNameWithoutExtension(path)
                            : name,
                        Path = path,
                    });
                }
            }

            if (settings.Sounds.Count > 0)
            {
                settings.MigrationNotice =
                    $"Imported {settings.Sounds.Count} sounds from the previous version. " +
                    "Re-select your audio devices once.";
            }

            WriteAtomic(settings);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex);
        }

        return settings;
    }
}
