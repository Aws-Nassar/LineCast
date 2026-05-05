using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LineCast;

/// <summary>
/// Audio device descriptor — equivalent to the Python AudioDevice dataclass.
/// </summary>
public record AudioDeviceInfo(
    string Id,
    string Name,
    string HostApi,
    bool IsInput,
    bool IsOutput);

/// <summary>
/// Callback signature for playback completion.
/// </summary>
public delegate void PlaybackFinishedCallback(string status, Exception? error);

/// <summary>
/// Core audio engine. Mirrors AudioHandler from audio_handler.py.
/// Uses NAudio (WasapiOut / WasapiCapture) instead of sounddevice/pydub.
/// </summary>
public sealed class AudioHandler : IDisposable
{
    // ── Configuration ────────────────────────────────────────────────────────
    public string? MonitorDeviceId { get; set; }
    public string? InjectionDeviceId { get; set; }
    public string? MicDeviceId { get; set; }
    public string? ExternalAudioDeviceId { get; set; }

    public float MonitorVolume { get; set; } = 0.85f;
    public float InjectionVolume { get; set; } = 0.85f;
    public float MicVolume { get; set; } = 0.85f;
    public float ExternalAudioVolume { get; set; } = 0.85f;

    public bool MicPassthroughEnabled { get; private set; }
    public bool ExternalAudioEnabled { get; private set; }

    // ── State ────────────────────────────────────────────────────────────────
    private readonly object _lock = new();
    private ActivePlayback? _active;
    private MicPassthroughMixer? _micMixer;

    public bool IsPlaying
    {
        get { lock (_lock) return _active?.IsRunning == true; }
    }

    // ── Device enumeration ───────────────────────────────────────────────────

    public static List<AudioDeviceInfo> ListOutputDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, "WASAPI", false, true));
        }
        return result;
    }

    public static List<AudioDeviceInfo> ListInputDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(device.ID, device.FriendlyName, "WASAPI", true, false));
        }
        return result;
    }

    public static List<AudioDeviceInfo> ListLoopbackDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var name = device.FriendlyName;
            var nameLower = name.ToLowerInvariant();
            if (nameLower.Contains("vb-audio") || nameLower.Contains("cable"))
                continue;
            result.Add(new AudioDeviceInfo(
                $"loopback:{device.ID}", name, "WASAPI Loopback", true, false));
        }
        return result;
    }

    // ── Volume/device setters ────────────────────────────────────────────────

    public void SetVolumes(float? monitor = null, float? injection = null,
                           float? mic = null, float? externalAudio = null)
    {
        lock (_lock)
        {
            if (monitor != null)       MonitorVolume       = Clamp(monitor.Value);
            if (injection != null)     InjectionVolume     = Clamp(injection.Value);
            if (mic != null)           MicVolume           = Clamp(mic.Value);
            if (externalAudio != null) ExternalAudioVolume = Clamp(externalAudio.Value);

            _active?.SetVolumes(MonitorVolume, InjectionVolume);
            if (_micMixer != null)
            {
                _micMixer.MicVolume           = MicVolume;
                _micMixer.SoundVolume         = InjectionVolume;
                _micMixer.ExternalAudioVolume = ExternalAudioVolume;
            }
        }
    }

    public void SetMicPassthroughEnabled(bool enabled)
    {
        MicPassthroughEnabled = enabled;
        if (!enabled && !ExternalAudioEnabled)
            StopMicPassthrough();
    }

    public void SetExternalAudioEnabled(bool enabled)
    {
        bool wasRunning;
        lock (_lock) wasRunning = _micMixer != null;
        ExternalAudioEnabled = enabled;
        if (wasRunning) { StopMicPassthrough(); StartMicPassthrough(); }
    }

    // ── Mic passthrough ──────────────────────────────────────────────────────

    public void StartMicPassthrough()
    {
        bool routeMic      = MicPassthroughEnabled;
        bool routeExternal = ExternalAudioEnabled;
        if (!routeMic && !routeExternal) return;
        if (routeMic && MicDeviceId == null)
            throw new InvalidOperationException("Mic device not selected.");
        if (InjectionDeviceId == null)
            throw new InvalidOperationException("Injection device not selected.");

        MicPassthroughMixer? existing;
        lock (_lock) existing = _micMixer;

        if (existing != null && existing.IsRunning &&
            existing.MicDeviceId == MicDeviceId &&
            existing.InjectionDeviceId == InjectionDeviceId)
        {
            existing.MicVolume   = routeMic ? MicVolume : 0f;
            existing.SoundVolume = InjectionVolume;
            return;
        }

        existing?.Stop();

        var mixer = new MicPassthroughMixer(
            micDeviceId:        routeMic ? MicDeviceId : null,
            injectionDeviceId:  InjectionDeviceId,
            externalDeviceId:   routeExternal ? ExternalAudioDeviceId : null,
            micVolume:          routeMic ? MicVolume : 0f,
            externalAudioVolume: ExternalAudioVolume,
            soundVolume:        InjectionVolume);

        mixer.Start();
        lock (_lock) _micMixer = mixer;
    }

    public void StopMicPassthrough()
    {
        MicPassthroughMixer? mixer;
        lock (_lock) { mixer = _micMixer; _micMixer = null; }
        mixer?.Stop();
    }

    public bool IsMicPassthroughRunning
    {
        get { lock (_lock) return _micMixer?.IsRunning == true; }
    }

    // ── File playback ────────────────────────────────────────────────────────

    public void PlayFile(string filePath, float startSeconds = 0f,
                         PlaybackFinishedCallback? onFinished = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found.", filePath);
        if (MonitorDeviceId == null)
            throw new InvalidOperationException("Monitor device not selected.");
        if (InjectionDeviceId == null)
            throw new InvalidOperationException("Injection device not selected.");

        if (MicPassthroughEnabled || ExternalAudioEnabled)
            StartMicPassthrough();

        ActivePlayback? prev;
        lock (_lock) { prev = _active; _active = null; }
        prev?.Stop();

        MicPassthroughMixer? mixer;
        lock (_lock) mixer = _micMixer;

        var playback = new ActivePlayback(
            filePath:         filePath,
            monitorDeviceId:  MonitorDeviceId,
            injectionDeviceId: InjectionDeviceId,
            monitorVolume:    MonitorVolume,
            injectionVolume:  InjectionVolume,
            startSeconds:     startSeconds,
            onFinished: (status, err) =>
            {
                lock (_lock) { if (_active?.IsRunning == false) _active = null; }
                onFinished?.Invoke(status, err);
            });

        lock (_lock) _active = playback;
        playback.Start();
    }

    public void Stop()
    {
        ActivePlayback? active;
        lock (_lock) { active = _active; _active = null; }
        active?.Stop();
    }

    public static double GetFileDurationSeconds(string filePath)
    {
        using var reader = new AudioFileReader(filePath);
        return reader.TotalTime.TotalSeconds;
    }

    private static float Clamp(float v) => Math.Clamp(v, 0f, 1f);

    public void Dispose() { Stop(); StopMicPassthrough(); }
}

