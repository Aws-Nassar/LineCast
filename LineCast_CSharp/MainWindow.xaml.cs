using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LineCast;

public partial class MainWindow : Window
{
    // ── State ────────────────────────────────────────────────────────────────
    private AppConfig _config;
    private readonly AudioHandler _audio = new();

    private readonly ObservableCollection<SoundEntry> _sounds = new();
    private List<SoundEntry> _filteredSounds = new();

    private readonly DispatcherTimer _reviewTimer;
    private double _currentDurationSec;
    private double _playbackStartOffsetSec;
    private DateTime _playbackStartedAt;
    private bool _isReviewSeeking;
    private bool _ignoreNextStopped;
    private string? _currentSoundPath;

    private List<AudioDeviceInfo> _outputDevices  = new();
    private List<AudioDeviceInfo> _inputDevices   = new();
    private List<AudioDeviceInfo> _loopbackDevices = new();

    // ── Init ─────────────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();

        _config = AppConfig.Load();
        _reviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _reviewTimer.Tick += OnReviewTimerTick;

        SoundTable.ItemsSource = _sounds;

        ApplyConfigToUI();
        LoadDevices();
        LoadSoundTable();
        ApplySavedVolumes();

        Dispatcher.BeginInvoke(EnableStartupMicPassthrough, DispatcherPriority.ApplicationIdle);
    }

    private void ApplyConfigToUI()
    {
        MonitorSlider.Value       = _config.MonitorVolume;
        InjectionSlider.Value     = _config.InjectionVolume;
        MicSlider.Value           = _config.MicVolume;
        ExternalAudioSlider.Value = _config.ExternalAudioVolume;

        MicPassthroughCheck.IsChecked  = _config.MicPassthroughEnabled;
        ExternalAudioCheck.IsChecked   = _config.ExternalAudioEnabled;
        AdvancedDevicesCheck.IsChecked = _config.ShowAdvancedDevices;

        _audio.SetMicPassthroughEnabled(_config.MicPassthroughEnabled);
        _audio.SetExternalAudioEnabled(_config.ExternalAudioEnabled);
    }

    // ── Device loading ───────────────────────────────────────────────────────

    private void LoadDevices()
    {
        try
        {
            _outputDevices   = AudioHandler.ListOutputDevices();
            _inputDevices    = AudioHandler.ListInputDevices();
            _loopbackDevices = AudioHandler.ListLoopbackDevices();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Audio Devices", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        PopulateCombo(MonitorCombo,       _outputDevices,   "monitor",   _config.MonitorDevice);
        PopulateCombo(InjectionCombo,     _outputDevices,   "injection", _config.InjectionDevice);
        PopulateCombo(MicCombo,           _inputDevices,    "mic",       _config.MicDevice);
        PopulateCombo(ExternalAudioCombo, _loopbackDevices, "external",  _config.ExternalAudioDevice);
    }

    private void PopulateCombo(ComboBox combo, List<AudioDeviceInfo> devices,
                                string role, string? selectedId)
    {
        combo.SelectionChanged -= OnDeviceSelectionChanged;
        combo.Items.Clear();
        combo.Items.Add(new AudioDeviceInfo("", $"Select {RolePlaceholder(role)}", "", false, false));

        var shown = _config.ShowAdvancedDevices ? devices : RecommendedDevices(devices, role);
        foreach (var d in shown)
            combo.Items.Add(d);

        SelectComboById(combo, selectedId);
        combo.SelectionChanged += OnDeviceSelectionChanged;
    }

    private static string RolePlaceholder(string role) => role switch
    {
        "monitor"   => "headphones/speakers",
        "injection" => "virtual cable input",
        "mic"       => "your real microphone",
        "external"  => "output device to capture",
        _           => "device"
    };

    private static List<AudioDeviceInfo> RecommendedDevices(List<AudioDeviceInfo> all, string role)
    {
        return all.Where(d =>
        {
            var name = d.Name.ToLowerInvariant();
            return role switch
            {
                "monitor"   => (name.Contains("headphones") || name.Contains("speakers") || name.Contains("headset"))
                               && !IsVirtualAudio(name),
                "mic"       => (name.Contains("microphone") || name.Contains("mic") || name.Contains("headset"))
                               && !IsVirtualAudio(name) && !name.Contains("stereo mix"),
                "injection" => IsVirtualCableInput(name),
                "external"  => d.HostApi.Contains("Loopback") || name.Contains("stereo mix"),
                _           => true
            };
        }).ToList();
    }

    private static bool IsVirtualAudio(string lower)
        => lower.Contains("vb-audio") || lower.Contains("cable input") || lower.Contains("cable output");

    private static bool IsVirtualCableInput(string lower)
        => lower.Contains("cable input") ||
           (lower.Contains("vb-audio virtual cable") && !lower.Contains("cable output"));

    private void SelectComboById(ComboBox combo, string? id)
    {
        if (string.IsNullOrEmpty(id)) { combo.SelectedIndex = 0; return; }
        foreach (AudioDeviceInfo item in combo.Items)
        {
            if (item.Id == id) { combo.SelectedItem = item; return; }
        }
        combo.SelectedIndex = 0;
    }

    // ── Sound table ──────────────────────────────────────────────────────────

    private void LoadSoundTable()
    {
        _sounds.Clear();
        foreach (var s in _config.Sounds)
            _sounds.Add(s);
        FilterTable(SearchBox.Text);
    }

    private void FilterTable(string query)
    {
        var needle = query.Trim().ToLowerInvariant();
        _filteredSounds = string.IsNullOrEmpty(needle)
            ? _sounds.ToList()
            : _sounds.Where(s =>
                s.Name.ToLowerInvariant().Contains(needle) ||
                s.Path.ToLowerInvariant().Contains(needle)).ToList();

        SoundTable.ItemsSource = _filteredSounds;
    }

    private SoundEntry? SelectedEntry() => SoundTable.SelectedItem as SoundEntry;

    // ── Volume ───────────────────────────────────────────────────────────────

    private void ApplySavedVolumes()
    {
        MonitorSlider.Value       = _config.MonitorVolume;
        InjectionSlider.Value     = _config.InjectionVolume;
        MicSlider.Value           = _config.MicVolume;
        ExternalAudioSlider.Value = _config.ExternalAudioVolume;
        UpdateVolumeLabels();
        ApplyVolumesToAudio();
    }

    private void UpdateVolumeLabels()
    {
        MonitorValueLabel.Text       = $"{(int)MonitorSlider.Value}%";
        InjectionValueLabel.Text     = $"{(int)InjectionSlider.Value}%";
        MicValueLabel.Text           = $"{(int)MicSlider.Value}%";
        ExternalAudioValueLabel.Text = $"{(int)ExternalAudioSlider.Value}%";
    }

    private void ApplyVolumesToAudio()
    {
        _audio.SetVolumes(
            monitor:       (float)MonitorSlider.Value / 100f,
            injection:     (float)InjectionSlider.Value / 100f,
            mic:           (float)MicSlider.Value / 100f,
            externalAudio: (float)ExternalAudioSlider.Value / 100f);
    }

    // ── Event handlers ───────────────────────────────────────────────────────

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _config.MonitorVolume       = (int)MonitorSlider.Value;
        _config.InjectionVolume     = (int)InjectionSlider.Value;
        _config.MicVolume           = (int)MicSlider.Value;
        _config.ExternalAudioVolume = (int)ExternalAudioSlider.Value;
        UpdateVolumeLabels();
        ApplyVolumesToAudio();
        _config.Save();
    }

    private void OnDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _config.MonitorDevice       = (MonitorCombo.SelectedItem as AudioDeviceInfo)?.Id;
        _config.InjectionDevice     = (InjectionCombo.SelectedItem as AudioDeviceInfo)?.Id;
        _config.MicDevice           = (MicCombo.SelectedItem as AudioDeviceInfo)?.Id;
        _config.ExternalAudioDevice = (ExternalAudioCombo.SelectedItem as AudioDeviceInfo)?.Id;

        _audio.MonitorDeviceId       = _config.MonitorDevice;
        _audio.InjectionDeviceId     = _config.InjectionDevice;
        _audio.MicDeviceId           = _config.MicDevice;
        _audio.ExternalAudioDeviceId = _config.ExternalAudioDevice;

        _config.Save();
        ApplyMicPassthrough(showErrors: false);
    }

    private void OnMicPassthroughChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = MicPassthroughCheck.IsChecked == true;
        _config.MicPassthroughEnabled = enabled;
        _audio.SetMicPassthroughEnabled(enabled);
        _config.Save();
        ApplyMicPassthrough(showErrors: enabled);
    }

    private void OnExternalAudioChanged(object sender, RoutedEventArgs e)
    {
        bool enabled = ExternalAudioCheck.IsChecked == true;
        _config.ExternalAudioEnabled = enabled;
        _audio.SetExternalAudioEnabled(enabled);
        _config.Save();
        ApplyMicPassthrough(showErrors: enabled);
    }

    private void OnAdvancedDevicesChanged(object sender, RoutedEventArgs e)
    {
        _config.ShowAdvancedDevices = AdvancedDevicesCheck.IsChecked == true;
        _config.Save();
        LoadDevices();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
        => FilterTable(SearchBox.Text);

    private void OnAddSounds(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title            = "Add sound files",
            Filter           = "Audio files (*.mp3;*.wav)|*.mp3;*.wav|All files (*.*)|*.*",
            Multiselect      = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dlg.ShowDialog() != true) return;

        var existing = _config.Sounds.Select(s => s.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int added = 0;
        foreach (var file in dlg.FileNames)
        {
            if (existing.Contains(file)) continue;
            var entry = new SoundEntry { Name = Path.GetFileNameWithoutExtension(file), Path = file };
            _config.Sounds.Add(entry);
            _sounds.Add(entry);
            existing.Add(file);
            added++;
        }
        _config.Save();
        FilterTable(SearchBox.Text);
        StatusLabel.Text = $"Added {added} sound file(s)";
    }

    private void OnRenameSound(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntry();
        if (entry == null) { MessageBox.Show("Select a sound first.", "Rename Sound"); return; }

        var dialog = new InputDialog("Rename Sound", "Sound name:", entry.Name) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Result)) return;

        entry.Name = dialog.Result.Trim();
        _config.Save();
        FilterTable(SearchBox.Text);
        StatusLabel.Text = $"Renamed: {entry.Name}";
    }

    private void OnChangePath(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntry();
        if (entry == null) { MessageBox.Show("Select a sound first.", "Change Path"); return; }

        var dlg = new OpenFileDialog
        {
            Title            = "Choose replacement sound file",
            Filter           = "Audio files (*.mp3;*.wav)|*.mp3;*.wav|All files (*.*)|*.*",
            InitialDirectory = File.Exists(entry.Path)
                ? Path.GetDirectoryName(entry.Path)
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dlg.ShowDialog() != true) return;

        if (_config.Sounds.Any(s => s != entry &&
            string.Equals(s.Path, dlg.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("That sound file is already in the library.", "Change Path",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        entry.Path = dlg.FileName;
        _config.Save();
        FilterTable(SearchBox.Text);
        StatusLabel.Text = $"Path updated: {Path.GetFileName(dlg.FileName)}";
    }

    private void OnDeleteSound(object sender, RoutedEventArgs e)
    {
        var entry = SelectedEntry();
        if (entry == null) { MessageBox.Show("Select a sound first.", "Delete Sound"); return; }

        var result = MessageBox.Show(
            $"Remove '{entry.Name}' from the library?\n\nThe audio file itself will not be deleted.",
            "Delete Sound", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (result != MessageBoxResult.Yes) return;

        if (_currentSoundPath == entry.Path) { StopPlayback(); ResetReviewSlider(); }
        _config.Sounds.Remove(entry);
        _sounds.Remove(entry);
        _config.Save();
        FilterTable(SearchBox.Text);
        StatusLabel.Text = $"Deleted: {entry.Name}";
    }

    private void OnPlay(object sender, RoutedEventArgs e) => PlaySelected();
    private void OnStop(object sender, RoutedEventArgs e) => StopPlayback();
    private void OnTableDoubleClick(object sender, MouseButtonEventArgs e) => PlaySelected();

    private void OnTableSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_audio.IsPlaying) return;
        var entry = SelectedEntry();
        if (entry == null) { ResetReviewSlider(); return; }

        try
        {
            double dur         = AudioHandler.GetFileDurationSeconds(entry.Path);
            _currentSoundPath  = entry.Path;
            _currentDurationSec = dur;
            ReviewSlider.Maximum   = dur;
            ReviewSlider.IsEnabled = dur > 0;
            SetReviewPosition(0);
        }
        catch { ResetReviewSlider(); }
    }

    // ── Playback ─────────────────────────────────────────────────────────────

    private void PlaySelected()
    {
        var entry = SelectedEntry();
        if (entry == null) { StatusLabel.Text = "Add or select a sound first"; return; }

        double startSec = 0;
        if (_currentSoundPath == entry.Path)
        {
            startSec = ReviewSlider.Value;
            if (_currentDurationSec > 0 && startSec >= _currentDurationSec)
                startSec = 0;
        }

        PlayPath(entry.Path, startSec);
    }

    private void PlayPath(string path, double startSec = 0)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show($"File not found:\n{path}", "Missing File",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(_config.MonitorDevice) ||
            string.IsNullOrEmpty(_config.InjectionDevice))
        {
            MessageBox.Show("Select both monitor and injection devices before playing.",
                            "Audio Routing", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            _audio.MonitorDeviceId       = _config.MonitorDevice;
            _audio.InjectionDeviceId     = _config.InjectionDevice;
            _audio.MicDeviceId           = _config.MicDevice;
            _audio.ExternalAudioDeviceId = _config.ExternalAudioDevice;

            double dur = AudioHandler.GetFileDurationSeconds(path);
            startSec = Math.Max(0, Math.Min(startSec, Math.Max(0, dur - 0.001)));

            _audio.PlayFile(path, (float)startSec, (status, err) =>
            {
                Dispatcher.Invoke(() => OnPlaybackFinished(status, err));
            });

            StartReviewProgress(path, dur, startSec);
            StatusLabel.Text = $"Playing: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Playback Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StopPlayback()
    {
        SetReviewPosition(CurrentReviewPositionSec());
        _reviewTimer.Stop();
        _ignoreNextStopped = _audio.IsPlaying;
        _audio.Stop();
        StatusLabel.Text = "Stopped";
    }

    private void OnPlaybackFinished(string status, Exception? error)
    {
        if (status == "stopped" && _ignoreNextStopped) { _ignoreNextStopped = false; return; }
        _reviewTimer.Stop();
        _ignoreNextStopped = false;

        if (error != null) { StatusLabel.Text = $"Playback {status}: {error.Message}"; return; }
        if (status == "finished" && _currentDurationSec > 0)
            SetReviewPosition(_currentDurationSec);
        StatusLabel.Text = $"Playback {status}";
    }

    // ── Review slider ─────────────────────────────────────────────────────────

    private void StartReviewProgress(string path, double durationSec, double startSec)
    {
        _currentSoundPath       = path;
        _currentDurationSec     = durationSec;
        _playbackStartOffsetSec = startSec;
        _playbackStartedAt      = DateTime.UtcNow;
        ReviewSlider.Maximum    = Math.Max(0, durationSec);
        ReviewSlider.IsEnabled  = durationSec > 0;
        SetReviewPosition(startSec);
        _reviewTimer.Start();
    }

    private void OnReviewTimerTick(object? sender, EventArgs e)
    {
        if (_isReviewSeeking) return;
        SetReviewPosition(CurrentReviewPositionSec());
        if (_currentDurationSec > 0 && CurrentReviewPositionSec() >= _currentDurationSec)
            _reviewTimer.Stop();
    }

    private double CurrentReviewPositionSec()
    {
        if (_currentDurationSec == 0) return 0;
        if (!_reviewTimer.IsEnabled)
            return Math.Min(ReviewSlider.Value, _currentDurationSec);
        double elapsed = (DateTime.UtcNow - _playbackStartedAt).TotalSeconds;
        return Math.Min(_playbackStartOffsetSec + elapsed, _currentDurationSec);
    }

    private void OnReviewSliderPressed(object sender, MouseButtonEventArgs e)
        => _isReviewSeeking = true;

    private void OnReviewSliderReleased(object sender, MouseButtonEventArgs e)
    {
        _isReviewSeeking = false;
        if (_currentSoundPath == null) return;

        double target = ReviewSlider.Value;
        SetReviewPosition(target);
        if (_audio.IsPlaying)
        {
            _ignoreNextStopped = true;
            _audio.Stop();
            PlayPath(_currentSoundPath, target);
        }
    }

    private void SetReviewPosition(double sec)
    {
        sec = Math.Clamp(sec, 0, _currentDurationSec);
        ReviewSlider.Value   = sec;
        ReviewTimeLabel.Text = $"{FormatDuration(sec)} / {FormatDuration(_currentDurationSec)}";
    }

    private void ResetReviewSlider()
    {
        _currentSoundPath       = null;
        _currentDurationSec     = 0;
        _playbackStartOffsetSec = 0;
        _reviewTimer.Stop();
        ReviewSlider.IsEnabled = false;
        ReviewSlider.Maximum   = 0;
        ReviewSlider.Value     = 0;
        ReviewTimeLabel.Text   = "00:00 / 00:00";
    }

    private static string FormatDuration(double sec)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, sec));
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    // ── Mic passthrough helpers ───────────────────────────────────────────────

    private bool _deferMicStart = true;

    private void EnableStartupMicPassthrough()
    {
        _deferMicStart = false;
        ApplyMicPassthrough(showErrors: false);
    }

    private void ApplyMicPassthrough(bool showErrors)
    {
        if (_deferMicStart && !showErrors) return;

        bool micEnabled      = MicPassthroughCheck.IsChecked == true;
        bool externalEnabled = ExternalAudioCheck.IsChecked  == true;

        if (!micEnabled && !externalEnabled)
        {
            _audio.StopMicPassthrough();
            if (StatusLabel.Text is "Mic mix active" or "Video mix active" or "Mic + video mix active")
                StatusLabel.Text = "Ready";
            return;
        }

        if (string.IsNullOrEmpty(_config.InjectionDevice))
        {
            if (showErrors) MessageBox.Show("Select an Injection Device first.", "Audio Routing");
            return;
        }
        if (micEnabled && string.IsNullOrEmpty(_config.MicDevice))
        {
            if (showErrors) MessageBox.Show("Select your real microphone as the Mic Input first.", "Mic Mixing");
            return;
        }

        try
        {
            _audio.StartMicPassthrough();
            string label = (micEnabled, externalEnabled) switch
            {
                (true, true)  => "Mic + video mix active",
                (false, true) => "Video mix active",
                _             => "Mic mix active"
            };
            if (StatusLabel.Text is "Ready" or "Stopped"
                or "Mic mix active" or "Video mix active" or "Mic + video mix active")
                StatusLabel.Text = label;
        }
        catch (Exception ex)
        {
            _audio.StopMicPassthrough();
            if (showErrors)
                MessageBox.Show(ex.Message, "Audio Routing Error",
                                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Window closing ────────────────────────────────────────────────────────

    protected override void OnClosed(EventArgs e)
    {
        _reviewTimer.Stop();
        _audio.Stop();
        _audio.StopMicPassthrough();
        _config.Save();
        _audio.Dispose();
        base.OnClosed(e);
    }
}
