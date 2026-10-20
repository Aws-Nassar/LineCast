using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MicDeck.Audio;

/// <summary>One WASAPI playback endpoint fed by a single stereo mix graph.</summary>
public sealed class AudioBus : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiOut _output;
    private readonly MixerProvider _mixer;
    private readonly PeakMeterProvider _meter;
    private bool _disposed;

    public AudioBus(MMDevice device, int latencyMs)
    {
        _device = device;

        var mix = device.AudioClient.MixFormat;
        SampleRate = mix.SampleRate;
        Channels = mix.Channels;

        // Everything upstream is stereo; the device format is only applied at the
        // very end so both buses can run at different rates without duplicating
        // the whole graph.
        _mixer = new MixerProvider(WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2));
        var limiter = new LimiterProvider(_mixer);
        var mapper = new ChannelMapProvider(limiter, Channels);
        _meter = new PeakMeterProvider(mapper);

        _output = new WasapiOut(
            device,
            AudioClientShareMode.Shared,
            true,
            Math.Clamp(latencyMs, 10, 200));
        _output.Init(_meter);
    }

    public int SampleRate { get; }

    public int Channels { get; }

    public bool IsRunning { get; private set; }

    public float Peak => _meter.Peak;

    public int InputCount => _mixer.InputCount;

    public void AddInput(ISampleProvider input) => _mixer.Add(input);

    public void RemoveInput(ISampleProvider input) => _mixer.Remove(input);

    public void Start()
    {
        _output.Play();
        IsRunning = true;
    }

    public void Stop()
    {
        try
        {
            _output.Stop();
        }
        catch
        {
            // Device may already be gone.
        }

        IsRunning = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _output.Dispose();
        }
        catch
        {
            // Ignore teardown races.
        }

        _device.Dispose();
    }
}

/// <summary>Microphone or system-loopback capture feeding an <see cref="AdaptiveBuffer"/>.</summary>
public sealed class CaptureSource : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly CaptureConverter _converter;
    private bool _disposed;

    public CaptureSource(string deviceId, bool loopback, bool downmixToMono, int targetRate, double targetFillMs)
    {
        _device = DeviceCatalog.Open(deviceId);

        _capture = loopback
            ? new WasapiLoopbackCapture(_device)
            : new WasapiCapture(_device);

        var format = _capture.WaveFormat;
        _converter = new CaptureConverter(format, downmixToMono);
        Buffer = new AdaptiveBuffer(format.SampleRate, targetRate, targetFillMs);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public AdaptiveBuffer Buffer { get; }

    public bool IsRunning => _capture.CaptureState == CaptureState.Capturing;

    public int SourceSampleRate => _capture.WaveFormat.SampleRate;

    public int SourceChannels => _capture.WaveFormat.Channels;

    public event Action<Exception>? Faulted;

    public void Start() => _capture.StartRecording();

    public void Stop()
    {
        try
        {
            _capture.StopRecording();
        }
        catch
        {
            // Ignore.
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        // No allocation: the converter owns a grow-only scratch buffer.
        var count = _converter.Convert(e.Buffer, e.BytesRecorded, out var samples);
        if (count > 0)
        {
            Buffer.Write(samples, count);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed || e.Exception is null)
        {
            return;
        }

        Faulted?.Invoke(e.Exception);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;

        try
        {
            _capture.StopRecording();
            _capture.Dispose();
        }
        catch
        {
            // Ignore.
        }

        _device.Dispose();
    }
}