// ── ActivePlayback ────────────────────────────────────────────────────────────

internal sealed class ActivePlayback
{
    private readonly string _filePath;
    private readonly string _monitorDeviceId;
    private readonly string _injectionDeviceId;
    private readonly float _startSeconds;
    private readonly PlaybackFinishedCallback _onFinished;

    private WasapiOut? _monitorOut;
    private WasapiOut? _injectionOut;
    private AudioFileReader? _monitorReader;
    private AudioFileReader? _injectionReader;
    private VolumeSampleProvider? _monitorVol;
    private VolumeSampleProvider? _injectionVol;

    private volatile bool _stopped;
    private int _finishedCount;

    public bool IsRunning { get; private set; } = true;

    public ActivePlayback(string filePath, string monitorDeviceId, string injectionDeviceId,
                          float monitorVolume, float injectionVolume, float startSeconds,
                          PlaybackFinishedCallback onFinished)
    {
        _filePath          = filePath;
        _monitorDeviceId   = monitorDeviceId;
        _injectionDeviceId = injectionDeviceId;
        _startSeconds      = startSeconds;
        _onFinished        = onFinished;
    }

    public void Start()
    {
        try
        {
            var enumerator = new MMDeviceEnumerator();

            _monitorReader = new AudioFileReader(_filePath);
            if (_startSeconds > 0)
                _monitorReader.CurrentTime = TimeSpan.FromSeconds(_startSeconds);
            _monitorVol = new VolumeSampleProvider(_monitorReader.ToSampleProvider());

            var monitorDevice = enumerator.GetDevice(_monitorDeviceId);
            _monitorOut = new WasapiOut(monitorDevice, AudioClientShareMode.Shared, true, 50);
            _monitorOut.Init(_monitorVol);
            _monitorOut.PlaybackStopped += OnMonitorStopped;

            _injectionReader = new AudioFileReader(_filePath);
            if (_startSeconds > 0)
                _injectionReader.CurrentTime = TimeSpan.FromSeconds(_startSeconds);
            _injectionVol = new VolumeSampleProvider(_injectionReader.ToSampleProvider());

            var injectionDevice = enumerator.GetDevice(_injectionDeviceId);
            _injectionOut = new WasapiOut(injectionDevice, AudioClientShareMode.Shared, true, 50);
            _injectionOut.Init(_injectionVol);
            _injectionOut.PlaybackStopped += OnInjectionStopped;

            _monitorOut.Play();
            _injectionOut.Play();
        }
        catch (Exception ex)
        {
            IsRunning = false;
            _onFinished("error", ex);
        }
    }

