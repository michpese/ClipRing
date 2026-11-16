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

    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _provider;
    private readonly byte[] _discard = new byte[16384];
    private volatile bool _stopped;

    /// <summary>Takes ownership of <paramref name="device"/>, also if opening it fails.</summary>
    public CaptureSource(MMDevice device, bool loopback)
    {
        _device = device;
        try
        {
            DeviceId = device.ID;
            Name = device.FriendlyName;
            _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 20);
        }
        catch
        {
            device.Dispose();
            throw;
        }

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
            provider = new DownmixToStereoSampleProvider(provider, ChannelMask(_capture.WaveFormat));
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
        try
        {
            _capture.StartRecording();
        }
        catch
        {
            Dispose(); // device in exclusive use, gone meanwhile, …
            throw;
        }
        Log.Info($"Audioquelle gestartet: {Name} ({(loopback ? "Wiedergabe" : "Mikrofon")}, {format})");
    }

    /// <summary>Speaker positions of the channels (WAVE_FORMAT_EXTENSIBLE), 0 if the format has none.</summary>
    private static int ChannelMask(WaveFormat format)
    {
        if (format is not WaveFormatExtensible) return 0;
        // NAudio keeps dwChannelMask private.
        var field = typeof(WaveFormatExtensible).GetField("dwChannelMask",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(format) is int mask ? mask : 0;
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
        try { _device.Dispose(); } catch { }
    }
}

/// <summary>
/// Downmixes 3+ channels to stereo by speaker position. The positions come from the device format's channel
/// mask (channels are ordered by mask bit); without one, the Windows default layout for that channel count
/// is assumed (quad = FL FR BL BR, 5.0 = FL FR FC BL BR, 5.1 = FL FR FC LFE BL BR, 7.1 adds SL SR).
/// </summary>
internal sealed class DownmixToStereoSampleProvider : ISampleProvider
{
    // KSAUDIO speaker bits
    private const int FL = 0x1, FR = 0x2, FC = 0x4, LFE = 0x8, BL = 0x10, BR = 0x20, FLC = 0x40, FRC = 0x80,
                      BC = 0x100, SL = 0x200, SR = 0x400, TC = 0x800, TFL = 0x1000, TFC = 0x2000, TFR = 0x4000,
                      TBL = 0x8000, TBC = 0x10000, TBR = 0x20000;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private readonly float[] _left;
    private readonly float[] _right;
    private float[] _scratch = Array.Empty<float>();

    public DownmixToStereoSampleProvider(ISampleProvider source, int channelMask = 0)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        (_left, _right) = Weights(_channels, channelMask);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>Per-channel gains for the left and right output, normalized so a full mix rarely clips.</summary>
    internal static (float[] Left, float[] Right) Weights(int channels, int channelMask)
    {
        var positions = Positions(channels, channelMask);
        var left = new float[channels];
        var right = new float[channels];
        const float c = 0.7071f;
        for (int ch = 0; ch < channels; ch++)
        {
            (left[ch], right[ch]) = positions[ch] switch
            {
                FL => (1f, 0f),
                FR => (0f, 1f),
                FC or TC or TFC => (c, c),
                LFE => (0f, 0f), // games put nothing essential there; mixed in it only muddies the sound
                BL or SL or TBL or TFL => (c, 0f),
                BR or SR or TBR or TFR => (0f, c),
                FLC => (0.9f, 0.3f),
                FRC => (0.3f, 0.9f),
                BC or TBC => (0.5f, 0.5f),
                _ => (0.5f, 0.5f), // unknown position: keep it audible in the middle
            };
        }
        // Same loudness as the old fixed factor for 5.1 (sum 2.41 -> 0.55), louder for fewer channels.
        float sum = Math.Max(left.Sum(), right.Sum());
        float norm = Math.Clamp(1f / (sum * 0.75f), 0.3f, 1f);
        for (int ch = 0; ch < channels; ch++)
        {
            left[ch] *= norm;
            right[ch] *= norm;
        }
        return (left, right);
    }

    /// <summary>Speaker bit of every channel, in channel order.</summary>
    internal static int[] Positions(int channels, int channelMask)
    {
        int mask = channelMask;
        if (System.Numerics.BitOperations.PopCount((uint)mask) != channels)
        {
            mask = channels switch
            {
                3 => FL | FR | FC,
                4 => FL | FR | BL | BR,
                5 => FL | FR | FC | BL | BR,
                6 => FL | FR | FC | LFE | BL | BR,
                7 => FL | FR | FC | LFE | BC | SL | SR,
                8 => FL | FR | FC | LFE | BL | BR | SL | SR,
                _ => 0,
            };
        }
        var positions = new int[channels];
        int ch = 0;
        for (int bit = 0; bit < 31 && ch < channels; bit++)
        {
            if ((mask & (1 << bit)) != 0) positions[ch++] = 1 << bit;
        }
        // More channels than known positions: the rest stay 0 ("unknown").
        return positions;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / 2;
        int needed = frames * _channels;
        if (_scratch.Length < needed) _scratch = new float[needed];
        int read = _source.Read(_scratch, 0, needed);
        int framesRead = read / _channels;

        for (int f = 0; f < framesRead; f++)
        {
            int i = f * _channels;
            float l = 0, r = 0;
            for (int ch = 0; ch < _channels; ch++)
            {
                float v = _scratch[i + ch];
                l += v * _left[ch];
                r += v * _right[ch];
            }
            buffer[offset + f * 2] = l;
            buffer[offset + f * 2 + 1] = r;
        }
        return framesRead * 2;
    }
}
