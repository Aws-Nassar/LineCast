namespace LineCast.ViewModels;

public sealed class SoundItemViewModel : ObservableObject
{
    private string _name;
    private int _gain;
    private string? _hotkey;
    private TimeSpan? _duration;
    private float[]? _waveform;
    private bool _isPlaying;
    private string? _hotkeyError;

    public SoundItemViewModel(string name, string path, int gain = 100, string? hotkey = null)
    {
        _name = name;
        Path = path;
        _gain = gain;
        _hotkey = hotkey;
    }

    public string Path { get; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                Raise(nameof(Tooltip));
            }
        }
    }

    public int Gain
    {
        get => _gain;
        set
        {
            if (Set(ref _gain, Math.Clamp(value, 0, 200)))
            {
                Raise(nameof(GainText));
            }
        }
    }

    public string GainText => $"{Gain}%";

    public string? Hotkey
    {
        get => _hotkey;
        set
        {
            if (Set(ref _hotkey, value))
            {
                Raise(nameof(HotkeyText), nameof(HasHotkey));
            }
        }
    }

    public string HotkeyText => string.IsNullOrWhiteSpace(Hotkey) ? "—" : Hotkey!;

    public bool HasHotkey => !string.IsNullOrWhiteSpace(Hotkey);

    public string? HotkeyError
    {
        get => _hotkeyError;
        set
        {
            if (Set(ref _hotkeyError, value))
            {
                Raise(nameof(HasHotkeyError));
            }
        }
    }

    public bool HasHotkeyError => !string.IsNullOrEmpty(HotkeyError);

    public TimeSpan? Duration
    {
        get => _duration;
        set
        {
            if (Set(ref _duration, value))
            {
                Raise(nameof(DurationText));
            }
        }
    }

    public string DurationText => Duration is null
        ? "—"
        : FormatDuration(Duration.Value);

    public float[]? Waveform
    {
        get => _waveform;
        set => Set(ref _waveform, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    public bool IsMissing => !System.IO.File.Exists(Path);

    public string Tooltip => Path;

    public static string FormatDuration(TimeSpan value)
    {
        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}";
        }

        return $"{value.Minutes}:{value.Seconds:00}";
    }
}
