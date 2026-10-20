using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using MicDeck.Audio;
using MicDeck.Configuration;
using MicDeck.Services;

namespace MicDeck.ViewModels;

public sealed record HotkeyBinding(string Label, string Gesture, Action Action, SoundItemViewModel? Owner = null);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly AudioDeviceInfo OffDevice =
        new("", "Off — do not capture", false, false, NAudio.CoreAudioApi.DataFlow.Capture);

    private readonly AppSettings _settings;
    private readonly AudioEngine _engine = new();
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _routingDebounce;

    private bool _loading = true;
    private bool _initialized;
    private string? _startupNotice;
    private bool _suppressDeviceEvents;
    private bool _seeking;
    private CancellationTokenSource? _waveformWarmup;
    private bool _faultThisConfigure;

    private AudioDeviceInfo? _selectedMonitor;
    private AudioDeviceInfo? _selectedInjection;
    private AudioDeviceInfo? _selectedMic;
    private AudioDeviceInfo? _selectedSystem;

    private double _monitorVolume;
    private double _injectionVolume;
    private double _micVolume;
    private double _systemVolume;

    private bool _micEnabled;
    private bool _systemEnabled;
    private bool _monitorClips;
    private bool _showAllDevices;

    private string _status = "Starting…";
    private bool _isPlaying;
    private string _nowPlayingName = "Nothing playing";
    private string? _nowPlayingPath;
    private double _durationSeconds;
    private double _seekSeconds;
    private string _bufferHealth = "";
    private double _micLevel;
    private double _systemLevel;
    private double _outputLevel;
    private bool _settingsOpen;
    private bool _cableReady;
    private bool _micLive;
    private bool _systemLive;
    private string? _appliedActivePath;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;

        Sounds = new ObservableCollection<SoundItemViewModel>();
        SoundsView = CollectionViewSource.GetDefaultView(Sounds);
        SoundsView.Filter = FilterSound;

        MonitorDevices = new ObservableCollection<AudioDeviceInfo>();
        InjectionDevices = new ObservableCollection<AudioDeviceInfo>();
        MicDevices = new ObservableCollection<AudioDeviceInfo>();
        SystemDevices = new ObservableCollection<AudioDeviceInfo>();

        _engine.Faulted += OnEngineFaulted;

        PlayCommand = new RelayCommand(PlaySelected, () => SelectedSound is not null);
        StopCommand = new RelayCommand(Stop, () => IsPlaying);
        ToggleSettingsCommand = new RelayCommand(() => IsSettingsOpen = !IsSettingsOpen);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);

        RestoreFromSettings();

        _tick = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _tick.Tick += OnTick;

        // Device rebuilds take a few hundred milliseconds; dragging the latency
        // slider must not reopen streams on every tick.
        _routingDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(650),
        };
        _routingDebounce.Tick += (_, _) =>
        {
            _routingDebounce.Stop();
            ApplyRouting();
        };

        LoadSounds();
        _tick.Start();

        if (!string.IsNullOrEmpty(settings.MigrationNotice))
        {
            _startupNotice = settings.MigrationNotice;
            settings.MigrationNotice = null;
            Status = _startupNotice!;
        }
    }

    /// <summary>
    /// Device enumeration and WASAPI stream creation take a few hundred
    /// milliseconds. Doing that before the window is shown made startup feel
    /// slow, so the window paints first and this runs right after.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        RefreshDevices();
        _loading = false;
        ApplyRouting();

        if (_startupNotice is not null)
        {
            Status = _startupNotice;
        }

        _ = WarmWaveformsAsync();
    }

    public ObservableCollection<SoundItemViewModel> Sounds { get; }

    public ICollectionView SoundsView { get; }

    public ObservableCollection<AudioDeviceInfo> MonitorDevices { get; }

    public ObservableCollection<AudioDeviceInfo> InjectionDevices { get; }

    public ObservableCollection<AudioDeviceInfo> MicDevices { get; }

    public ObservableCollection<AudioDeviceInfo> SystemDevices { get; }

    public RelayCommand PlayCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand ToggleSettingsCommand { get; }

    public RelayCommand ToggleThemeCommand { get; }

    public event Action? HotkeysChanged;

    // ── Devices ────────────────────────────────────────────────────────────

    public AudioDeviceInfo? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (Set(ref _selectedMonitor, value) && !_suppressDeviceEvents)
            {
                _settings.MonitorDeviceId = NullIfEmpty(value?.Id);
                Persist();
                ApplyRouting();
            }
        }
    }

    public AudioDeviceInfo? SelectedInjection
    {
        get => _selectedInjection;
        set
        {
            if (Set(ref _selectedInjection, value) && !_suppressDeviceEvents)
            {
                _settings.InjectionDeviceId = NullIfEmpty(value?.Id);
                Persist();
                ApplyRouting();
            }
        }
    }

    public AudioDeviceInfo? SelectedMic
    {
        get => _selectedMic;
        set
        {
            if (Set(ref _selectedMic, value) && !_suppressDeviceEvents)
            {
                _settings.MicDeviceId = NullIfEmpty(value?.Id);
                Persist();
                ApplyRouting();
            }
        }
    }

    public AudioDeviceInfo? SelectedSystem
    {
        get => _selectedSystem;
        set
        {
            if (Set(ref _selectedSystem, value) && !_suppressDeviceEvents)
            {
                _settings.SystemDeviceId = NullIfEmpty(value?.Id);
                Persist();
                ApplyRouting();
            }
        }
    }

    public bool ShowAllDevices
    {
        get => _showAllDevices;
        set
        {
            if (Set(ref _showAllDevices, value))
            {
                _settings.ShowAllDevices = value;
                Persist();
                RefreshDevices();
            }
        }
    }

    // ── Volumes ────────────────────────────────────────────────────────────

    public double MonitorVolume
    {
        get => _monitorVolume;
        set
        {
            if (!Set(ref _monitorVolume, Math.Clamp(value, 0, 150)))
            {
                return;
            }

            Raise(nameof(MonitorVolumeText));
            _settings.MonitorVolume = (int)value;
            PushVolumes();
            Persist();
        }
    }

    public string MonitorVolumeText => $"{(int)_monitorVolume}%";

    public double InjectionVolume
    {
        get => _injectionVolume;
        set
        {
            if (!Set(ref _injectionVolume, Math.Clamp(value, 0, 150)))
            {
                return;
            }

            Raise(nameof(InjectionVolumeText));
            _settings.InjectionVolume = (int)value;
            PushVolumes();
            Persist();
        }
    }

    public string InjectionVolumeText => $"{(int)_injectionVolume}%";

    public double MicVolume
    {
        get => _micVolume;
        set
        {
            if (!Set(ref _micVolume, Math.Clamp(value, 0, 150)))
            {
                return;
            }

            Raise(nameof(MicVolumeText));
            _settings.MicVolume = (int)value;
            PushVolumes();
            Persist();
        }
    }

    public string MicVolumeText => $"{(int)_micVolume}%";

    public double SystemVolume
    {
        get => _systemVolume;
        set
        {
            if (!Set(ref _systemVolume, Math.Clamp(value, 0, 150)))
            {
                return;
            }

            Raise(nameof(SystemVolumeText));
            _settings.SystemVolume = (int)value;
            PushVolumes();
            Persist();
        }
    }

    public string SystemVolumeText => $"{(int)_systemVolume}%";

    // ── Toggles ────────────────────────────────────────────────────────────

    public bool MicEnabled
    {
        get => _micEnabled;
        set
        {
            if (!Set(ref _micEnabled, value))
            {
                return;
            }

            _settings.MicEnabled = value;
            _engine.SetMicEnabled(value);
            Persist();
            Raise(nameof(MicStateText));
        }
    }

    public string MicStateText => MicEnabled ? "Live in meeting" : "Muted";

    public bool SystemEnabled
    {
        get => _systemEnabled;
        set
        {
            if (!Set(ref _systemEnabled, value))
            {
                return;
            }

            _settings.SystemEnabled = value;
            _engine.SetSystemEnabled(value);
            Persist();
        }
    }

    public bool MonitorClips
    {
        get => _monitorClips;
        set
        {
            if (!Set(ref _monitorClips, value))
            {
                return;
            }

            _settings.MonitorClips = value;
            _engine.SetMonitorClips(value);
            Persist();
        }
    }

    public int LatencyMs
    {
        get => _settings.LatencyMs;
        set
        {
            var clamped = Math.Clamp(value, 10, 120);
            if (_settings.LatencyMs == clamped)
            {
                return;
            }

            _settings.LatencyMs = clamped;
            Raise();
            Raise(nameof(LatencyText));
            Persist();
            _routingDebounce.Stop();
            _routingDebounce.Start();
        }
    }

    public string LatencyText => $"{_settings.LatencyMs} ms";

    /// <summary>
    /// Read from the assembly so the settings footer cannot drift out of sync
    /// with the csproj version.
    /// </summary>
    public string VersionText { get; } = BuildVersionText();

    private static string BuildVersionText()
    {
        var version = typeof(MainViewModel).Assembly.GetName().Version;
        var shortVersion = version is null ? "1.0" : $"{version.Major}.{version.Minor}";
        return $"MicDeck {shortVersion} · WASAPI shared mode";
    }

    // ── Theme / settings panel ─────────────────────────────────────────────

    public bool ThemeIsDark => !string.Equals(_settings.Theme, "light", StringComparison.OrdinalIgnoreCase);

    public bool IsSettingsOpen
    {
        get => _settingsOpen;
        set => Set(ref _settingsOpen, value);
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set
        {
            _settings.MinimizeToTray = value;
            Raise();
            Persist();
        }
    }

    public bool StartMinimized
    {
        get => _settings.StartMinimized;
        set
        {
            _settings.StartMinimized = value;
            Raise();
            Persist();
        }
    }

    public string HotkeyPlay
    {
        get => _settings.HotkeyPlay;
        set
        {
            _settings.HotkeyPlay = value ?? "";
            Raise();
            Persist();
            HotkeysChanged?.Invoke();
        }
    }

    public string HotkeyStop
    {
        get => _settings.HotkeyStop;
        set
        {
            _settings.HotkeyStop = value ?? "";
            Raise();
            Persist();
            HotkeysChanged?.Invoke();
        }
    }

    public string HotkeyToggleMic
    {
        get => _settings.HotkeyToggleMic;
        set
        {
            _settings.HotkeyToggleMic = value ?? "";
            Raise();
            Persist();
            HotkeysChanged?.Invoke();
        }
    }

    public void SetTheme(string theme)
    {
        _settings.Theme = theme;
        ThemeManager.Apply(theme, _settings.Accent);
        if (System.Windows.Application.Current.MainWindow is { } window)
        {
            ThemeManager.ApplyTitleBar(window);
        }

        Raise(nameof(ThemeIsDark));
        Persist();
    }

    public void SetAccent(string accent)
    {
        _settings.Accent = accent;
        ThemeManager.Apply(_settings.Theme, accent);
        Raise(nameof(AccentName));
        Persist();
    }

    public string AccentName => _settings.Accent;

    private void ToggleTheme() => SetTheme(ThemeIsDark ? "light" : "dark");

    // ── Library ────────────────────────────────────────────────────────────

    private SoundItemViewModel? _selectedSound;

    public SoundItemViewModel? SelectedSound
    {
        get => _selectedSound;
        set
        {
            if (!Set(ref _selectedSound, value))
            {
                return;
            }

            PlayCommand.RaiseCanExecuteChanged();
            if (value is not null && !IsPlaying)
            {
                SeekSeconds = 0;
                DurationSeconds = value.Duration?.TotalSeconds ?? 0;
                _ = EnsureWaveformAsync(value);
            }

            Raise(nameof(SelectedName));
        }
    }

    public string SelectedName => SelectedSound?.Name ?? "Nothing selected";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? ""))
            {
                SoundsView.Refresh();
                if (SelectedSound is { } current && !SoundsView.Contains(current))
                {
                    SelectedSound = SoundsView.Cast<SoundItemViewModel>().FirstOrDefault();
                }
            }
        }
    }

    private string _searchText = "";

    public bool IsLibraryEmpty => Sounds.Count == 0;

    public string LibraryCountText => Sounds.Count switch
    {
        0 => "No sounds",
        1 => "1 sound",
        _ => $"{Sounds.Count} sounds",
    };

    public void AddPaths(IEnumerable<string> paths, bool selectLast = true)
    {
        var added = new List<SoundItemViewModel>();
        var known = new HashSet<string>(Sounds.Select(s => s.Path), StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path) || !known.Add(path))
            {
                continue;
            }

            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var item = new SoundItemViewModel(name, path);
            Attach(item);
            Sounds.Add(item);
            _settings.Sounds.Add(new SoundDefinition { Name = name, Path = path });
            added.Add(item);
        }

        if (added.Count == 0)
        {
            Status = "Those files are already in the library.";
            return;
        }

        Persist();
        RaiseLibraryFlags();
        HotkeysChanged?.Invoke();

        foreach (var item in added)
        {
            _ = EnsureWaveformAsync(item);
        }

        _ = WarmWaveformsAsync();

        if (selectLast)
        {
            SelectedSound = added[^1];
        }

        Status = added.Count == 1
            ? $"Added {added[0].Name}"
            : $"Added {added.Count} sounds";
    }

    public void Remove(SoundItemViewModel item)
    {
        if (_nowPlayingPath == item.Path)
        {
            Stop();
        }

        Sounds.Remove(item);
        _settings.Sounds.RemoveAll(s => string.Equals(s.Path, item.Path, StringComparison.OrdinalIgnoreCase));
        _engine.Clips.Invalidate(item.Path);

        if (SelectedSound == item)
        {
            SelectedSound = SoundsView.Cast<SoundItemViewModel>().FirstOrDefault();
        }

        Persist();
        RaiseLibraryFlags();
        HotkeysChanged?.Invoke();
        Status = $"Removed {item.Name}";
    }

    public void Relink(SoundItemViewModel item, string newPath)
    {
        if (!System.IO.File.Exists(newPath))
        {
            return;
        }

        var duplicate = Sounds.Any(s => s != item &&
            string.Equals(s.Path, newPath, StringComparison.OrdinalIgnoreCase));

        if (duplicate)
        {
            Status = "That file is already in the library.";
            return;
        }

        var replacement = new SoundItemViewModel(item.Name, newPath, item.Gain, item.Hotkey);
        var index = Sounds.IndexOf(item);
        Sounds[index] = replacement;

        var definition = _settings.Sounds.FirstOrDefault(s =>
            string.Equals(s.Path, item.Path, StringComparison.OrdinalIgnoreCase));

        if (definition is not null)
        {
            definition.Path = newPath;
        }

        _engine.Clips.Invalidate(item.Path);
        Persist();
        SelectedSound = replacement;
        _ = EnsureWaveformAsync(replacement);
        Status = $"Relinked {replacement.Name}";
    }

    private void Attach(SoundItemViewModel item)
    {
        item.PropertyChanged += OnSoundPropertyChanged;
    }

    private void OnSoundPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SoundItemViewModel item)
        {
            return;
        }

        if (e.PropertyName is not (nameof(SoundItemViewModel.Name)
            or nameof(SoundItemViewModel.Gain)
            or nameof(SoundItemViewModel.Hotkey)))
        {
            return;
        }

        var definition = _settings.Sounds.FirstOrDefault(s =>
            string.Equals(s.Path, item.Path, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
        {
            return;
        }

        definition.Name = item.Name;
        definition.Gain = item.Gain;
        definition.Hotkey = item.Hotkey;
        Persist();

        if (e.PropertyName == nameof(SoundItemViewModel.Hotkey))
        {
            HotkeysChanged?.Invoke();
        }
    }

    private bool FilterSound(object item)
    {
        if (item is not SoundItemViewModel sound)
        {
            return false;
        }

        var needle = (_searchText ?? "").Trim();
        if (needle.Length == 0)
        {
            return true;
        }

        return sound.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)
            || sound.Path.Contains(needle, StringComparison.CurrentCultureIgnoreCase);
    }

    private async Task EnsureWaveformAsync(SoundItemViewModel item)
    {
        if (item.Waveform is not null)
        {
            return;
        }

        try
        {
            var clip = await _engine.Clips.GetAsync(item.Path);
            item.Waveform = clip.Waveform;
            item.Duration ??= clip.Duration;
        }
        catch
        {
            // Waveform is decorative; ignore decode failures here.
        }
    }

    private async Task ProbeDurationsAsync()
    {
        foreach (var item in Sounds.ToList())
        {
            if (item.Duration is not null)
            {
                continue;
            }

            var duration = await _engine.Clips.ProbeDurationAsync(item.Path);
            if (duration is not null)
            {
                item.Duration = duration;
            }
        }
    }

    private void LoadSounds()
    {
        foreach (var definition in _settings.Sounds)
        {
            var item = new SoundItemViewModel(
                string.IsNullOrWhiteSpace(definition.Name)
                    ? System.IO.Path.GetFileNameWithoutExtension(definition.Path)
                    : definition.Name,
                definition.Path,
                definition.Gain,
                definition.Hotkey);

            if (_engine.Clips.CachedDuration(definition.Path) is { } cached)
            {
                item.Duration = cached;
            }

            Attach(item);
            Sounds.Add(item);
        }

        RaiseLibraryFlags();
        _ = ProbeDurationsAsync();
    }

    /// <summary>
    /// Decodes the library in the background so every row shows its waveform
    /// without the user having to select it first. Decoded clips also stay in the
    /// LRU, which makes the first press of each sound instant.
    /// </summary>
    private async Task WarmWaveformsAsync()
    {
        _waveformWarmup?.Cancel();
        var cts = new CancellationTokenSource();
        _waveformWarmup = cts;

        foreach (var item in Sounds.ToList())
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (item.Waveform is not null || item.IsMissing)
            {
                continue;
            }

            await EnsureWaveformAsync(item);

            try
            {
                await Task.Delay(15, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void RaiseLibraryFlags()
    {
        Raise(nameof(IsLibraryEmpty), nameof(LibraryCountText));
    }

    // ── Playback ───────────────────────────────────────────────────────────

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value))
            {
                return;
            }

            StopCommand.RaiseCanExecuteChanged();
            Raise(nameof(PlayPauseGlyph));
        }
    }

    public string PlayPauseGlyph => IsPlaying ? "❚❚" : "▶";

    public string NowPlayingName
    {
        get => _nowPlayingName;
        private set => Set(ref _nowPlayingName, value);
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        private set
        {
            if (Set(ref _durationSeconds, value))
            {
                Raise(nameof(PositionText), nameof(ProgressFraction));
            }
        }
    }

    public string PositionText =>
        $"{SoundItemViewModel.FormatDuration(TimeSpan.FromSeconds(_seekSeconds))} / " +
        $"{SoundItemViewModel.FormatDuration(TimeSpan.FromSeconds(_durationSeconds))}";

    public double ProgressFraction => _durationSeconds <= 0
        ? 0
        : Math.Clamp(_seekSeconds / _durationSeconds, 0, 1);

    /// <summary>
    /// Single source of truth for the transport position; bound to the seek
    /// slider. The meter timer writes it while playing, the user writes it while
    /// dragging, and <see cref="IsTickUpdate"/> tells the view which is which.
    /// </summary>
    public double SeekSeconds
    {
        get => _seekSeconds;
        set
        {
            if (Set(ref _seekSeconds, Math.Clamp(value, 0, Math.Max(0, _durationSeconds))))
            {
                Raise(nameof(PositionText), nameof(ProgressFraction));
            }
        }
    }

    /// <summary>True while the playback timer is pushing the position into the view.</summary>
    public bool IsTickUpdate { get; private set; }

    public string? NowPlayingPath => _nowPlayingPath;

    public void PlaySelected()
    {
        if (SelectedSound is null)
        {
            Status = "Select a sound first.";
            return;
        }

        _ = PlayItemAsync(SelectedSound);
    }

    public async Task PlayItemAsync(SoundItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsMissing)
        {
            Status = "File not found — use Relink to point MicDeck at it.";
            return;
        }

        if (!_engine.IsConfigured)
        {
            Status = "Choose an injection output device first.";
            return;
        }

        try
        {
            var clip = await _engine.Clips.GetAsync(item.Path);
            item.Waveform ??= clip.Waveform;
            item.Duration ??= clip.Duration;

            var offset = TimeSpan.FromSeconds(
                Math.Clamp(_seekSeconds, 0, Math.Max(0, clip.Duration.TotalSeconds - 0.05)));

            _engine.Play(clip, offset, item.Gain / 100f);

            _nowPlayingPath = item.Path;
            NowPlayingName = item.Name;
            DurationSeconds = clip.Duration.TotalSeconds;
            SeekSeconds = offset.TotalSeconds;
            SelectedSound ??= item;
            Status = $"Playing {item.Name}";
            Raise(nameof(NowPlayingPath));
        }
        catch (Exception ex)
        {
            Status = $"Cannot play {item.Name}: {ex.Message}";
        }
    }

    public void Stop()
    {
        _engine.StopClip();
        _engine.PruneFinished();
        IsPlaying = false;
        SeekSeconds = 0;
        if (_nowPlayingPath is not null)
        {
            Status = "Stopped";
        }
    }

    public void BeginSeek() => _seeking = true;

    public void CommitSeek(double seconds)
    {
        _seeking = false;
        SeekSeconds = seconds;

        if (!_engine.IsPlaying || _engine.CurrentClip is not { } clip)
        {
            return;
        }

        _engine.Play(clip, TimeSpan.FromSeconds(seconds));
    }

    public void TogglePlayPause()
    {
        if (IsPlaying)
        {
            Stop();
        }
        else
        {
            PlaySelected();
        }
    }

    public void PlayItem(SoundItemViewModel item)
    {
        SeekSeconds = 0;
        SelectedSound = item;
        _ = PlayItemAsync(item);
    }

    // ── Hotkeys ────────────────────────────────────────────────────────────

    public IReadOnlyList<HotkeyBinding> GetHotkeyBindings()
    {
        var bindings = new List<HotkeyBinding>
        {
            new("Play selected", _settings.HotkeyPlay, PlaySelected),
            new("Stop playback", _settings.HotkeyStop, Stop),
            new("Toggle microphone", _settings.HotkeyToggleMic, () => MicEnabled = !MicEnabled),
        };

        foreach (var item in Sounds)
        {
            if (!string.IsNullOrWhiteSpace(item.Hotkey))
            {
                bindings.Add(new HotkeyBinding(item.Name, item.Hotkey!, () => PlayItem(item), item));
            }
        }

        return bindings;
    }

    public void ReportHotkeyConflict(string label, string gesture, SoundItemViewModel? owner)
    {
        if (owner is not null)
        {
            owner.HotkeyError = $"{gesture} is already taken by another app.";
        }

        Status = $"Hotkey {gesture} could not be registered ({label}).";
    }

    /// <summary>Called before re-registering, so fixed conflicts clear their badge.</summary>
    public void ClearHotkeyErrors()
    {
        foreach (var sound in Sounds)
        {
            sound.HotkeyError = null;
        }
    }

    // ── Engine ─────────────────────────────────────────────────────────────

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public double MicLevel
    {
        get => _micLevel;
        private set => Set(ref _micLevel, value);
    }

    public double SystemLevel
    {
        get => _systemLevel;
        private set => Set(ref _systemLevel, value);
    }

    public double OutputLevel
    {
        get => _outputLevel;
        private set => Set(ref _outputLevel, value);
    }

    public bool CableReady
    {
        get => _cableReady;
        private set => Set(ref _cableReady, value);
    }

    public bool MicLive
    {
        get => _micLive;
        private set => Set(ref _micLive, value);
    }

    public bool SystemLive
    {
        get => _systemLive;
        private set => Set(ref _systemLive, value);
    }

    public string BufferHealthText => _bufferHealth;

    private void RestoreFromSettings()
    {
        _monitorVolume = _settings.MonitorVolume;
        _injectionVolume = _settings.InjectionVolume;
        _micVolume = _settings.MicVolume;
        _systemVolume = _settings.SystemVolume;
        _micEnabled = _settings.MicEnabled;
        _systemEnabled = _settings.SystemEnabled;
        _monitorClips = _settings.MonitorClips;
        _showAllDevices = _settings.ShowAllDevices;

        _engine.SetVolumes(
            (float)_monitorVolume / 100f,
            (float)_injectionVolume / 100f,
            (float)_micVolume / 100f,
            (float)_systemVolume / 100f);
        _engine.SetMicEnabled(_micEnabled);
        _engine.SetSystemEnabled(_systemEnabled);
        _engine.SetMonitorClips(_monitorClips);
    }

    private void PushVolumes()
    {
        _engine.SetVolumes(
            (float)_monitorVolume / 100f,
            (float)_injectionVolume / 100f,
            (float)_micVolume / 100f,
            (float)_systemVolume / 100f);
    }

    public void RefreshDevices()
    {
        var render = DeviceCatalog.RenderDevices();
        var capture = DeviceCatalog.CaptureDevices();

        var before = (SelectedMonitor?.Id, SelectedInjection?.Id, SelectedMic?.Id, SelectedSystem?.Id);

        var monitorItems = render
            .Where(d => _settings.ShowAllDevices || !d.IsVirtualCable)
            .ToList();

        Fill(
            MonitorDevices,
            monitorItems,
            _settings.MonitorDeviceId,
            monitorItems.FirstOrDefault(d => d.IsDefault) ?? monitorItems.FirstOrDefault(),
            value => _selectedMonitor = value);

        Fill(
            InjectionDevices,
            render,
            _settings.InjectionDeviceId,
            FindInjectionFallback(render),
            value => _selectedInjection = value);

        Fill(
            MicDevices,
            capture.Where(d => !d.IsVirtualCable),
            _settings.MicDeviceId,
            capture.FirstOrDefault(d => d.IsDefault) ?? capture.FirstOrDefault(),
            value => _selectedMic = value);

        Fill(
            SystemDevices,
            new[] { OffDevice }.Concat(DeviceCatalog.LoopbackSources()),
            _settings.SystemDeviceId,
            OffDevice,
            value => _selectedSystem = value);

        Raise(nameof(SelectedMonitor), nameof(SelectedInjection), nameof(SelectedMic), nameof(SelectedSystem));

        // A device that vanished was replaced by a fallback; persist it so the
        // next launch does not chase the stale endpoint id again.
        if (before != (SelectedMonitor?.Id, SelectedInjection?.Id, SelectedMic?.Id, SelectedSystem?.Id))
        {
            _settings.MonitorDeviceId = NullIfEmpty(SelectedMonitor?.Id);
            _settings.InjectionDeviceId = NullIfEmpty(SelectedInjection?.Id);
            _settings.MicDeviceId = NullIfEmpty(SelectedMic?.Id);
            _settings.SystemDeviceId = NullIfEmpty(SelectedSystem?.Id);
            Persist();
            ApplyRouting();
        }
    }

    private static AudioDeviceInfo? FindInjectionFallback(IReadOnlyList<AudioDeviceInfo> render)
    {
        var cable = DeviceCatalog.GuessInjectionDevice(render);
        if (cable is not null)
        {
            return render.FirstOrDefault(d => d.Id == cable);
        }

        return render.FirstOrDefault();
    }

    private void Fill(
        ObservableCollection<AudioDeviceInfo> target,
        IEnumerable<AudioDeviceInfo> items,
        string? selectedId,
        AudioDeviceInfo? fallback,
        Action<AudioDeviceInfo?> assign)
    {
        _suppressDeviceEvents = true;
        try
        {
            target.Clear();
            foreach (var item in items)
            {
                target.Add(item);
            }

            var match = target.FirstOrDefault(d =>
                !string.IsNullOrEmpty(selectedId) &&
                string.Equals(d.Id, selectedId, StringComparison.OrdinalIgnoreCase));

            assign(match ?? fallback ?? target.FirstOrDefault());
        }
        finally
        {
            _suppressDeviceEvents = false;
        }
    }

    private void ApplyRouting()
    {
        if (_loading)
        {
            return;
        }

        _faultThisConfigure = false;

        try
        {
            _engine.Configure(new AudioRoute(
                NullIfEmpty(SelectedMonitor?.Id),
                NullIfEmpty(SelectedInjection?.Id),
                NullIfEmpty(SelectedMic?.Id),
                NullIfEmpty(SelectedSystem?.Id),
                _settings.LatencyMs));
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }

        CableReady = _engine.IsConfigured && SelectedInjection is { IsVirtualCable: true };
        MicLive = _engine.MicCapturing;
        SystemLive = _engine.SystemCapturing;

        if (_engine.IsConfigured && !_faultThisConfigure)
        {
            Status = SelectedInjection is { IsVirtualCable: false }
                ? "No virtual cable detected — pick a CABLE Input device to send audio to the call."
                : "Ready";
        }
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private void OnEngineFaulted(string message)
    {
        _faultThisConfigure = true;

        void Apply()
        {
            Status = message;
            CableReady = _engine.IsConfigured && SelectedInjection is { IsVirtualCable: true };
            MicLive = _engine.MicCapturing;
            SystemLive = _engine.SystemCapturing;
        }

        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            Apply();
        }
        else
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(Apply);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _engine.PruneFinished();

        MicLevel = _engine.MicLevel;
        SystemLevel = _engine.SystemLevel;
        OutputLevel = _engine.OutputLevel;
        var playing = _engine.IsPlaying;

        if (playing && !_seeking)
        {
            IsTickUpdate = true;
            SeekSeconds = _engine.ClipPosition.TotalSeconds;
            IsTickUpdate = false;
        }

        if (playing != IsPlaying)
        {
            IsPlaying = playing;

            if (!playing)
            {
                SeekSeconds = _durationSeconds;
                ApplyActivePath(null);
                Status = "Ready";
            }
            else
            {
                ApplyActivePath(_nowPlayingPath);
            }
        }

        var micBuffer = _engine.MicBufferMilliseconds;
        var health = micBuffer > 0 ? $"Mic buffer {micBuffer:F0} ms" : "Audio idle";

        if (health != _bufferHealth)
        {
            _bufferHealth = health;
            Raise(nameof(BufferHealthText));
        }
    }

    private void ApplyActivePath(string? path)
    {
        if (_appliedActivePath == path)
        {
            return;
        }

        foreach (var item in Sounds)
        {
            var shouldBePlaying = string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase);
            if (item.IsPlaying != shouldBePlaying)
            {
                item.IsPlaying = shouldBePlaying;
            }
        }

        _appliedActivePath = path;
    }

    private void Persist() => SettingsStore.Save(_settings);

    public void Dispose()
    {
        _waveformWarmup?.Cancel();
        _tick.Stop();
        _tick.Tick -= OnTick;
        _routingDebounce.Stop();
        _engine.Faulted -= OnEngineFaulted;
        _engine.Dispose();
        SettingsStore.SaveNow();
    }
}
