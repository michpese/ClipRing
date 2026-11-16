using System.Buffers.Binary;
using System.Text;

namespace ClipRing.Capture;

public sealed class Mp4Track
{
    public uint TrackId { get; init; }
    public uint Timescale { get; init; }
    public string Handler { get; init; } = "";
    public uint DefaultSampleDuration { get; set; }
    public uint DefaultSampleFlags { get; set; }
}

public sealed class Mp4InitInfo
{
    public List<Mp4Track> Tracks { get; } = new();
    public Mp4Track? Video => Tracks.FirstOrDefault(t => t.Handler == "vide");
    public Mp4Track? Find(uint id) => Tracks.FirstOrDefault(t => t.TrackId == id);
}

public readonly record struct FragmentTiming(double Start, double Duration, bool StartsWithKeyframe);

/// <summary>Minimal ISO-BMFF parsing for ffmpeg's fragmented MP4 output (ftyp+moov, then moof+mdat pairs).</summary>
public static class Mp4Parser
{
    public static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4));
    public static ulong U64(byte[] b, int o) => BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(o, 8));
    public static uint U24(byte[] b, int o) => (uint)(b[o] << 16 | b[o + 1] << 8 | b[o + 2]);

    /// <summary>Are there <paramref name="bytes"/> bytes at <paramref name="pos"/> before <paramref name="end"/>?</summary>
    private static bool Fits(int pos, int bytes, int end) => pos >= 0 && (long)pos + bytes <= end;

    /// <summary>Enumerates child boxes in [start, end): (type, payloadStart, boxEnd).</summary>
    public static IEnumerable<(string Type, int Payload, int End)> Children(byte[] buf, int start, int end)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            long size = U32(buf, pos);
            string type = Encoding.ASCII.GetString(buf, pos + 4, 4);
            int header = 8;
            if (size == 1)
            {
                if (pos + 16 > end) yield break;
                size = (long)U64(buf, pos + 8);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < header || pos + size > end) yield break;
            yield return (type, pos + header, (int)(pos + size));
            pos += (int)size;
        }
    }

    public static Mp4InitInfo ParseInit(byte[] init)
    {
        var info = new Mp4InitInfo();
        foreach (var top in Children(init, 0, init.Length))
        {
            if (top.Type != "moov") continue;
            foreach (var box in Children(init, top.Payload, top.End))
            {
                if (box.Type == "trak") ParseTrak(init, box.Payload, box.End, info);
            }
            foreach (var box in Children(init, top.Payload, top.End))
            {
                if (box.Type != "mvex") continue;
                foreach (var trex in Children(init, box.Payload, box.End))
                {
                    if (trex.Type != "trex" || !Fits(trex.Payload, 24, trex.End)) continue;
                    // version/flags(4) track_ID(4) desc_index(4) duration(4) size(4) flags(4)
                    var track = info.Find(U32(init, trex.Payload + 4));
                    if (track is null) continue;
                    track.DefaultSampleDuration = U32(init, trex.Payload + 12);
                    track.DefaultSampleFlags = U32(init, trex.Payload + 20);
                }
            }
        }
        return info;
    }

    private static void ParseTrak(byte[] b, int start, int end, Mp4InitInfo info)
    {
        uint trackId = 0, timescale = 0;
        string handler = "";
        foreach (var box in Children(b, start, end))
        {
            if (box.Type == "tkhd" && Fits(box.Payload, 1, box.End))
            {
                int offset = b[box.Payload] == 1 ? 20 : 12;
                if (Fits(box.Payload + offset, 4, box.End)) trackId = U32(b, box.Payload + offset);
            }
            else if (box.Type == "mdia")
            {
                foreach (var m in Children(b, box.Payload, box.End))
                {
                    if (m.Type == "mdhd" && Fits(m.Payload, 1, m.End))
                    {
                        int offset = b[m.Payload] == 1 ? 20 : 12;
                        if (Fits(m.Payload + offset, 4, m.End)) timescale = U32(b, m.Payload + offset);
                    }
                    else if (m.Type == "hdlr" && Fits(m.Payload, 12, m.End))
                    {
                        handler = Encoding.ASCII.GetString(b, m.Payload + 8, 4);
                    }
                }
            }
        }
        if (trackId != 0 && timescale != 0)
            info.Tracks.Add(new Mp4Track { TrackId = trackId, Timescale = timescale, Handler = handler });
    }

    /// <summary>Timing of the video track inside one moof box (whole box including header).</summary>
    public static FragmentTiming? ParseMoof(byte[] moof, int length, Mp4InitInfo init)
    {
        var video = init.Video ?? init.Tracks.FirstOrDefault();
        if (video is null) return null;

        foreach (var traf in Children(moof, 8, length))
        {
            if (traf.Type != "traf") continue;

            uint trackId = 0, defaultDuration = 0, defaultFlags = 0;
            bool hasDefaultDuration = false, hasDefaultFlags = false;
            ulong baseTime = 0;
            bool hasBaseTime = false;
            ulong totalDuration = 0;
            uint? firstSampleFlags = null;

            foreach (var box in Children(moof, traf.Payload, traf.End))
            {
                switch (box.Type)
                {
                    case "tfhd" when Fits(box.Payload, 8, box.End):
                    {
                        uint flags = U24(moof, box.Payload + 1);
                        trackId = U32(moof, box.Payload + 4);
                        int pos = box.Payload + 8;
                        if ((flags & 0x01) != 0) pos += 8;       // base_data_offset
                        if ((flags & 0x02) != 0) pos += 4;       // sample_description_index
                        if ((flags & 0x08) != 0 && Fits(pos, 4, box.End))
                        {
                            defaultDuration = U32(moof, pos);
                            hasDefaultDuration = true;
                        }
                        if ((flags & 0x08) != 0) pos += 4;
                        if ((flags & 0x10) != 0) pos += 4;       // default_sample_size
                        if ((flags & 0x20) != 0 && Fits(pos, 4, box.End))
                        {
                            defaultFlags = U32(moof, pos);
                            hasDefaultFlags = true;
                        }
                        break;
                    }
                    case "tfdt" when Fits(box.Payload, 1, box.End):
                    {
                        bool v1 = moof[box.Payload] == 1;
                        if (!Fits(box.Payload + 4, v1 ? 8 : 4, box.End)) break;
                        baseTime = v1 ? U64(moof, box.Payload + 4) : U32(moof, box.Payload + 4);
                        hasBaseTime = true;
                        break;
                    }
                }
            }
            if (trackId != video.TrackId || !hasBaseTime) continue;
            if (!hasDefaultDuration) defaultDuration = video.DefaultSampleDuration;
            if (!hasDefaultFlags) defaultFlags = video.DefaultSampleFlags;

            foreach (var box in Children(moof, traf.Payload, traf.End))
            {
                if (box.Type != "trun" || !Fits(box.Payload, 8, box.End)) continue;
                uint flags = U24(moof, box.Payload + 1);
                uint count = U32(moof, box.Payload + 4);
                int pos = box.Payload + 8;
                if ((flags & 0x001) != 0) pos += 4;   // data_offset
                uint? trunFirstFlags = null;
                if ((flags & 0x004) != 0)
                {
                    if (!Fits(pos, 4, box.End)) continue;
                    trunFirstFlags = U32(moof, pos);
                    pos += 4;
                }

                // Bytes per sample entry: one 32-bit field for each of duration, size, flags, cts offset.
                int stride = 4 * System.Numerics.BitOperations.PopCount(flags & 0xF00);
                if (stride == 0)
                {
                    // All samples use the defaults: no need to walk them.
                    if (count > 0) firstSampleFlags ??= trunFirstFlags ?? defaultFlags;
                    totalDuration += (ulong)count * defaultDuration;
                    continue;
                }
                for (uint i = 0; i < count && Fits(pos, stride, box.End); i++)
                {
                    uint duration = defaultDuration;
                    uint sampleFlags = defaultFlags;
                    if ((flags & 0x100) != 0) { duration = U32(moof, pos); pos += 4; }
                    if ((flags & 0x200) != 0) pos += 4;                                  // size
                    if ((flags & 0x400) != 0) { sampleFlags = U32(moof, pos); pos += 4; }
                    if ((flags & 0x800) != 0) pos += 4;                                  // cts offset
                    if (firstSampleFlags is null)
                        firstSampleFlags = i == 0 && trunFirstFlags.HasValue ? trunFirstFlags.Value : sampleFlags;
                    totalDuration += duration;
                }
            }

            // sample_is_non_sync_sample is bit 16 of the sample flags.
            bool key = firstSampleFlags is uint f && ((f >> 16) & 1) == 0;
            return new FragmentTiming(baseTime / (double)video.Timescale, totalDuration / (double)video.Timescale, key);
        }
        return null;
    }
}
