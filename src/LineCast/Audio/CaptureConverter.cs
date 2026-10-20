using NAudio.Wave;

namespace LineCast.Audio;

/// <summary>
/// Converts a WASAPI capture buffer (PCM16/24/32 or IEEE float) into interleaved
/// stereo float. Buffers are reused so the audio callback never allocates.
/// </summary>
public sealed class CaptureConverter
{
    private static readonly Guid FloatSubFormat =
        new("00000003-0000-0010-8000-00AA00389B71");

    private readonly int _sourceChannels;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;
    private readonly bool _downmixToMono;

    private float[] _output = Array.Empty<float>();

    public CaptureConverter(WaveFormat format, bool downmixToMono)
    {
        _sourceChannels = Math.Max(1, format.Channels);
        _bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        _downmixToMono = downmixToMono;
        _isFloat = IsFloat(format);
        BlockAlign = Math.Max(1, format.BlockAlign);
    }

    public int BlockAlign { get; }

    private static bool IsFloat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            return true;
        }

        if (format.Encoding == WaveFormatEncoding.Extensible &&
            format is WaveFormatExtensible extensible)
        {
            return extensible.SubFormat == FloatSubFormat;
        }

        return false;
    }

    /// <summary>Convert and return the number of valid samples in <paramref name="samples"/>.</summary>
    public int Convert(byte[] data, int byteCount, out float[] samples)
    {
        var frames = byteCount / BlockAlign;
        var needed = frames * 2;

        if (_output.Length < needed)
        {
            _output = new float[Math.Max(needed, 4096)];
        }

        samples = _output;

        if (frames == 0)
        {
            return 0;
        }

        var bytes = _bytesPerSample;
        var channels = _sourceChannels;

        for (var frame = 0; frame < frames; frame++)
        {
            var baseIndex = frame * BlockAlign;

            if (_isFloat && bytes == 4)
            {
                if (channels == 1 || _downmixToMono)
                {
                    var sum = 0f;
                    for (var c = 0; c < channels; c++)
                    {
                        sum += BitConverter.ToSingle(data, baseIndex + c * 4);
                    }

                    var value = channels > 1 ? sum / channels : sum;
                    _output[frame * 2] = value;
                    _output[frame * 2 + 1] = value;
                }
                else
                {
                    _output[frame * 2] = BitConverter.ToSingle(data, baseIndex);
                    _output[frame * 2 + 1] = BitConverter.ToSingle(data, baseIndex + 4);
                }

                continue;
            }

            float Sample(int channel)
            {
                var at = baseIndex + channel * bytes;
                return bytes switch
                {
                    2 => BitConverter.ToInt16(data, at) / 32768f,
                    3 => ((data[at] | (data[at + 1] << 8) | (sbyte)data[at + 2] << 16)) / 8388608f,
                    4 => BitConverter.ToInt32(data, at) / 2147483648f,
                    _ => 0f,
                };
            }

            if (channels == 1 || _downmixToMono)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                {
                    sum += Sample(c);
                }

                var value = channels > 1 ? sum / channels : sum;
                _output[frame * 2] = value;
                _output[frame * 2 + 1] = value;
            }
            else
            {
                _output[frame * 2] = Sample(0);
                _output[frame * 2 + 1] = Sample(1);
            }
        }

        return needed;
    }
}
