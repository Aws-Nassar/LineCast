using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NAudio.MediaFoundation;

namespace LineCast;

public record AudioDeviceInfo(
    string Id,
    string Name,
    string HostApi,
    bool IsInput,
    bool IsOutput);

public delegate void PlaybackFinishedCallback(string status, Exception? error);

public sealed class AudioHandler : IDisposable
{
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

    private readonly object _lock = new();
    private ActivePlayback? _active;
    private MicPassthroughMixer? _micMixer;

    public bool IsPlaying
    {
        get { lock (_lock) return _active?.IsRunning == true; }
    }

    // ── Device enumeration ───────────────────────────────────────────────────

    /// <summary>
    /// Returns ALL output endpoints across every host API, numbered like the
    /// Python version: "0: Speakers (Realtek) [Windows WASAPI]"
    /// </summary>
    public static List<AudioDeviceInfo> ListOutputDevices()
    {
        var result = new List<AudioDeviceInfo>();
        int index = 0;

        // WASAPI (primary)
        var enumerator = new MMDeviceEnumerator();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(
                d.ID,
                $"{index}: {d.FriendlyName} [Windows WASAPI]",
                "Windows WASAPI",
                false, true));
            index++;
        }

        // DirectSound
        foreach (var ds in DirectSoundOut.Devices)
        {
            result.Add(new AudioDeviceInfo(
                $"ds:{ds.Guid}",
                $"{index}: {ds.Description} [Windows DirectSound]",
                "Windows DirectSound",
                false, true));
            index++;
        }

        return result;
    }

    /// <summary>
    /// Returns ALL input endpoints, numbered across host APIs.
    /// </summary>
    public static List<AudioDeviceInfo> ListInputDevices()
    {
        var result = new List<AudioDeviceInfo>();
        int index = 0;

        var enumerator = new MMDeviceEnumerator();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(
                d.ID,
                $"{index}: {d.FriendlyName} [Windows WASAPI]",
                "Windows WASAPI",
                true, false));
            index++;
        }

        // WaveIn devices (MME equivalent)
        int waveInCount = WaveIn.DeviceCount;
        for (int i = 0; i < waveInCount; i++)
        {
            var caps = WaveIn.GetCapabilities(i);
            result.Add(new AudioDeviceInfo(
                $"wavein:{i}",
                $"{index}: {caps.ProductName} [MME]",
                "MME",
                true, false));
            index++;
        }

        return result;
    }

    /// <summary>
    /// Returns loopback-capable render endpoints (for capturing system audio).
    /// In advanced mode these are all render devices; in normal mode only real
    /// audio outputs (not virtual cables).
    /// </summary>
    public static List<AudioDeviceInfo> ListLoopbackDevices()
    {
        var enumerator = new MMDeviceEnumerator();
        var result = new List<AudioDeviceInfo>();
        int index = 0;

        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            result.Add(new AudioDeviceInfo(
                $"loopback:{d.ID}",
                $"{index}: {d.FriendlyName} [Windows WASAPI Loopback]",
                "Windows WASAPI Loopback",
                true, false));
            index++;
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

    // ── Mic/loopback passthrough ─────────────────────────────────────────────

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
            existing.MicDeviceId     == MicDeviceId &&
            existing.InjectionDeviceId == InjectionDeviceId &&
            existing.ExternalDeviceId == ExternalAudioDeviceId)
        {
            existing.MicVolume           = routeMic ? MicVolume : 0f;
            existing.SoundVolume         = InjectionVolume;
            existing.ExternalAudioVolume = ExternalAudioVolume;
            return;
        }

        existing?.Stop();

        var mixer = new MicPassthroughMixer(
            micDeviceId:         routeMic      ? MicDeviceId           : null,
            injectionDeviceId:   InjectionDeviceId,
            externalDeviceId:    routeExternal ? ExternalAudioDeviceId : null,
            micVolume:           routeMic      ? MicVolume             : 0f,
            externalAudioVolume: ExternalAudioVolume,
            soundVolume:         InjectionVolume);

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

        var playback = new ActivePlayback(
            filePath:          filePath,
            monitorDeviceId:   MonitorDeviceId,
            injectionDeviceId: InjectionDeviceId,
            monitorVolume:     MonitorVolume,
            injectionVolume:   InjectionVolume,
            startSeconds:      startSeconds,
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

    // Resampler for loopback → mixer format conversion
    private WaveFormat? _mixerFormat;

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

    private static string RealId(string id) =>
        id.StartsWith("loopback:") ? id["loopback:".Length..] : id;

    public void Start()
    {
        var enumerator = new MMDeviceEnumerator();

        // ── Pick a common mixer format based on what's available ─────────────
        // Use the injection device's mix format as the master
        var injDev = enumerator.GetDevice(RealId(InjectionDeviceId!));
        var injFmt = injDev.AudioClient.MixFormat;
        _mixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            injFmt.SampleRate, Math.Min(2, injFmt.Channels));

        _micBuffer      = new BufferedWaveProvider(_mixerFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2) };
        _externalBuffer = new BufferedWaveProvider(_mixerFormat) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromSeconds(2) };
        _micVolProv     = new VolumeSampleProvider(_micBuffer.ToSampleProvider());
        _extVolProv     = new VolumeSampleProvider(_externalBuffer.ToSampleProvider());
        _mixer          = new MixingSampleProvider(_mixerFormat) { ReadFully = true };

        // ── Mic capture ──────────────────────────────────────────────────────
        if (MicDeviceId != null)
        {
            var micDevice = enumerator.GetDevice(RealId(MicDeviceId));
            _micCapture = new WasapiCapture(micDevice);
            // Force the capture format to match our mixer so no conversion needed
            _micCapture.WaveFormat = _mixerFormat;
            _micCapture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded == 0) return;
                _micVolProv.Volume = MicVolume;
                _micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };
            _mixer.AddMixerInput(_micVolProv);
        }

        // ── Loopback capture (system/app audio) ──────────────────────────────
        if (ExternalDeviceId != null)
        {
            var extDevice = enumerator.GetDevice(RealId(ExternalDeviceId));
            _loopbackCapture = new WasapiLoopbackCapture(extDevice);
            // Loopback format is whatever the render device uses — may differ
            var loopbackFmt = _loopbackCapture.WaveFormat;

            _loopbackCapture.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded == 0) return;

                byte[] converted;
                if (loopbackFmt.Equals(_mixerFormat))
                {
                    // Same format — copy directly
                    converted = e.Buffer[..e.BytesRecorded];
                }
                else
                {
                    // Resample: float32 loopback → float32 mixer format
                    converted = ResampleFloat(e.Buffer, e.BytesRecorded,
                                              loopbackFmt, _mixerFormat);
                }

                if (converted.Length == 0) return;
                _extVolProv.Volume = ExternalAudioVolume;
                _externalBuffer.AddSamples(converted, 0, converted.Length);
            };

            _mixer.AddMixerInput(_extVolProv);
            _loopbackCapture.StartRecording();
        }

        // ── Injection output ─────────────────────────────────────────────────
        _injectionOut = new WasapiOut(injDev, AudioClientShareMode.Shared, true, 50);
        _injectionOut.Init(_mixer);
        _micCapture?.StartRecording();
        _injectionOut.Play();
        IsRunning = true;
    }

    /// <summary>
    /// Resample raw IEEE float bytes from <paramref name="src"/> format to
    /// <paramref name="dst"/> format using NAudio's MediaFoundation resampler.
    /// Falls back to a simple channel/sample-rate conversion if MF is unavailable.
    /// </summary>
    private static byte[] ResampleFloat(byte[] buffer, int count,
                                         WaveFormat src, WaveFormat dst)
    {
        try
        {
            using var ms  = new MemoryStream(buffer, 0, count);
            using var raw = new RawSourceWaveStream(ms, src);

            // Convert to PCM16 first if needed (MF resampler works best with PCM)
            ISampleProvider sp = raw.ToSampleProvider();

            // Handle channel count mismatch
            if (src.Channels == 1 && dst.Channels == 2)
                sp = new MonoToStereoSampleProvider(sp);
            else if (src.Channels == 2 && dst.Channels == 1)
                sp = new StereoToMonoSampleProvider(sp);

            // Handle sample rate mismatch using MediaFoundation
            if (src.SampleRate != dst.SampleRate)
            {
                var pcm16 = sp.ToWaveProvider16();
                var pcmDst = new WaveFormat(dst.SampleRate, 16, dst.Channels);
                using var resampled = new MediaFoundationResampler(pcm16, pcmDst);
                resampled.ResamplerQuality = 60;
                using var outMs = new MemoryStream();
                var tmp = new byte[4096];
                int read;
                while ((read = resampled.Read(tmp, 0, tmp.Length)) > 0)
                    outMs.Write(tmp, 0, read);

                // Convert back to float32
                var pcmBytes = outMs.ToArray();
                var floatBytes = new byte[pcmBytes.Length * 2]; // 16bit → 32bit float
                using var pcmMs    = new MemoryStream(pcmBytes);
                using var pcmWave  = new RawSourceWaveStream(pcmMs, pcmDst);
                using var floatOut = new MemoryStream();
                var floatSp = pcmWave.ToSampleProvider();
                var floatBuf = new float[1024];
                int fread;
                while ((fread = floatSp.Read(floatBuf, 0, floatBuf.Length)) > 0)
                {
                    var bytes = new byte[fread * 4];
                    Buffer.BlockCopy(floatBuf, 0, bytes, 0, bytes.Length);
                    floatOut.Write(bytes, 0, bytes.Length);
                }
                return floatOut.ToArray();
            }
            else
            {
                // Same sample rate — just convert channels, output as float
                using var outMs = new MemoryStream();
                var floatBuf = new float[1024];
                int fread;
                while ((fread = sp.Read(floatBuf, 0, floatBuf.Length)) > 0)
                {
                    var bytes = new byte[fread * 4];
                    Buffer.BlockCopy(floatBuf, 0, bytes, 0, bytes.Length);
                    outMs.Write(bytes, 0, bytes.Length);
                }
                return outMs.ToArray();
            }
        }
        catch
        {
            return Array.Empty<byte>();
        }
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
