using ClipRing.Core;

namespace ClipRing.Capture;

/// <summary>One moof+mdat pair (≈ one GOP) held in RAM. Reference counted so saving can
/// keep using it while the ring buffer already evicted it.</summary>
public sealed class BufferedFragment
{
    private int _refs = 1;

    public BufferedFragment(SegmentedBuffer data, FragmentTiming timing)
    {
        ArrivalUnix = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
        Data = data;
        Start = timing.Start;
        Duration = timing.Duration;
        IsKeyframe = timing.StartsWithKeyframe;
    }

    public SegmentedBuffer Data { get; }
    public double Start { get; }
    public double Duration { get; }
    public double End => Start + Duration;
    public bool IsKeyframe { get; }

    /// <summary>Wall-clock time (Unix seconds) when the fragment came out of ffmpeg.</summary>
    public double ArrivalUnix { get; }

    public void AddRef() => Interlocked.Increment(ref _refs);

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) == 0) Data.Dispose();
    }
}

/// <summary>Frozen view of the last N seconds, safe to write out while recording continues.</summary>
public sealed class ClipSnapshot : IDisposable
{
    private bool _disposed;

    public ClipSnapshot(byte[] init, List<BufferedFragment> fragments)
    {
        Init = init;
        Fragments = fragments;
        Duration = fragments.Count == 0 ? 0 : fragments[^1].End - fragments[0].Start;
        Bytes = init.Length + fragments.Sum(f => f.Data.Length);
    }

    public byte[] Init { get; }
    public IReadOnlyList<BufferedFragment> Fragments { get; }
    public double Duration { get; }
    public long Bytes { get; }

    public async Task WriteToAsync(Stream destination, CancellationToken ct)
    {
        await destination.WriteAsync(Init, ct).ConfigureAwait(false);
        foreach (var fragment in Fragments)
            await fragment.Data.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var f in Fragments) f.Release();
    }
}

/// <summary>
/// The RAM ring buffer: init segment + a sliding window of keyframe-aligned fragments
/// covering at least <see cref="TargetSeconds"/>.
/// </summary>
public sealed class ReplayBuffer
{
    private readonly object _gate = new();
    private readonly List<BufferedFragment> _fragments = new();
    private byte[]? _init;
    private long _bytes;
    private double _targetSeconds = 120;
    private long _version;
    private double _minWallOffset = double.PositiveInfinity;

    /// <summary>Raised after a fragment was added (on the reader thread).</summary>
    public event Action? FragmentAdded;

    public double TargetSeconds
    {
        get { lock (_gate) return _targetSeconds; }
        set { lock (_gate) { _targetSeconds = Math.Max(1, value); TrimLocked(); } }
    }

    /// <summary>Increments with every added fragment; used to wait for "the next one".</summary>
    public long Version => Interlocked.Read(ref _version);

    public void Reset(byte[]? init)
    {
        lock (_gate)
        {
            foreach (var f in _fragments) f.Release();
            _fragments.Clear();
            _bytes = 0;
            _init = init;
            _minWallOffset = double.PositiveInfinity;
        }
    }

    public void Add(BufferedFragment fragment)
    {
        lock (_gate)
        {
            if (_fragments.Count > 0)
            {
                double lastEnd = _fragments[^1].End;
                // Clock jump (e.g. system time adjusted): old fragments can't be joined with new ones.
                if (fragment.Start < lastEnd - 1.0 || fragment.Start > lastEnd + 10.0)
                {
                    Log.Warn($"Zeitsprung im Stream ({lastEnd:F2}s -> {fragment.Start:F2}s), Puffer wird geleert");
                    foreach (var f in _fragments) f.Release();
                    _fragments.Clear();
                    _bytes = 0;
                    _minWallOffset = double.PositiveInfinity;
                }
            }
            _fragments.Add(fragment);
            // Stream time is wall-clock based (constant offset). The smallest "arrival - end" seen so far
            // is that offset plus the minimal pipeline latency: good enough to map a hotkey press into the stream.
            _minWallOffset = Math.Min(_minWallOffset, fragment.ArrivalUnix - fragment.End);
            _bytes += fragment.Data.Length;
            TrimLocked();
        }
        Interlocked.Increment(ref _version);
        FragmentAdded?.Invoke();
    }

    private void TrimLocked()
    {
        while (_fragments.Count > 1)
        {
            double end = _fragments[^1].End;
            int nextKey = -1;
            for (int i = 1; i < _fragments.Count; i++)
            {
                if (_fragments[i].IsKeyframe) { nextKey = i; break; }
            }
            if (nextKey < 0) break;
            // Drop everything before the next keyframe only if the rest still covers the target.
            if (end - _fragments[nextKey].Start < _targetSeconds) break;
            for (int i = 0; i < nextKey; i++)
            {
                _bytes -= _fragments[i].Data.Length;
                _fragments[i].Release();
            }
            _fragments.RemoveRange(0, nextKey);
        }
    }

    public (double Seconds, long Bytes, int Fragments) Stats
    {
        get
        {
            lock (_gate)
            {
                if (_fragments.Count == 0) return (0, 0, 0);
                double seconds = _fragments[^1].End - _fragments[0].Start;
                return (Math.Min(seconds, _targetSeconds + 5), _bytes, _fragments.Count);
            }
        }
    }

    /// <summary>End of the buffered stream (stream seconds), or null when empty.</summary>
    public double? StreamEnd
    {
        get { lock (_gate) return _fragments.Count == 0 ? null : _fragments[^1].End; }
    }

    /// <summary>Maps a wall-clock time to stream time, once at least one fragment arrived.</summary>
    public double? WallToStream(DateTime utc)
    {
        lock (_gate)
        {
            if (double.IsInfinity(_minWallOffset)) return null;
            return (utc - DateTime.UnixEpoch).TotalSeconds - _minWallOffset;
        }
    }

    /// <summary>
    /// Takes (at least) <paramref name="seconds"/> before <paramref name="anchor"/> (stream time of the
    /// hotkey press; null = end of buffer) up to the end of the buffer, starting at a keyframe.
    /// With <paramref name="trimEnd"/> it stops at the fragment (GOP) that contains the anchor.
    /// </summary>
    public ClipSnapshot? Snapshot(double seconds, double? anchor = null, bool trimEnd = false)
    {
        lock (_gate)
        {
            if (_init is null || _fragments.Count == 0) return null;

            double end = _fragments[^1].End;
            double wantedStart = Math.Min(anchor ?? end, end) - seconds;
            int start = -1;
            // Latest keyframe fragment that still lies at or before the wanted start...
            for (int i = _fragments.Count - 1; i >= 0; i--)
            {
                if (_fragments[i].IsKeyframe && _fragments[i].Start <= wantedStart + 0.05) { start = i; break; }
            }
            // ...or, if the buffer is shorter than requested, the earliest keyframe.
            if (start < 0) start = _fragments.FindIndex(f => f.IsKeyframe);
            if (start < 0) return null;

            int stop = _fragments.Count;
            if (trimEnd && anchor is double cut)
            {
                stop = start + 1;
                while (stop < _fragments.Count && _fragments[stop].Start < cut) stop++;
            }
            var list = _fragments.GetRange(start, stop - start);
            foreach (var f in list) f.AddRef();
            return new ClipSnapshot(_init, list);
        }
    }
}
