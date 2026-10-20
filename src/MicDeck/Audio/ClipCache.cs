using System.IO;
using NAudio.Wave;

namespace MicDeck.Audio;

public sealed class Clip
{
    public required string Path { get; init; }

    /// <summary>Interleaved stereo float at <see cref="SampleRate"/>.</summary>
    public required float[] Samples { get; init; }

    public required int SampleRate { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>128 normalized peak buckets for the row waveform.</summary>
    public required float[] Waveform { get; init; }

    public required float RmsDb { get; init; }

    public long MemoryBytes => Samples.LongLength * sizeof(float);
}

/// <summary>
/// Decodes clips once and keeps them in memory. The old app re-decoded and
/// re-normalized the file on every play and every seek, which stalled the UI and
/// made seeking take hundreds of milliseconds.
/// </summary>
public sealed class ClipCache
{
    private const long MaxBytes = 384L * 1024 * 1024;
    private const float TargetRmsDb = -18f;
    private const float PeakCeiling = 0.85f; // ≈ -1.4 dBFS

    private readonly Dictionary<string, Clip> _clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TimeSpan> _durations = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private long _bytes;

    public static string MakeKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return path.ToLowerInvariant();
            }

            return $"{path.ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return path.ToLowerInvariant();
        }
    }

    public bool IsCached(string path) => _clips.ContainsKey(MakeKey(path));

    public Clip? TryGet(string path)
    {
        var key = MakeKey(path);
        if (!_clips.TryGetValue(key, out var clip))
        {
            return null;
        }

        Touch(key);
        return clip;
    }

    public async Task<Clip> GetAsync(string path, CancellationToken token = default)
    {
        var key = MakeKey(path);
        if (_clips.TryGetValue(key, out var cached))
        {
            Touch(key);
            return cached;
        }

        await _loadGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_clips.TryGetValue(key, out cached))
            {
                Touch(key);
                return cached;
            }

            var clip = await Task.Run(() => Decode(path), token).ConfigureAwait(false);
            _clips[key] = clip;
            _order.AddFirst(key);
            _bytes += clip.MemoryBytes;
            Trim();
            return clip;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public Clip Get(string path)
    {
        var key = MakeKey(path);
        if (_clips.TryGetValue(key, out var cached))
        {
            Touch(key);
            return cached;
        }

        _loadGate.Wait();
        try
        {
            if (_clips.TryGetValue(key, out cached))
            {
                return cached;
            }

            var clip = Decode(path);
            _clips[key] = clip;
            _order.AddFirst(key);
            _bytes += clip.MemoryBytes;
            Trim();
            return clip;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public void Invalidate(string path)
    {
        // Removes every cached revision of this file.
        var lower = path.ToLowerInvariant();
        foreach (var key in _clips.Keys.Where(k => k.StartsWith(lower, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _bytes -= _clips[key].MemoryBytes;
            _clips.Remove(key);
            _order.Remove(key);
        }

        foreach (var key in _durations.Keys.Where(k => k.StartsWith(lower, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _durations.TryRemove(key, out _);
        }
    }

    public TimeSpan? CachedDuration(string path)
        => _durations.TryGetValue(MakeKey(path), out var value) ? value : null;

    /// <summary>Reads only the container header — much cheaper than decoding.</summary>
    public async Task<TimeSpan?> ProbeDurationAsync(string path, CancellationToken token = default)
    {
        var key = MakeKey(path);
        if (_durations.TryGetValue(key, out var cached))
        {
            return cached;
        }

        await _probeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var duration = await Task.Run(() =>
            {
                try
                {
                    using var reader = new AudioFileReader(path);
                    return reader.TotalTime > TimeSpan.Zero
                        ? reader.TotalTime
                        : TimeSpan.FromSeconds((double)reader.Length / reader.WaveFormat.AverageBytesPerSecond);
                }
                catch
                {
                    return (TimeSpan?)null;
                }
            }, token).ConfigureAwait(false);

            if (duration is not null)
            {
                _durations[key] = duration.Value;
            }

            return duration;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private void Touch(string key)
    {
        var node = _order.Find(key);
        if (node is null)
        {
            return;
        }

        _order.Remove(node);
        _order.AddFirst(node);
    }

    private void Trim()
    {
        while (_bytes > MaxBytes && _order.Last is { } last)
        {
            var key = last.Value;
            if (_clips.Remove(key, out var clip))
            {
                _bytes -= clip.MemoryBytes;
            }

            _order.RemoveLast();
        }
    }

    private static Clip Decode(string path)
    {
        using var reader = new AudioFileReader(path);

        var format = reader.WaveFormat;
        var sourceChannels = Math.Max(1, format.Channels);
        var gainNormalization = 1f;

        var capacity = 1 << 18;
        var interleaved = new float[capacity];
        var chunk = new float[16384];
        var total = 0;
        int read;

        while ((read = reader.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (total + read > interleaved.Length)
            {
                Array.Resize(ref interleaved, Math.Max(interleaved.Length * 2, total + read));
            }

            Array.Copy(chunk, 0, interleaved, total, read);
            total += read;
        }

        if (total == 0)
        {
            throw new InvalidDataException($"'{System.IO.Path.GetFileName(path)}' contains no audio.");
        }

        var frames = total / sourceChannels;
        var stereo = new float[frames * 2];

        for (var frame = 0; frame < frames; frame++)
        {
            var index = frame * sourceChannels;
            var left = interleaved[index];
            var right = sourceChannels > 1 ? interleaved[index + 1] : left;

            stereo[frame * 2] = left;
            stereo[frame * 2 + 1] = right;
        }

        var (rmsDb, peak) = Measure(stereo);

        // Loudness-match clips so a quiet meme and a loud alert land at the same
        // level, while still guaranteeing headroom below 0 dBFS.
        if (rmsDb > -60f && peak > 0.0001f)
        {
            var rmsGain = MathF.Pow(10f, (TargetRmsDb - rmsDb) / 20f);
            var ceilingGain = PeakCeiling / peak;
            gainNormalization = Math.Clamp(Math.Min(rmsGain, ceilingGain), 0.02f, 24f);
        }

        if (Math.Abs(gainNormalization - 1f) > 0.001f)
        {
            for (var i = 0; i < stereo.Length; i++)
            {
                stereo[i] *= gainNormalization;
            }
        }

        var sampleRate = format.SampleRate;
        var duration = reader.TotalTime > TimeSpan.Zero
            ? reader.TotalTime
            : TimeSpan.FromSeconds(frames / (double)sampleRate);

        return new Clip
        {
            Path = path,
            Samples = stereo,
            SampleRate = sampleRate,
            Duration = duration,
            Waveform = BuildWaveform(stereo, 128),
            RmsDb = rmsDb,
        };
    }

    private static (float RmsDb, float Peak) Measure(float[] samples)
    {
        double sum = 0;
        var peak = 0f;

        for (var i = 0; i < samples.Length; i++)
        {
            var value = samples[i];
            sum += value * value;
            var abs = Math.Abs(value);
            if (abs > peak)
            {
                peak = abs;
            }
        }

        var rms = samples.Length == 0 ? 0 : Math.Sqrt(sum / samples.Length);
        var rmsDb = rms <= 1e-8 ? -120f : (float)(20 * Math.Log10(rms));
        return (rmsDb, peak);
    }

    private static float[] BuildWaveform(float[] samples, int buckets)
    {
        var result = new float[buckets];
        var frames = samples.Length / 2;
        if (frames == 0)
        {
            return result;
        }

        var perBucket = Math.Max(1, frames / buckets);
        var max = 0f;

        for (var bucket = 0; bucket < buckets; bucket++)
        {
            var start = bucket * perBucket;
            var end = Math.Min(frames, start + perBucket);
            var peak = 0f;

            for (var frame = start; frame < end; frame++)
            {
                var value = Math.Abs(samples[frame * 2]);
                if (value > peak)
                {
                    peak = value;
                }
            }

            result[bucket] = peak;
            if (peak > max)
            {
                max = peak;
            }
        }

        if (max > 0.0001f)
        {
            for (var i = 0; i < buckets; i++)
            {
                result[i] = Math.Clamp(result[i] / max, 0.02f, 1f);
            }
        }

        return result;
    }
}
