using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ClipRing.Core;
using ClipRing.Native;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ClipRing.Audio;

/// <summary>
/// Clock-driven mixer: produces exactly 48 000 stereo frames per second of wall-clock time,
/// mixing PC audio (WASAPI loopback) and microphone with individual gains. The output is a
/// gap-free float32 PCM stream for ffmpeg's stdin whose first sample has a known wall-clock
/// time, which keeps audio and video in sync (see PipelineBuilder.BuildRecordArgs).
/// <para>
/// Only active while something needs it (a recording with sound, a visible level meter): when idle, the
/// devices are closed (no microphone indicator in the taskbar), the mixer thread is gone and the 1 ms timer
/// resolution is released.
/// </para>
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    /// <summary>
    /// Channels of the stream with separate tracks: mix L/R (clamped like the normal stream), PC audio L/R,
    /// microphone L/R. FFmpeg splits them into three stereo tracks.
    /// </summary>
    public const int SeparateChannels = 6;

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
    private volatile bool _separate;
    private bool _awaitingAttach;
    private double _streamStartUnix = double.NaN;
    private bool _overflowReported;

    /// <summary>More audio than this waiting for ffmpeg means it stopped reading for good (two minutes).</summary>
    private long MaxQueuedBytes => SampleRate * (_separate ? SeparateChannels : Channels) * sizeof(float) * 120L;

    private readonly object _lifeGate = new();
    private Thread? _thread;
    private volatile bool _running;
    private Timer? _deviceWatch;
    private MMDeviceEnumerator? _notifyEnumerator;
    private DeviceNotifications? _notifications;

    /// <summary>Raised (on the mixer thread) when the clock jumped and the A/V relation is lost.</summary>
    public event Action? SyncLost;

    public float SystemGain { get => _systemGain; set => _systemGain = value; }
    public float MicGain { get => _micGain; set => _micGain = value; }
    public float SystemPeak => _systemPeak;
    public float MicPeak => _micPeak;
    public string? SystemDeviceName => _system?.Name;
    public string? MicDeviceName => _mic?.Name;

    private static double UnixNow() => (DateTime.UtcNow - DateTime.UnixEpoch).Ticks / (double)TimeSpan.TicksPerSecond;

    public bool IsActive => _running;

    /// <summary>Opens the devices and starts the mixer, or closes everything again.</summary>
    public void SetActive(bool active)
    {
        lock (_lifeGate)
        {
            if (active == _running) return;
            if (active)
            {
                _running = true;
                Rebuild(force: false);
                _thread = new Thread(MixLoop) { IsBackground = true, Name = "audio-mixer", Priority = ThreadPriority.Highest };
                _thread.Start();
                StartDeviceWatch();
                Log.Info("Tonaufnahme aktiv");
            }
            else
            {
                _running = false;
                StopDeviceWatch();
                _thread?.Join(1000);
                _thread = null;
                DetachOutput();
                Rebuild(force: false); // closes the devices while not running
                _systemPeak = 0;
                _micPeak = 0;
                Log.Info("Tonaufnahme ruht, Geräte geschlossen");
            }
        }
    }

    /// <summary>
    /// Default-device switches (headset plugged in) arrive as notifications; the slow timer only recovers
    /// devices that stopped without one.
    /// </summary>
    private void StartDeviceWatch()
    {
        _deviceWatch = new Timer(_ => WatchDevices(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        try
        {
            _notifyEnumerator = new MMDeviceEnumerator();
            _notifications = new DeviceNotifications(() => ThreadPool.QueueUserWorkItem(_ => WatchDevices()));
            _notifyEnumerator.RegisterEndpointNotificationCallback(_notifications);
        }
        catch (Exception ex)
        {
            Log.Warn("Benachrichtigungen über Audiogeräte nicht verfügbar: " + ex.Message);
            _notifications = null;
        }
    }

    private void StopDeviceWatch()
    {
        _deviceWatch?.Dispose();
        _deviceWatch = null;
        try
        {
            if (_notifications is not null) _notifyEnumerator?.UnregisterEndpointNotificationCallback(_notifications);
        }
        catch { /* enumerator already gone */ }
        _notifications = null;
        _notifyEnumerator?.Dispose();
        _notifyEnumerator = null;
    }

    private sealed class DeviceNotifications(Action changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => changed();
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) => changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => changed();
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
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
        if (_running) Rebuild(force: false);
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
                // Not running: no device stays open.
                sysEnabled = _systemEnabled && _running; micEnabled = _micEnabled && _running;
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
        string name = device.FriendlyName;
        try
        {
            return new CaptureSource(device, loopback: flow == DataFlow.Render); // owns the device from here on
        }
        catch (Exception ex)
        {
            Log.Error($"Audiogerät '{name}' konnte nicht geöffnet werden", ex);
            return null;
        }
    }

    /// <summary>Follows default-device switches (e.g. headset plugged in) and recovers lost devices.</summary>
    private void WatchDevices()
    {
        if (!_running) return;
        try
        {
            bool needRebuild = false;
            bool sysEnabled, micEnabled;
            string sysId, micId;
            lock (_sourceGate)
            {
                sysEnabled = _systemEnabled;
                sysId = _systemId;
                micEnabled = _micEnabled;
                micId = _micId;
                if (sysEnabled && (_system is null || _system.IsStopped)) needRebuild = true;
                if (micEnabled && (_mic is null || _mic.IsStopped)) needRebuild = true;
            }
            if (!needRebuild && ((sysEnabled && string.IsNullOrEmpty(sysId)) || (micEnabled && string.IsNullOrEmpty(micId))))
            {
                using var enumerator = new MMDeviceEnumerator();
                if (sysEnabled && string.IsNullOrEmpty(sysId) &&
                    AudioDevices.DefaultId(enumerator, DataFlow.Render) != Volatile.Read(ref _systemDefaultSeen))
                    needRebuild = true;
                if (micEnabled && string.IsNullOrEmpty(micId) &&
                    AudioDevices.DefaultId(enumerator, DataFlow.Capture) != Volatile.Read(ref _micDefaultSeen))
                    needRebuild = true;
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
    /// <param name="separateTracks">Six channels (mix, PC audio, microphone) instead of the stereo mix.</param>
    public double BeginStream(bool separateTracks = false)
    {
        lock (_outGate)
        {
            _separate = separateTracks;
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
            _writer = new PipeWriter(output, _separate ? SeparateChannels : Channels);
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
            var systemOnly = new float[SampleRate * Channels];
            var micOnly = new float[SampleRate * Channels];
            var wide = new float[SampleRate * SeparateChannels];

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

                bool separate = _separate;
                int samples = frames * Channels;
                Array.Clear(systemOnly, 0, samples);
                Array.Clear(micOnly, 0, samples);
                float sysPeak = 0, micPeak = 0;
                lock (_sourceGate)
                {
                    if (_system is not null) sysPeak = _system.MixInto(systemOnly, scratch, frames, _systemGain, keepingUp);
                    if (_mic is not null) micPeak = _mic.MixInto(micOnly, scratch, frames, _micGain, keepingUp);
                }
                for (int i = 0; i < samples; i++) mix[i] = Clamp(systemOnly[i] + micOnly[i]);
                float decayedSys = Math.Max(sysPeak, _systemPeak * 0.8f);
                float decayedMic = Math.Max(micPeak, _micPeak * 0.8f);
                _systemPeak = decayedSys < 1e-5f ? 0f : decayedSys;
                _micPeak = decayedMic < 1e-5f ? 0f : decayedMic;

                ReadOnlySpan<byte> bytes;
                if (separate)
                {
                    for (int f = 0; f < frames; f++)
                    {
                        int i = f * Channels, o = f * SeparateChannels;
                        wide[o] = mix[i];
                        wide[o + 1] = mix[i + 1];
                        wide[o + 2] = Clamp(systemOnly[i]);
                        wide[o + 3] = Clamp(systemOnly[i + 1]);
                        wide[o + 4] = Clamp(micOnly[i]);
                        wide[o + 5] = Clamp(micOnly[i + 1]);
                    }
                    bytes = MemoryMarshal.AsBytes(wide.AsSpan(0, frames * SeparateChannels));
                }
                else
                {
                    bytes = MemoryMarshal.AsBytes(mix.AsSpan(0, samples));
                }
                Write(bytes, chunkStart, separate);
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

    private static float Clamp(float v) => v > 1f ? 1f : v < -1f ? -1f : v;

    private void Write(ReadOnlySpan<byte> bytes, double chunkStart, bool separate)
    {
        bool notifyOverflow = false;
        lock (_outGate)
        {
            // Mixed just before a new stream switched the channel layout: doesn't fit that stream.
            if (separate != _separate) return;
            if (_awaitingAttach)
            {
                if (double.IsNaN(_streamStartUnix))
                {
                    _streamStartUnix = chunkStart;
                    _streamAnchored.Set();
                }
                _backlog.Write(bytes);
                long maxBacklogBytes = SampleRate * (separate ? SeparateChannels : Channels) * sizeof(float) * 10L;
                if (_backlog.Length > maxBacklogBytes)
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
                notifyOverflow = true;
            }
        }

        if (notifyOverflow)
        {
            Log.Warn("FFmpeg nimmt seit 2 Minuten keinen Ton mehr an, Aufnahme wird neu gestartet");
            SyncLost?.Invoke();
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
        private readonly int _channels;
        private readonly BlockingCollection<(byte[] Chunk, int Length)> _queue = new();
        private long _queued, _written, _lastWriteTicks, _writeSinceTicks;
        private volatile string? _error;

        public PipeWriter(Stream stream, int channels)
        {
            _stream = stream;
            _channels = channels;
            new Thread(Run) { IsBackground = true, Name = "audio-pipe", Priority = ThreadPriority.AboveNormal }.Start();
        }

        public bool Failed => _error is not null;
        public long QueuedBytes => Interlocked.Read(ref _queued);

        /// <summary>Copies the chunk into a pooled array (the mixer calls this ~200 times a second).</summary>
        public void Enqueue(ReadOnlySpan<byte> data)
        {
            var chunk = ArrayPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(chunk);
            Interlocked.Add(ref _queued, data.Length);
            try
            {
                _queue.Add((chunk, data.Length));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) // writer ended
            {
                Interlocked.Add(ref _queued, -data.Length);
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        private void Run()
        {
            try
            {
                foreach (var (chunk, length) in _queue.GetConsumingEnumerable())
                {
                    Interlocked.Exchange(ref _writeSinceTicks, DateTime.UtcNow.Ticks);
                    try
                    {
                        _stream.Write(chunk, 0, length);
                        _stream.Flush();
                        Interlocked.Add(ref _written, length);
                        Interlocked.Exchange(ref _lastWriteTicks, DateTime.UtcNow.Ticks);
                    }
                    finally
                    {
                        Interlocked.Add(ref _queued, -length);
                        Interlocked.Exchange(ref _writeSinceTicks, 0);
                        ArrayPool<byte>.Shared.Return(chunk);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // ffmpeg went away; the recorder reports that separately.
                _error = ex.Message;
                Log.Info("Ton-Pipe zu FFmpeg beendet: " + ex.Message);
            }
            finally
            {
                // The queue belongs to this thread; it ends only after CompleteAdding or a dead pipe.
                try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }
                while (_queue.TryTake(out var leftover))
                {
                    Interlocked.Add(ref _queued, -leftover.Length);
                    ArrayPool<byte>.Shared.Return(leftover.Chunk);
                }
                _queue.Dispose();
            }
        }

        public string Describe()
        {
            double bytesPerSecond = SampleRate * _channels * sizeof(float);
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
        SetActive(false);
        lock (_outGate)
        {
            _writer?.Dispose();
            _writer = null;
        }
        _streamAnchored.Dispose();
        _backlog.Dispose();
    }
}
