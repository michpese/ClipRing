using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ClipRing.Core;
using ClipRing.Native;
using NAudio.CoreAudioApi;

namespace ClipRing.Audio;

/// <summary>
/// Clock-driven mixer: produces exactly 48 000 stereo frames per second of wall-clock time,
/// mixing PC audio (WASAPI loopback) and microphone with individual gains. The output is a
/// gap-free float32 PCM stream for ffmpeg's stdin whose first sample has a known wall-clock
/// time, which keeps audio and video in sync (see PipelineBuilder.BuildRecordArgs).
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly object _sourceGate = new();
    private readonly object _outGate = new();
    private readonly object _rebuildGate = new();
    private readonly ManualResetEventSlim _streamAnchored = new(false);
    private readonly MemoryStream _backlog = new();

    private CaptureSource? _system;
    private CaptureSource? _mic;
    private bool _systemEnabled, _micEnabled;
    private string _systemId = "", _micId = "";
    private string _systemDefaultSeen = "", _micDefaultSeen = "";

    private volatile float _systemGain = 1f;
    private volatile float _micGain = 1f;
    private volatile float _systemPeak, _micPeak;

    private PipeWriter? _writer;
    private bool _awaitingAttach;
    private double _streamStartUnix = double.NaN;
    private bool _overflowReported;

    /// <summary>More audio than this waiting for ffmpeg means it stopped reading for good.</summary>
    private const long MaxQueuedBytes = SampleRate * Channels * sizeof(float) * 120L;

    private Thread? _thread;
    private volatile bool _running;
    private Timer? _deviceWatch;

    /// <summary>Raised (on the mixer thread) when the clock jumped and the A/V relation is lost.</summary>
    public event Action? SyncLost;

    public float SystemGain { get => _systemGain; set => _systemGain = value; }
    public float MicGain { get => _micGain; set => _micGain = value; }
    public float SystemPeak => _systemPeak;
    public float MicPeak => _micPeak;
    public string? SystemDeviceName => _system?.Name;
    public string? MicDeviceName => _mic?.Name;

    private static double UnixNow() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(MixLoop) { IsBackground = true, Name = "audio-mixer", Priority = ThreadPriority.Highest };
        _thread.Start();
        _deviceWatch = new Timer(_ => WatchDevices(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>Enables/disables the sources and selects devices ("" = Windows default).</summary>
    public void Configure(bool systemEnabled, string systemDeviceId, bool micEnabled, string micDeviceId)
    {
        lock (_sourceGate)
        {
            _systemEnabled = systemEnabled;
            _systemId = systemDeviceId ?? "";
            _micEnabled = micEnabled;
            _micId = micDeviceId ?? "";
        }
        Rebuild(force: false);
    }

    private void Rebuild(bool force)
    {
        lock (_rebuildGate) RebuildLocked(force);
    }

    private void RebuildLocked(bool force)
    {
        CaptureSource? oldSystem = null, oldMic = null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            bool sysEnabled, micEnabled;
            string sysId, micId;
            lock (_sourceGate)
            {
                sysEnabled = _systemEnabled; micEnabled = _micEnabled;
                sysId = _systemId; micId = _micId;
            }

            CaptureSource? newSystem = BuildSource(enumerator, DataFlow.Render, sysEnabled, sysId, _system, force,
                                                   ref _systemDefaultSeen);
            CaptureSource? newMic = BuildSource(enumerator, DataFlow.Capture, micEnabled, micId, _mic, force,
                                                ref _micDefaultSeen);

            lock (_sourceGate)
            {
                if (!ReferenceEquals(newSystem, _system)) { oldSystem = _system; _system = newSystem; }
                if (!ReferenceEquals(newMic, _mic)) { oldMic = _mic; _mic = newMic; }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audioquellen konnten nicht eingerichtet werden", ex);
        }
        oldSystem?.Dispose();
        oldMic?.Dispose();
    }

    private static CaptureSource? BuildSource(MMDeviceEnumerator enumerator, DataFlow flow, bool enabled, string wantedId,
                                              CaptureSource? current, bool force, ref string defaultSeen)
    {
        if (!enabled) return null;

        string resolvedId = string.IsNullOrEmpty(wantedId) ? AudioDevices.DefaultId(enumerator, flow) : wantedId;
        if (string.IsNullOrEmpty(wantedId)) defaultSeen = resolvedId;

        if (!force && current is not null && !current.IsStopped && current.DeviceId == resolvedId)
            return current; // unchanged

        var device = AudioDevices.Resolve(enumerator, flow, wantedId);
        if (device is null) return null;
        try
        {
            return new CaptureSource(device, loopback: flow == DataFlow.Render);
        }
        catch (Exception ex)
        {
            Log.Error($"Audiogerät '{device.FriendlyName}' konnte nicht geöffnet werden", ex);
            return null;
        }
    }

    /// <summary>Follows default-device switches (e.g. headset plugged in) and recovers lost devices.</summary>
    private void WatchDevices()
    {
        try
        {
            bool needRebuild = false;
            using (var enumerator = new MMDeviceEnumerator())
            {
                lock (_sourceGate)
                {
                    if (_systemEnabled && (_system is null || _system.IsStopped)) needRebuild = true;
                    if (_micEnabled && (_mic is null || _mic.IsStopped)) needRebuild = true;
                }
                if (_systemEnabled && string.IsNullOrEmpty(_systemId) &&
                    AudioDevices.DefaultId(enumerator, DataFlow.Render) != _systemDefaultSeen) needRebuild = true;
                if (_micEnabled && string.IsNullOrEmpty(_micId) &&
                    AudioDevices.DefaultId(enumerator, DataFlow.Capture) != _micDefaultSeen) needRebuild = true;
            }
            if (needRebuild) Rebuild(force: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Geräteüberwachung: " + ex.Message);
        }
    }

    /// <summary>
    /// Starts a new output stream. Returns the wall-clock time (Unix seconds) of its first
    /// sample; samples are buffered until <see cref="AttachOutput"/> provides the pipe.
    /// </summary>
    public double BeginStream()
    {
        lock (_outGate)
        {
            _writer?.Dispose();
            _writer = null;
            _backlog.SetLength(0);
            _streamStartUnix = double.NaN;
            _awaitingAttach = true;
            _streamAnchored.Reset();
        }
        if (!_streamAnchored.Wait(TimeSpan.FromSeconds(2)))
            throw new InvalidOperationException(Localization.Loc.T("Err_MixerStuck"));
        lock (_outGate) return _streamStartUnix;
    }

    public void AttachOutput(Stream output)
    {
        lock (_outGate)
        {
            _writer?.Dispose();
            _writer = new PipeWriter(output);
            _overflowReported = false;
            if (_backlog.Length > 0)
            {
                _writer.Enqueue(_backlog.GetBuffer().AsSpan(0, (int)_backlog.Length));
                _backlog.SetLength(0);
            }
            _awaitingAttach = false;
        }
    }

    /// <summary>Human-readable state of the PCM pipe to ffmpeg, for the log.</summary>
    public string PipeDiagnostics()
    {
        string sources = string.Join(", ", new[] { SystemDeviceName, MicDeviceName }.Where(n => n is not null));
        var writer = _writer;
        string pipe = writer is null ? "Ton an FFmpeg: keine Verbindung" : writer.Describe();
        return pipe + $"; Quellen: {(sources.Length == 0 ? "keine" : sources)}";
    }

    public void DetachOutput()
    {
        lock (_outGate)
        {
            _writer?.Dispose();
            _writer = null;
            _awaitingAttach = false;
            _backlog.SetLength(0);
        }
    }

    private void MixLoop()
    {
        Win32.timeBeginPeriod(1);
        try
        {
            double anchor = UnixNow();
            long produced = 0;
            var mix = new float[SampleRate * Channels];
            var scratch = new float[SampleRate * Channels];

            while (_running)
            {
                double now = UnixNow();
                long target = (long)((now - anchor) * SampleRate);
                long pending = target - produced;

                if (pending > SampleRate * 5 || pending < -SampleRate * 5)
                {
                    // System clock jumped (sleep/resume, manual change): restart the timeline.
                    Log.Warn($"Audio-Uhr sprang um {pending / (double)SampleRate:F1}s, Zeitachse wird neu gesetzt");
                    anchor = now;
                    produced = 0;
                    SyncLost?.Invoke();
                    continue;
                }
                if (pending < SampleRate / 200) // < 5 ms due
                {
                    Thread.Sleep(2);
                    continue;
                }

                int frames = (int)Math.Min(pending, SampleRate / 2);
                double chunkStart = anchor + produced / (double)SampleRate;
                bool keepingUp = frames <= SampleRate / 10; // don't trim latency while catching up

                Array.Clear(mix, 0, frames * Channels);
                float sysPeak = 0, micPeak = 0;
                lock (_sourceGate)
                {
                    if (_system is not null) sysPeak = _system.MixInto(mix, scratch, frames, _systemGain, keepingUp);
                    if (_mic is not null) micPeak = _mic.MixInto(mix, scratch, frames, _micGain, keepingUp);
                }
                for (int i = 0; i < frames * Channels; i++)
                {
                    float v = mix[i];
                    if (v > 1f) mix[i] = 1f;
                    else if (v < -1f) mix[i] = -1f;
                }
                _systemPeak = Math.Max(sysPeak, _systemPeak * 0.8f);
                _micPeak = Math.Max(micPeak, _micPeak * 0.8f);

                var bytes = MemoryMarshal.AsBytes(mix.AsSpan(0, frames * Channels));
                Write(bytes, chunkStart);
                produced += frames;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio-Mixer abgestürzt", ex);
        }
        finally
        {
            Win32.timeEndPeriod(1);
        }
    }

    private void Write(ReadOnlySpan<byte> bytes, double chunkStart)
    {
        lock (_outGate)
        {
            if (_awaitingAttach)
            {
                if (double.IsNaN(_streamStartUnix))
                {
                    _streamStartUnix = chunkStart;
                    _streamAnchored.Set();
                }
                _backlog.Write(bytes);
                if (_backlog.Length > SampleRate * Channels * sizeof(float) * 10L)
                {
                    // Nobody attached within 10 s (ffmpeg failed to start): stop buffering.
                    _awaitingAttach = false;
                    _backlog.SetLength(0);
                }
                return;
            }

            var writer = _writer;
            if (writer is null || writer.Failed) return;
            writer.Enqueue(bytes);
            if (writer.QueuedBytes > MaxQueuedBytes && !_overflowReported)
            {
                // ffmpeg has not read audio for two minutes: something is stuck, start over.
                _overflowReported = true;
                Log.Warn("FFmpeg nimmt seit 2 Minuten keinen Ton mehr an, Aufnahme wird neu gestartet");
                SyncLost?.Invoke();
            }
        }
    }

    /// <summary>
    /// Writes PCM to ffmpeg's stdin on its own thread. ffmpeg stops reading audio whenever the video runs
    /// behind (e.g. Graphics Capture on a static screen delivers no frames); the mixer must keep its
    /// wall-clock pace regardless, so chunks queue up here and are delivered once ffmpeg reads again.
    /// </summary>
    private sealed class PipeWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly BlockingCollection<byte[]> _queue = new();
        private long _queued, _written, _lastWriteTicks, _writeSinceTicks;
        private volatile string? _error;

        public PipeWriter(Stream stream)
        {
            _stream = stream;
            new Thread(Run) { IsBackground = true, Name = "audio-pipe", Priority = ThreadPriority.AboveNormal }.Start();
        }

        public bool Failed => _error is not null;
        public long QueuedBytes => Interlocked.Read(ref _queued);

        public void Enqueue(ReadOnlySpan<byte> data)
        {
            var chunk = data.ToArray();
            Interlocked.Add(ref _queued, chunk.Length);
            try { _queue.Add(chunk); }
            catch (InvalidOperationException) { Interlocked.Add(ref _queued, -chunk.Length); } // disposed
        }

        private void Run()
        {
            try
            {
                foreach (var chunk in _queue.GetConsumingEnumerable())
                {
                    Interlocked.Exchange(ref _writeSinceTicks, DateTime.UtcNow.Ticks);
                    try
                    {
                        _stream.Write(chunk, 0, chunk.Length);
                        _stream.Flush();
                        Interlocked.Add(ref _written, chunk.Length);
                        Interlocked.Exchange(ref _lastWriteTicks, DateTime.UtcNow.Ticks);
                    }
                    finally
                    {
                        Interlocked.Add(ref _queued, -chunk.Length);
                        Interlocked.Exchange(ref _writeSinceTicks, 0);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // ffmpeg went away; the recorder reports that separately.
                _error = ex.Message;
                Log.Info("Ton-Pipe zu FFmpeg beendet: " + ex.Message);
            }
        }

        public string Describe()
        {
            const double bytesPerSecond = SampleRate * Channels * sizeof(float);
            long written = Interlocked.Read(ref _written);
            long last = Interlocked.Read(ref _lastWriteTicks);
            long since = Interlocked.Read(ref _writeSinceTicks);
            long now = DateTime.UtcNow.Ticks;
            var text = $"Ton an FFmpeg: {written / 1024} KB ({written / bytesPerSecond:F1} s)";
            text += last == 0 ? ", noch nie geschrieben" : $", zuletzt vor {TimeSpan.FromTicks(now - last).TotalSeconds:F1} s";
            if (since != 0) text += $", Schreibvorgang wartet seit {TimeSpan.FromTicks(now - since).TotalSeconds:F1} s";
            text += $", wartend {QueuedBytes / bytesPerSecond:F1} s";
            if (_error is not null) text += $", Fehler: {_error}";
            return text;
        }

        public void Dispose()
        {
            // Ends the writer after the current chunk; a write blocked on a dead pipe fails by itself.
            try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        _running = false;
        _deviceWatch?.Dispose();
        _thread?.Join(1000);
        lock (_sourceGate)
        {
            _system?.Dispose();
            _mic?.Dispose();
            _system = null;
            _mic = null;
        }
    }
}
