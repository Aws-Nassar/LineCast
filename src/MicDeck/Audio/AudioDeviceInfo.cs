using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicDeck.Audio;

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    bool IsDefault,
    bool IsVirtualCable,
    DataFlow Flow)
{
    public bool IsInput => Flow == DataFlow.Capture;

    /// <summary>Human label used in the device pickers.</summary>
    public string Label => IsDefault ? $"{Name}   ·   default" : Name;

    public override string ToString() => Label;
}

/// <summary>
/// Enumerates WASAPI endpoints only. The old app exposed MME, DirectSound and
/// WDM-KS duplicates, which both confused users and crashed playback (a
/// DirectSound GUID cannot be opened through MMDeviceEnumerator).
/// </summary>
public static class DeviceCatalog
{
    public static IReadOnlyList<AudioDeviceInfo> RenderDevices()
        => Enumerate(DataFlow.Render);

    public static IReadOnlyList<AudioDeviceInfo> CaptureDevices()
        => Enumerate(DataFlow.Capture);

    /// <summary>Render endpoints usable as a loopback capture source.</summary>
    public static IReadOnlyList<AudioDeviceInfo> LoopbackSources()
    {
        var rendered = Enumerate(DataFlow.Render);
        return rendered
            .Where(d => !d.IsVirtualCable)
            .Select(d => d with { Flow = DataFlow.Capture })
            .ToList();
    }

    private static List<AudioDeviceInfo> Enumerate(DataFlow flow)
    {
        var result = new List<AudioDeviceInfo>();

        using var enumerator = new MMDeviceEnumerator();
        var defaultId = SafeDefaultId(enumerator, flow);

        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            try
            {
                var name = BuildName(device);
                result.Add(new AudioDeviceInfo(
                    device.ID,
                    name,
                    device.ID == defaultId,
                    IsCable(name),
                    flow));
            }
            finally
            {
                device.Dispose();
            }
        }

        return result
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.IsVirtualCable)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string SafeDefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
            return device.ID;
        }
        catch
        {
            return "";
        }
    }

    private static string BuildName(MMDevice device)
    {
        string name;
        try
        {
            name = device.DeviceFriendlyName;
        }
        catch
        {
            name = device.FriendlyName;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = device.FriendlyName;
        }

        // WASAPI frequently reports "Speakers (Realtek(R) Audio)" plus a driver
        // suffix; collapse whitespace so labels are stable lengths.
        return System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();
    }

    public static bool IsCable(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.Contains("cable input")
            || lower.Contains("cable output")
            || lower.Contains("vb-audio")
            || lower.Contains("voicemeeter");
    }

    /// <summary>Open an endpoint by stable id. The caller owns the returned device.</summary>
    public static MMDevice Open(string id)
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(id);
    }

    /// <summary>Best guess at the virtual cable's playback endpoint (the sink we inject into).</summary>
    public static string? GuessInjectionDevice(IEnumerable<AudioDeviceInfo> render)
    {
        return render
            .Where(d => !d.Name.Contains("16ch", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(d =>
                d.Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
            ?.Id;
    }
}
