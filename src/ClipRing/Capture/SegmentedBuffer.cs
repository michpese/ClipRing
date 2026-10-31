using System.Collections.Concurrent;

namespace ClipRing.Capture;

/// <summary>
/// Pool of fixed-size 256 KB blocks. The replay buffer constantly frees old fragments and
/// stores new ones; recycling blocks keeps the GC (and the Large Object Heap) out of it.
/// </summary>
public static class BlockPool
{
    public const int BlockSize = 256 * 1024;
    private const int MaxPooledBlocks = 256; // 64 MB of spare blocks at most

    private static readonly ConcurrentBag<byte[]> Free = new();
    private static int _freeCount;

    public static byte[] Rent()
    {
        if (Free.TryTake(out var block))
        {
            Interlocked.Decrement(ref _freeCount);
            return block;
        }
        return GC.AllocateUninitializedArray<byte>(BlockSize);
    }

    /// <summary>Drops the spare blocks, so their memory can go back to Windows.</summary>
    public static void Clear()
    {
        while (Free.TryTake(out _)) Interlocked.Decrement(ref _freeCount);
    }

    /// <summary>
    /// After the buffer was emptied (pause, waiting for a game, restart): the freed blocks sit on the
    /// Large Object Heap, which the GC only cleans in a full collection, and an idle app allocates nothing
    /// that would trigger one. Without this the memory of a full buffer stayed with the app for good.
    /// </summary>
    public static void ReleaseMemory()
    {
        long before = Environment.WorkingSet;
        Clear();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        long after = Environment.WorkingSet;
        if (before - after > 16L * 1024 * 1024)
            ClipRing.Core.Log.Info($"Speicher an Windows zurückgegeben: {before / 1048576} MB -> {after / 1048576} MB");
    }

    public static void Return(byte[] block)
    {
        if (block.Length != BlockSize) return;
        if (Interlocked.Increment(ref _freeCount) <= MaxPooledBlocks)
            Free.Add(block);
        else
            Interlocked.Decrement(ref _freeCount);
    }
}

/// <summary>Append-only byte storage made of pooled blocks.</summary>
public sealed class SegmentedBuffer : IDisposable
{
    private readonly List<byte[]> _blocks = new();
    private long _length;
    private bool _disposed;

    public long Length => _length;

    private byte[] CurrentBlock()
    {
        int index = (int)(_length / BlockPool.BlockSize);
        if (index == _blocks.Count) _blocks.Add(BlockPool.Rent());
        return _blocks[index];
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!data.IsEmpty)
        {
            var block = CurrentBlock();
            int offset = (int)(_length % BlockPool.BlockSize);
            int n = Math.Min(BlockPool.BlockSize - offset, data.Length);
            data[..n].CopyTo(block.AsSpan(offset, n));
            data = data[n..];
            _length += n;
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes from the stream into the buffer.</summary>
    public void ReadFrom(Stream stream, long count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (count > 0)
        {
            var block = CurrentBlock();
            int offset = (int)(_length % BlockPool.BlockSize);
            int want = (int)Math.Min(BlockPool.BlockSize - offset, count);
            int n = stream.Read(block, offset, want);
            if (n <= 0) throw new EndOfStreamException();
            _length += n;
            count -= n;
        }
    }

    public async Task CopyToAsync(Stream destination, CancellationToken ct)
    {
        long remaining = _length;
        foreach (var block in _blocks)
        {
            if (remaining <= 0) break;
            int n = (int)Math.Min(BlockPool.BlockSize, remaining);
            await destination.WriteAsync(block.AsMemory(0, n), ct).ConfigureAwait(false);
            remaining -= n;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var block in _blocks) BlockPool.Return(block);
        _blocks.Clear();
        _length = 0;
    }
}
