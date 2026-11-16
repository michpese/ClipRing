using System.Buffers.Binary;
using System.Text;
using ClipRing.Capture;
using Xunit;

namespace ClipRing.Tests;

public class Mp4ParserTests
{
    private static byte[] Box(string type, params byte[][] content)
    {
        int size = 8 + content.Sum(c => c.Length);
        var box = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)size);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        int pos = 8;
        foreach (var c in content) { c.CopyTo(box, pos); pos += c.Length; }
        return box;
    }

    private static byte[] U32(params uint[] values)
    {
        var b = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(i * 4), values[i]);
        return b;
    }

    private static byte[] Init()
    {
        // tkhd v0: version/flags, created, modified, track_ID
        var tkhd = Box("tkhd", U32(0, 0, 0, 1, 0));
        // mdhd v0: version/flags, created, modified, timescale, duration
        var mdhd = Box("mdhd", U32(0, 0, 0, 90000, 0));
        var hdlr = Box("hdlr", U32(0, 0), Encoding.ASCII.GetBytes("vide"), U32(0, 0, 0));
        var trak = Box("trak", tkhd, Box("mdia", mdhd, hdlr));
        // trex: version/flags, track_ID, desc index, default duration, size, flags (non-sync)
        var trex = Box("trex", U32(0, 1, 1, 1500, 0, 0x10000));
        return Concat(Box("ftyp", Encoding.ASCII.GetBytes("isom")), Box("moov", trak, Box("mvex", trex)));
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Moof(uint baseTime, uint samples, uint firstFlags)
    {
        var tfhd = Box("tfhd", U32(0x020000, 1)); // default-base-is-moof, track 1
        var tfdt = Box("tfdt", U32(0, baseTime));
        // trun flags: data offset (0x1) + first sample flags (0x4); per-sample: nothing (defaults)
        var trun = Box("trun", U32(0x000005, samples, 0, firstFlags));
        return Box("moof", Box("mfhd", U32(0, 1)), Box("traf", tfhd, tfdt, trun));
    }

    [Fact]
    public void ReadsTracksAndDefaults()
    {
        var info = Mp4Parser.ParseInit(Init());
        var video = Assert.IsType<Mp4Track>(info.Video);
        Assert.Equal(1u, video.TrackId);
        Assert.Equal(90000u, video.Timescale);
        Assert.Equal(1500u, video.DefaultSampleDuration);
    }

    [Fact]
    public void ReadsFragmentTiming()
    {
        var info = Mp4Parser.ParseInit(Init());
        var moof = Moof(baseTime: 180000, samples: 60, firstFlags: 0x02000000);
        var timing = Mp4Parser.ParseMoof(moof, moof.Length, info);

        Assert.NotNull(timing);
        Assert.Equal(2.0, timing.Value.Start, 6);
        Assert.Equal(1.0, timing.Value.Duration, 6); // 60 × 1500 / 90000
        Assert.True(timing.Value.StartsWithKeyframe);
    }

    [Fact]
    public void TruncatedSampleTableDoesNotThrow()
    {
        var info = Mp4Parser.ParseInit(Init());
        var tfhd = Box("tfhd", U32(0x020000, 1));
        var tfdt = Box("tfdt", U32(0, 0));
        // per-sample duration (0x100) announced for 1000 samples, but only two entries present
        var trun = Box("trun", U32(0x000100, 1000, 1500, 1500));
        var moof = Box("moof", Box("traf", tfhd, tfdt, trun));

        var timing = Mp4Parser.ParseMoof(moof, moof.Length, info);
        Assert.NotNull(timing);
        Assert.Equal(3000 / 90000.0, timing.Value.Duration, 6);
    }

    [Fact]
    public void BrokenInitDoesNotThrow()
    {
        var init = Init();
        var info = Mp4Parser.ParseInit(init.AsSpan(0, init.Length - 10).ToArray());
        Assert.NotNull(info);
    }
}
