using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace MicDeck.Audio;

public sealed record AudioRoute(
    string? MonitorId,
    string? InjectionId,
    string? MicId,
    string? SystemId,
    int LatencyMs);

/// <summary>
/// Owns the whole audio graph: two output buses (injection + monitor), optional
/// microphone and system-loopback captures, and the soundboard clip taps.
///
/// The graph is built once per device selection and then runs continuously.
/// Playback never re-opens a stream, which removes the per-clip setup latency
/// and the clicks the previous implementation produced on every play and seek.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly object _gate = new();
    private readonly ClipCache _clips = new();

    private AudioBus? _injection;
    private AudioBus? _monitor;
    private CaptureSource? _mic;
    private CaptureSource? _system;
    private RampVolumeProvider? _micRamp;
    private RampVolumeProvider? _systemRamp;
    private PeakMeterProvider? _micMeter;
    private PeakMeterProvider? _systemMeter;
    private BusClipPlayer? _injectionClip;
    private BusClipPlayer? _monitorClip;

    private float _monitorVolume = 0.85f;
    private float _injectionVolume = 0.85f;
    private float _micVolume = 0.95f;
    private float _systemVolume = 0.70f;
    private bool _micEnabled = true;
    private bool _systemEnabled;
    private bool _monitorClips = true;

    private long _clipStartTicks;
    private TimeSpan _clipDuration;
    private TimeSpan _clipOffset;
    private float _clipGain = 1f;
    private Clip? _currentClip;
    private bool _disposed;

    public event Action<string>? Faulted;

    public ClipCache Clips => _clips;

    public bool IsConfigured => _injection is { IsRunning: true };

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                if (_currentClip is null)
                {
                    return false;
                }

                return _injectionClip is { Finished: false } || _monitorClip is { Finished: false };
            }
        }
    }

    public Clip? CurrentClip
    {
        get
        {
            lock (_gate)
            {
                return _currentClip;
            }
        }
    }

    public TimeSpan ClipPosition
    {
        get
        {
            lock (_gate)
            {
                if (_currentClip is null)
                {
                    return TimeSpan.Zero;
                }

                var elapsed = (Stopwatch.GetTimestamp() - _clipStartTicks) / (double)Stopwatch.Frequency;
                var position = _clipOffset.TotalSeconds + elapsed;
                return TimeSpan.FromSeconds(Math.Clamp(position, 0, _clipDuration.TotalSeconds));
            }
        }
    }

    public float MicLevel => _micMeter?.Peak ?? 0f;

    public float SystemLevel => _systemMeter?.Peak ?? 0f;

    public float OutputLevel => _injection?.Peak ?? 0f;

    public float MonitorLevel => _monitor?.Peak ?? 0f;

    public bool MicCapturing => _mic is { IsRunning: true };

    public bool SystemCapturing => _system is { IsRunning: true };

    public double MicBufferMilliseconds => _mic?.Buffer.BufferedMilliseconds ?? 0;

    public double SystemBufferMilliseconds => _system?.Buffer.BufferedMilliseconds ?? 0;

    public long MicUnderruns => _mic?.Buffer.Underruns ?? 0;

    public void Configure(AudioRoute route)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            Teardown();
            StopClipInternal();

            if (string.IsNullOrWhiteSpace(route.InjectionId))
            {
                Faulted?.Invoke("Choose an output device to inject into.");
                return;
            }

            try
            {
                _injection = new AudioBus(DeviceCatalog.Open(route.InjectionId), route.LatencyMs);
            }
            catch (Exception ex)
            {
                Faulted?.Invoke($"Cannot open the injection device: {ex.Message}");
                return;
            }

            if (!string.IsNullOrWhiteSpace(route.MonitorId))
            {
                try
                {
                    _monitor = new AudioBus(DeviceCatalog.Open(route.MonitorId), route.LatencyMs);
                }
                catch (Exception ex)
                {
                    Faulted?.Invoke($"Cannot open the monitor device: {ex.Message}");
                }
            }

            var rate = _injection.SampleRate;

            if (!string.IsNullOrWhiteSpace(route.MicId))
            {
                try
                {
                    // 70 ms target keeps push-to-talk responsive; the adaptive
                    // buffer holds it there instead of drifting.
                    _mic = new CaptureSource(route.MicId, loopback: false, downmixToMono: true,
                        targetRate: rate, targetFillMs: 70);
                    _mic.Faulted += OnCaptureFaulted;
                }
                catch (Exception ex)
                {
                    Faulted?.Invoke($"Cannot open the microphone: {ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(route.SystemId))
            {
                try
                {
                    _system = new CaptureSource(route.SystemId, loopback: true, downmixToMono: false,
                        targetRate: rate, targetFillMs: 120);
                    _system.Faulted += OnCaptureFaulted;
                }
                catch (Exception ex)
                {
                    Faulted?.Invoke($"Cannot capture system audio: {ex.Message}");
                }
            }

            if (_mic is not null)
            {
                _micRamp = new RampVolumeProvider(
                    _mic.Buffer, _micEnabled ? _micVolume : 0f, rampMs: 40);
                _micMeter = new PeakMeterProvider(_micRamp);
                _injection.AddInput(_micMeter);
            }

            if (_system is not null)
            {
                _systemRamp = new RampVolumeProvider(
                    _system.Buffer, _systemEnabled ? _systemVolume : 0f, rampMs: 40);
                _systemMeter = new PeakMeterProvider(_systemRamp);
                _injection.AddInput(_systemMeter);
            }

            try
            {
                // Capture first so the adaptive buffers are primed, then start
                // the outputs. This avoids an audible gap on the first block.
                _mic?.Start();
                _system?.Start();
                _injection.Start();
                _monitor?.Start();
            }
            catch (Exception ex)
            {
                Faulted?.Invoke($"Audio engine failed to start: {ex.Message}");
                Teardown();
            }
        }
    }

    public void SetVolumes(float monitor, float injection, float mic, float system)
    {
        lock (_gate)
        {
            _monitorVolume = Math.Clamp(monitor, 0f, 2f);
            _injectionVolume = Math.Clamp(injection, 0f, 2f);
            _micVolume = Math.Clamp(mic, 0f, 2f);
            _systemVolume = Math.Clamp(system, 0f, 2f);

            if (_micRamp is not null)
            {
                _micRamp.Target = _micEnabled ? _micVolume : 0f;
            }

            if (_systemRamp is not null)
            {
                _systemRamp.Target = _systemEnabled ? _systemVolume : 0f;
            }

            if (_injectionClip is not null)
            {
                _injectionClip.Volume = _injectionVolume * _clipGain;
            }

            if (_monitorClip is not null)
            {
                _monitorClip.Volume = _monitorVolume * _clipGain;
            }
        }
    }

    public void SetMicEnabled(bool enabled)
    {
        lock (_gate)
        {
            _micEnabled = enabled;
            if (_micRamp is not null)
            {
                _micRamp.Target = enabled ? _micVolume : 0f;
            }
        }
    }

    public void SetSystemEnabled(bool enabled)
    {
        lock (_gate)
        {
            _systemEnabled = enabled;
            if (_systemRamp is not null)
            {
                _systemRamp.Target = enabled ? _systemVolume : 0f;
            }
        }
    }

    public void SetMonitorClips(bool enabled)
    {
        lock (_gate)
        {
            _monitorClips = enabled;
        }
    }

    public void Play(Clip clip, TimeSpan offset = default, float gain = 1f)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            StopClipInternal();

            if (_injection is null)
            {
                Faulted?.Invoke("Audio is not configured yet.");
                return;
            }

            // Both buses release the clip at the same wall-clock instant. They are
            // separate devices with separate clocks, so this gate is what keeps
            // monitor and injection aligned within a few milliseconds.
            var lead = (long)(Stopwatch.Frequency * 0.03);
            var startAt = Stopwatch.GetTimestamp() + lead;

            if (offset < TimeSpan.Zero || offset > clip.Duration)
            {
                offset = TimeSpan.Zero;
            }

            _currentClip = clip;
            _clipStartTicks = startAt;
            _clipDuration = clip.Duration;
            _clipOffset = offset;
            _clipGain = Math.Clamp(gain, 0f, 2f);

            _injectionClip = new BusClipPlayer(
                clip, _injection.SampleRate, _injectionVolume * _clipGain, startAt, offset);
            _injection.AddInput(_injectionClip);

            if (_monitor is not null && _monitorClips)
            {
                _monitorClip = new BusClipPlayer(
                    clip, _monitor.SampleRate, _monitorVolume * _clipGain, startAt, offset);
                _monitor.AddInput(_monitorClip);
            }
        }
    }

    public void StopClip()
    {
        lock (_gate)
        {
            StopClipInternal();
        }
    }

    /// <summary>Detach players that reached the end of the clip. Called from the UI timer.</summary>
    public void PruneFinished()
    {
        lock (_gate)
        {
            if (_injectionClip is { Finished: true })
            {
                _injection?.RemoveInput(_injectionClip);
                _injectionClip = null;
            }

            if (_monitorClip is { Finished: true })
            {
                _monitor?.RemoveInput(_monitorClip);
                _monitorClip = null;
            }

            if (_injectionClip is null && _monitorClip is null)
            {
                _currentClip = null;
            }
        }
    }

    private void StopClipInternal()
    {
        if (_injectionClip is not null)
        {
            _injection?.RemoveInput(_injectionClip);
            _injectionClip = null;
        }

        if (_monitorClip is not null)
        {
            _monitor?.RemoveInput(_monitorClip);
            _monitorClip = null;
        }

        _currentClip = null;
    }

    private void OnCaptureFaulted(Exception exception)
    {
        Faulted?.Invoke($"Capture device stopped: {exception.Message}");
    }

    private void Teardown()
    {
        if (_mic is not null)
        {
            _mic.Faulted -= OnCaptureFaulted;
            _mic.Stop();
        }

        if (_system is not null)
        {
            _system.Faulted -= OnCaptureFaulted;
            _system.Stop();
        }

        _injection?.Dispose();
        _monitor?.Dispose();
        _mic?.Dispose();
        _system?.Dispose();

        _injection = null;
        _monitor = null;
        _mic = null;
        _system = null;
        _micRamp = null;
        _systemRamp = null;
        _micMeter = null;
        _systemMeter = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioEngine));
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopClipInternal();
            Teardown();
        }
    }
}
