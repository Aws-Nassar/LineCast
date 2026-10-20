using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace LineCast.Audio;

/// <summary>
/// Sums any number of stereo sources. Inputs can be added or removed from the UI
/// thread while the audio thread reads; the snapshot is swapped atomically so the
/// callback never blocks on a lock taken by the UI.
/// </summary>
public sealed class MixerProvider : ISampleProvider
{
    private readonly object _gate = new();
    private ISampleProvider[] _inputs = Array.Empty<ISampleProvider>();
    private float[] _scratch = new float[8192];

    public MixerProvider(WaveFormat format)
    {
        WaveFormat = format;
    }

    public WaveFormat WaveFormat { get; }

    public int InputCount => _inputs.Length;

    public void Add(ISampleProvider input)
    {
        lock (_gate)
        {
            var list = new List<ISampleProvider>(_inputs) { input };
            Volatile.Write(ref _inputs, list.ToArray());
        }
    }

    public void Remove(ISampleProvider input)
    {
        lock (_gate)
        {
            var list = new List<ISampleProvider>(_inputs);
            if (list.Remove(input))
            {
                Volatile.Write(ref _inputs, list.ToArray());
            }
        }
    }

    public void ClearInputs()
    {
        lock (_gate)
        {
            Volatile.Write(ref _inputs, Array.Empty<ISampleProvider>());
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);

        var inputs = Volatile.Read(ref _inputs);
        if (inputs.Length == 0)
        {
            return count;
        }

        if (_scratch.Length < count)
        {
            // Grows once, on the audio thread, then is reused forever.
            _scratch = new float[count];
        }

        foreach (var input in inputs)
        {
            int read;
            try
            {
                read = input.Read(_scratch, 0, count);
            }
            catch
            {
                continue;
            }

            if (read <= 0)
            {
                continue;
            }

            for (var i = 0; i < read; i++)
            {
                buffer[offset + i] += _scratch[i];
            }
        }

        return count;
    }
}

/// <summary>Linear volume with a short ramp, so toggles do not click.</summary>
public sealed class RampVolumeProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private volatile float _target;
    private float _current;

    public RampVolumeProvider(ISampleProvider source, float initial = 1f, float rampMs = 25)
    {
        _source = source;
        _target = Math.Clamp(initial, 0f, 4f);
        _current = _target;
        RampMilliseconds = rampMs;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public float RampMilliseconds { get; }

    public float Target
    {
        get => _target;
        set => _target = Math.Clamp(value, 0f, 4f);
    }

    public float Current => _current;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0)
        {
            return read;
        }

        var channels = WaveFormat.Channels;
        var frames = Math.Max(1, read / channels);
        var rampFrames = Math.Max(1, (int)(WaveFormat.SampleRate * RampMilliseconds / 1000.0));
        var step = (_target - _current) / Math.Min(frames, rampFrames);

        for (var frame = 0; frame < frames; frame++)
        {
            if (Math.Abs(_target - _current) <= Math.Abs(step))
            {
                _current = _target;
            }
            else
            {
                _current += step;
            }

            for (var c = 0; c < channels; c++)
            {
                buffer[offset + frame * channels + c] *= _current;
            }
        }

        return read;
    }
}

/// <summary>Pass-through that reports a decaying peak level for the UI meters.</summary>
public sealed class PeakMeterProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float _peak;

    public PeakMeterProvider(ISampleProvider source)
    {
        _source = source;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public float Peak => _peak;

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);

        var peak = 0f;
        for (var i = 0; i < read; i++)
        {
            var value = Math.Abs(buffer[offset + i]);
            if (value > peak)
            {
                peak = value;
            }
        }

        _peak = Math.Max(peak, _peak * 0.88f);
        return read;
    }
}

