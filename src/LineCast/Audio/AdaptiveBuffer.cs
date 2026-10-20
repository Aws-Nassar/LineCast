using NAudio.Wave;

namespace LineCast.Audio;

/// <summary>
/// Stereo float ring buffer that doubles as an adaptive sample-rate converter.
///
/// Capture and playback run on independent device clocks. Feeding one into the
/// other through a fixed buffer either starves (dropouts) or grows without bound
/// (latency creeps from 30 ms to seconds). This buffer tracks how full it is and
/// nudges the resampling ratio by up to ±1% to hold a target fill level, which is
/// inaudible but keeps latency locked.
/// </summary>
public sealed class AdaptiveBuffer : ISampleProvider
{
    private const int Channels = 2;

    private readonly float[] _ring;
    private readonly int _capacityFrames;
    private readonly object _gate = new();

    private int _read;
    private int _write;
    private int _available;
    private double _fraction;
    private float _peak;

    private long _underruns;
    private long _overflows;

    public AdaptiveBuffer(int sourceRate, int targetRate, double targetFillMs = 90, double capacityMs = 900)
    {
        SourceRate = sourceRate;
        TargetRate = targetRate;
        TargetFillMs = targetFillMs;

        _capacityFrames = Math.Max(64, (int)(sourceRate * capacityMs / 1000.0));
        _ring = new float[_capacityFrames * Channels];

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(targetRate, Channels);
    }

    public int SourceRate { get; }

    public int TargetRate { get; }

    public double TargetFillMs { get; }

    public WaveFormat WaveFormat { get; }

    public int BufferedFrames
    {
        get
        {
            lock (_gate)
            {
                return _available;
            }
        }
    }

    public double BufferedMilliseconds => BufferedFrames * 1000.0 / SourceRate;

    /// <summary>How far the buffer is from its target level, as a health signal for the UI.</summary>
    public double FillRatio
    {
        get
        {
            var target = Math.Max(1.0, TargetFillMs * 0.001 * SourceRate);
            return BufferedFrames / target;
        }
    }

    public long Underruns => Interlocked.Read(ref _underruns);

    public long Overflows => Interlocked.Read(ref _overflows);

    public float Peak => _peak;

    /// <summary>Append interleaved stereo frames produced by a capture callback.</summary>
    public void Write(float[] source, int sampleCount)
    {
        var frames = sampleCount / Channels;
        if (frames <= 0)
        {
            return;
        }

        if (frames > _capacityFrames)
        {
            // Keep the newest audio: a burst this large means we were stalled.
            source = source[(sampleCount - _capacityFrames * Channels)..];
            sampleCount = _capacityFrames * Channels;
            frames = _capacityFrames;
        }

        lock (_gate)
        {
            var overflow = _available + frames - _capacityFrames;
            if (overflow > 0)
            {
                _read = (_read + overflow) % _capacityFrames;
                _available -= overflow;
                Interlocked.Add(ref _overflows, overflow);
            }

            var first = Math.Min(frames, _capacityFrames - _write);
            Array.Copy(source, 0, _ring, _write * Channels, first * Channels);

            var second = frames - first;
            if (second > 0)
            {
                Array.Copy(source, first * Channels, _ring, 0, second * Channels);
            }

            _write = (_write + frames) % _capacityFrames;
            _available += frames;
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        var frames = count / Channels;
        if (frames <= 0)
        {
            return 0;
        }

        lock (_gate)
        {
            var targetFrames = Math.Max(1.0, TargetFillMs * 0.001 * SourceRate);
            var error = (_available - targetFrames) / targetFrames;
            var correction = Math.Clamp(1.0 + error * 0.10, 0.99, 1.01);
            var step = SourceRate / (double)TargetRate * correction;

            var position = _fraction;
            var peak = 0f;
            var starved = false;
            var produced = 0;

            for (var n = 0; n < frames; n++)
            {
                var i0 = (int)position;
                if (i0 + 1 >= _available)
                {
                    starved = true;
                    break;
                }

                var frac = position - i0;
                var a = (_read + i0) % _capacityFrames;
                var b = (_read + i0 + 1) % _capacityFrames;

                var l = _ring[a * Channels] + (_ring[b * Channels] - _ring[a * Channels]) * (float)frac;
                var r = _ring[a * Channels + 1] + (_ring[b * Channels + 1] - _ring[a * Channels + 1]) * (float)frac;

                buffer[offset + n * Channels] = l;
                buffer[offset + n * Channels + 1] = r;

                peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
                position += step;
                produced++;
            }

            if (starved)
            {
                Array.Clear(buffer, offset + produced * Channels, (frames - produced) * Channels);
                Interlocked.Increment(ref _underruns);
                _fraction = 0;
            }
            else
            {
                _fraction = position - (int)position;
            }

            var advance = Math.Min((int)position, _available);
            _read = (_read + advance) % _capacityFrames;
            _available -= advance;

            _peak = Math.Max(peak, _peak * 0.86f);
            return count;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _read = _write = _available = 0;
            _fraction = 0;
            _peak = 0f;
        }
    }
}
