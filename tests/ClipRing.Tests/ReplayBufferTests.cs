using ClipRing.Capture;
using Xunit;

namespace ClipRing.Tests;

public class ReplayBufferTests
{
    private static BufferedFragment Fragment(double start, double duration = 1.0, bool key = true)
    {
        var data = new SegmentedBuffer();
        data.Write(new byte[1000]);
        return new BufferedFragment(data, new FragmentTiming(start, duration, key));
    }

    private static ReplayBuffer Filled(int seconds, double target = 120)
    {
        var buffer = new ReplayBuffer { TargetSeconds = target };
        buffer.Reset(new byte[] { 1, 2, 3 });
        for (int i = 0; i < seconds; i++) buffer.Add(Fragment(i));
        return buffer;
    }

    [Fact]
    public void KeepsAboutTheTargetLength()
    {
        var buffer = Filled(200, target: 120);
        var (seconds, _, _) = buffer.Stats;
        Assert.InRange(seconds, 120, 121);
    }

    [Fact]
    public void SnapshotStartsAtAKeyframeBeforeTheWantedTime()
    {
        var buffer = new ReplayBuffer { TargetSeconds = 120 };
        buffer.Reset(new byte[] { 1 });
        for (int i = 0; i < 30; i++) buffer.Add(Fragment(i, key: i % 5 == 0));

        using var snapshot = buffer.Snapshot(12);
        Assert.NotNull(snapshot);
        Assert.True(snapshot.Fragments[0].IsKeyframe);
        Assert.True(snapshot.Duration >= 12);
        Assert.Equal(30, snapshot.End, 6);
    }

    [Fact]
    public void TrimmedSnapshotEndsAtTheAnchor()
    {
        var buffer = Filled(60);
        using var snapshot = buffer.Snapshot(10, anchor: 40.5, trimEnd: true);
        Assert.NotNull(snapshot);
        Assert.Equal(41, snapshot.End, 6); // the fragment containing the anchor is the last one
    }

    [Fact]
    public void DiscardThroughStartsOverAtAKeyframe()
    {
        var buffer = new ReplayBuffer { TargetSeconds = 120 };
        buffer.Reset(new byte[] { 1 });
        for (int i = 0; i < 20; i++) buffer.Add(Fragment(i, key: i % 4 == 0));

        buffer.DiscardThrough(9.5); // inside the fragment 9..10, next keyframe at 12
        using var snapshot = buffer.Snapshot(120);
        Assert.NotNull(snapshot);
        Assert.Equal(12, snapshot.Fragments[0].Start, 6);
        Assert.True(snapshot.Fragments[0].IsKeyframe);
    }

    [Fact]
    public void DiscardEverythingLeavesAnEmptyBuffer()
    {
        var buffer = Filled(10);
        buffer.DiscardThrough(10);
        Assert.Equal(0, buffer.Stats.Fragments);
        Assert.Null(buffer.Snapshot(5));
    }
}