/// <summary>
/// Fast peak limiter. Instant attack, exponential release, no lookahead — the
/// old app hard-clipped at ±1.0 which is audibly worse than a limiter.
/// </summary>
public sealed class LimiterProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly float _ceiling;
    private float _gain = 1f;

    public LimiterProvider(ISampleProvider source, float ceiling = 0.89f)
    {
        _source = source;
        _ceiling = Math.Clamp(ceiling, 0.1f, 0.999f);
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public float PeakReduction { get; private set; }

    public int Read(float[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        var channels = WaveFormat.Channels;
        var frames = read / channels;
        var release = 1f - Math.Min(0.5f, 1f / Math.Max(1, WaveFormat.SampleRate * 0.12f));

        for (var frame = 0; frame < frames; frame++)
        {
            var peak = 0f;
            for (var c = 0; c < channels; c++)
            {
                peak = Math.Max(peak, Math.Abs(buffer[offset + frame * channels + c]));
            }

            var desired = peak > _ceiling ? _ceiling / peak : 1f;
            if (desired < _gain)
            {
                _gain = desired;
            }
            else
            {
                _gain = _gain * release + desired * (1f - release);
            }

            for (var c = 0; c < channels; c++)
            {
                var index = offset + frame * channels + c;
                buffer[index] = Math.Clamp(buffer[index] * _gain, -1f, 1f);
            }
        }

        PeakReduction = 1f - _gain;
        return read;
    }
}

/// <summary>Maps the internal stereo bus onto the device's mix channel count.</summary>
public sealed class ChannelMapProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[] _scratch = Array.Empty<float>();

    public ChannelMapProvider(ISampleProvider source, int targetChannels)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        TargetChannels = targetChannels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            source.WaveFormat.SampleRate, targetChannels);
    }

    public WaveFormat WaveFormat { get; }

    public int TargetChannels { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_sourceChannels == TargetChannels)
        {
            return _source.Read(buffer, offset, count);
        }

        if (_scratch.Length < count)
        {
            _scratch = new float[count];
        }

        var read = _source.Read(_scratch, 0, count);
        var frames = read / Math.Max(1, _sourceChannels);

        Array.Clear(buffer, offset, frames * TargetChannels);

        for (var frame = 0; frame < frames; frame++)
        {
            var left = _scratch[frame * _sourceChannels];
            var right = _sourceChannels > 1 ? _scratch[frame * _sourceChannels + 1] : left;

            if (TargetChannels == 1)
            {
                buffer[offset + frame] = (left + right) * 0.5f;
                continue;
            }

            buffer[offset + frame * TargetChannels] = left;
            buffer[offset + frame * TargetChannels + 1] = right;
        }

        return frames * TargetChannels;
    }
}

/// <summary>Streams a decoded clip from memory at its native rate.</summary>
public sealed class ClipSource : ISampleProvider
{
    private readonly float[] _samples;
    private int _position;

    public ClipSource(Clip clip, TimeSpan startOffset)
    {
        _samples = clip.Samples;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(clip.SampleRate, 2);

        var frames = (int)(startOffset.TotalSeconds * clip.SampleRate);
        _position = Math.Clamp(frames * 2, 0, Math.Max(0, _samples.Length - 2));
    }

    public WaveFormat WaveFormat { get; }

    public bool Finished => _position >= _samples.Length;

    public int Read(float[] buffer, int offset, int count)
    {
        var remaining = _samples.Length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        var take = Math.Min(count, remaining);
        Array.Copy(_samples, _position, buffer, offset, take);
        _position += take;
        return take;
    }
}

/// <summary>
/// One clip rendered into one output bus. Both the monitor and the injection bus
/// get their own instance so neither can starve the other, and both are released
/// by the same wall-clock start gate so they begin in sync.
/// </summary>
public sealed class BusClipPlayer : ISampleProvider
{
    private readonly ISampleProvider _volume;
    private readonly long _startAt;
    private volatile bool _finished;

    public BusClipPlayer(Clip clip, int busRate, float volume, long startAtTimestamp, TimeSpan offset = default)
    {
        var source = new ClipSource(clip, offset);
        ISampleProvider resampled = clip.SampleRate == busRate
            ? source
            : new WdlResamplingSampleProvider(source, busRate);

        _volume = new RampVolumeProvider(resampled, volume, rampMs: 12);
        _startAt = startAtTimestamp;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(busRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public bool Finished => _finished;

    public float Volume
    {
        get => ((RampVolumeProvider)_volume).Target;
        set => ((RampVolumeProvider)_volume).Target = value;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (_finished)
        {
            return 0;
        }

        if (System.Diagnostics.Stopwatch.GetTimestamp() < _startAt)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        var read = _volume.Read(buffer, offset, count);
        if (read <= 0)
        {
            _finished = true;
            Array.Clear(buffer, offset, count);
            return count;
        }

        return read;
    }
}
