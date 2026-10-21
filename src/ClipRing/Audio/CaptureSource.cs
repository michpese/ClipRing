using ClipRing.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ClipRing.Audio;

/// <summary>
/// One WASAPI capture (loopback of an output device, or a microphone) converted to
/// 48 kHz stereo float and buffered for the mixer.
/// </summary>
internal sealed class CaptureSource : IDisposable
{
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _provider;
    private readonly byte[] _discard = new byte[16384];
    private volatile bool _stopped;

    public CaptureSource(MMDevice device, bool loopback)
    {
        DeviceId = device.ID;
        Name = device.FriendlyName;
        _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 20);

        var format = Normalize(_capture.WaveFormat);
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };

        ISampleProvider provider = _buffer.ToSampleProvider();
        if (provider.WaveFormat.Channels == 1)
            provider = new MonoToStereoSampleProvider(provider);
        else if (provider.WaveFormat.Channels > 2)
            provider = new DownmixToStereoSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != AudioEngine.SampleRate)
            provider = new WdlResamplingSampleProvider(provider, AudioEngine.SampleRate);
        _provider = provider;

        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0) _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };
        _capture.RecordingStopped += (_, e) =>
        {
            _stopped = true;
            if (e.Exception is not null) Log.Warn($"Audioaufnahme '{Name}' beendet: {e.Exception.Message}");
        };
        _capture.StartRecording();
        Log.Info($"Audioquelle gestartet: {Name} ({(loopback ? "Wiedergabe" : "Mikrofon")}, {format})");
    }

    public string DeviceId { get; }
    public string Name { get; }

    /// <summary>True if the device went away (unplugged, disabled) and the source should be rebuilt.</summary>
    public bool IsStopped => _stopped;

    private static WaveFormat Normalize(WaveFormat format)
    {
        // WASAPI reports WAVE_FORMAT_EXTENSIBLE; NAudio's converters want plain PCM/float.
        if (format is WaveFormatExtensible ext)
        {
            if (ext.SubFormat == SubtypeIeeeFloat)
                return WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
            if (ext.SubFormat == SubtypePcm)
                return new WaveFormat(format.SampleRate, format.BitsPerSample, format.Channels);
        }
        return format;
    }

    /// <summary>
    /// Adds <paramref name="frames"/> stereo frames (gain applied) into <paramref name="mix"/>.
    /// Missing data (underrun, silence on loopback) stays zero. Returns the peak level.
    /// </summary>
    public float MixInto(float[] mix, float[] scratch, int frames, float gain, bool allowDriftCorrection)
    {
        if (allowDriftCorrection) CorrectDrift();

        int needed = frames * 2;
        int got = 0;
        while (got < needed)
        {
            int n = _provider.Read(scratch, got, needed - got);
            if (n <= 0) break;
            got += n;
        }

        float peak = 0;
        for (int i = 0; i < got; i++)
        {
            float v = scratch[i] * gain;
            mix[i] += v;
            float a = Math.Abs(v);
            if (a > peak) peak = a;
        }
        return peak;
    }

    /// <summary>
    /// The device clock and the wall clock drift apart slowly. If more than 150 ms are queued,
    /// drop the oldest audio so latency stays low (a tiny, rare skip instead of growing delay).
    /// </summary>
    private void CorrectDrift()
    {
        var wf = _buffer.WaveFormat;
        int bytesPerMs = wf.AverageBytesPerSecond / 1000;
        if (_buffer.BufferedBytes <= bytesPerMs * 150) return;

        int drop = _buffer.BufferedBytes - bytesPerMs * 40;
        drop -= drop % wf.BlockAlign;
        while (drop > 0)
        {
            int n = _buffer.Read(_discard, 0, Math.Min(_discard.Length - _discard.Length % wf.BlockAlign, drop));
            if (n <= 0) break;
            drop -= n;
        }
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch { }
    }
}

/// <summary>Downmixes 3+ channels (5.1/7.1 order FL FR FC LFE BL BR SL SR) to stereo.</summary>
internal sealed class DownmixToStereoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _scratch = Array.Empty<float>();

    public DownmixToStereoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        int needed = frames * _channels;
        if (_scratch.Length < needed) _scratch = new float[needed];
        int read = _source.Read(_scratch, 0, needed);
        int framesRead = read / _channels;

        const float c = 0.7071f;
        const float norm = 0.55f; // keeps the sum of up to 4 contributions out of clipping
        for (int f = 0; f < framesRead; f++)
        {
            int i = f * _channels;
            float l = _scratch[i];
            float r = _scratch[i + 1];
            if (_channels > 2) { float center = _scratch[i + 2] * c; l += center; r += center; }
            if (_channels > 5) { l += _scratch[i + 4] * c; r += _scratch[i + 5] * c; }
            if (_channels > 7) { l += _scratch[i + 6] * c; r += _scratch[i + 7] * c; }
            buffer[offset + f * 2] = l * norm;
            buffer[offset + f * 2 + 1] = r * norm;
        }
        return framesRead * 2;
    }
}