    public void SetVolumes(float monitor, float injection)
    {
        if (_monitorVol != null)   _monitorVol.Volume   = monitor;
        if (_injectionVol != null) _injectionVol.Volume = injection;
    }

    public void Stop()
    {
        _stopped = true;
        _monitorOut?.Stop();
        _injectionOut?.Stop();
        Cleanup();
        IsRunning = false;
        _onFinished("stopped", null);
    }

    private void OnMonitorStopped(object? sender, StoppedEventArgs e)   => HandleStreamStopped(e);
    private void OnInjectionStopped(object? sender, StoppedEventArgs e) => HandleStreamStopped(e);

    private void HandleStreamStopped(StoppedEventArgs e)
    {
        if (_stopped) return;
        if (Interlocked.Increment(ref _finishedCount) < 2) return;
        IsRunning = false;
        Cleanup();
        _onFinished(e.Exception != null ? "error" : "finished", e.Exception);
    }

    private void Cleanup()
    {
        _monitorOut?.Dispose();
        _injectionOut?.Dispose();
        _monitorReader?.Dispose();
        _injectionReader?.Dispose();
    }
}

// ── MicPassthroughMixer ───────────────────────────────────────────────────────

internal sealed class MicPassthroughMixer
{
    public string? MicDeviceId { get; }
    public string? InjectionDeviceId { get; }
    public string? ExternalDeviceId { get; }

    public volatile float MicVolume;
    public volatile float SoundVolume;
    public volatile float ExternalAudioVolume;

    public bool IsRunning { get; private set; }

    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private WasapiOut? _injectionOut;
    private MixingSampleProvider? _mixer;
    private BufferedWaveProvider? _micBuffer;
    private BufferedWaveProvider? _externalBuffer;
    private VolumeSampleProvider? _micVolProv;
    private VolumeSampleProvider? _extVolProv;

    public MicPassthroughMixer(string? micDeviceId, string? injectionDeviceId,
                                string? externalDeviceId, float micVolume,
                                float externalAudioVolume, float soundVolume)
    {
        MicDeviceId         = micDeviceId;
        InjectionDeviceId   = injectionDeviceId;
        ExternalDeviceId    = externalDeviceId;
        MicVolume           = micVolume;
        ExternalAudioVolume = externalAudioVolume;
        SoundVolume         = soundVolume;
    }

    public void Start()
    {
        var enumerator = new MMDeviceEnumerator();
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        _micBuffer      = new BufferedWaveProvider(waveFormat) { DiscardOnBufferOverflow = true };
        _externalBuffer = new BufferedWaveProvider(waveFormat) { DiscardOnBufferOverflow = true };
        _micVolProv     = new VolumeSampleProvider(_micBuffer.ToSampleProvider());
        _extVolProv     = new VolumeSampleProvider(_externalBuffer.ToSampleProvider());
        _mixer          = new MixingSampleProvider(waveFormat) { ReadFully = true };

        if (MicDeviceId != null)
        {
            _micCapture = new WasapiCapture(enumerator.GetDevice(MicDeviceId));
            _micCapture.DataAvailable += (_, e) =>
            {
                _micVolProv.Volume = MicVolume;
                _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            _mixer.AddMixerInput(_micVolProv);
        }

        if (ExternalDeviceId != null)
        {
            var extDevice = enumerator.GetDevice(ExternalDeviceId);
            _loopbackCapture = new WasapiLoopbackCapture(extDevice);
            _loopbackCapture.DataAvailable += (_, e) =>
            {
                _extVolProv.Volume = ExternalAudioVolume;
                _externalBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            _mixer.AddMixerInput(_extVolProv);
            _loopbackCapture.StartRecording();
        }

        var injectionDevice = enumerator.GetDevice(InjectionDeviceId!);
        _injectionOut = new WasapiOut(injectionDevice, AudioClientShareMode.Shared, true, 50);
        _injectionOut.Init(_mixer);
        _micCapture?.StartRecording();
        _injectionOut.Play();
        IsRunning = true;
    }

    public void Stop()
    {
        IsRunning = false;
        _micCapture?.StopRecording();
        _loopbackCapture?.StopRecording();
        _injectionOut?.Stop();
        _micCapture?.Dispose();
        _loopbackCapture?.Dispose();
        _injectionOut?.Dispose();
    }
}
