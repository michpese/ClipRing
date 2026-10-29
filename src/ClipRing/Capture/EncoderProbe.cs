using System.Diagnostics;
using System.Text.Json;
using ClipRing.Core;
using ClipRing.Localization;
using ClipRing.Native;

namespace ClipRing.Capture;

/// <summary>
/// Which hardware encoders work on this PC, per codec. FFmpeg lists every encoder it was built with
/// (NVENC, AMF, QSV, D3D12), but only the ones the graphics hardware supports can be used: AV1 needs
/// e.g. an RTX 40/50, a Radeon RX 7000/9000 or an Intel Arc. Determined by a tiny test encode each.
/// </summary>
public sealed class EncoderCapabilities
{
    private static readonly EncoderBackend[] Hardware =
        { EncoderBackend.Nvenc, EncoderBackend.Amf, EncoderBackend.Qsv, EncoderBackend.D3D12 };

    private readonly Dictionary<EncoderBackend, HashSet<VideoCodec>> _supported;
    private readonly FfmpegInfo? _ffmpeg;

    private EncoderCapabilities(FfmpegInfo? ffmpeg, Dictionary<EncoderBackend, HashSet<VideoCodec>>? supported)
    {
        _ffmpeg = ffmpeg;
        _supported = supported ?? new Dictionary<EncoderBackend, HashSet<VideoCodec>>();
        Probed = supported is not null;
    }

    /// <summary>Before the check: everything in the FFmpeg build counts as usable.</summary>
    public static EncoderCapabilities Unknown(FfmpegInfo? ffmpeg) => new(ffmpeg, null);

    public static EncoderCapabilities FromProbe(FfmpegInfo ffmpeg, Dictionary<EncoderBackend, HashSet<VideoCodec>> supported)
        => new(ffmpeg, supported);

    public bool Probed { get; }

    public bool AnyHardware => _supported.Values.Any(s => s.Count > 0);

    public IReadOnlyDictionary<EncoderBackend, HashSet<VideoCodec>> Supported => _supported;

    /// <summary>Can this backend encode this codec here? Software: H.264 and HEVC (AV1 is too slow in real time).</summary>
    public bool Usable(EncoderBackend backend, VideoCodec codec)
    {
        if (backend == EncoderBackend.Auto) return CodecUsable(codec);
        bool inBuild = _ffmpeg is null || _ffmpeg.HasEncoder(PipelineBuilder.EncoderName(backend, codec));
        if (backend == EncoderBackend.Software) return inBuild && codec != VideoCodec.Av1;
        if (!Probed) return inBuild;
        return _supported.TryGetValue(backend, out var codecs) && codecs.Contains(codec);
    }

    /// <summary>A codec is offered when a hardware encoder can do it (or, without any, H.264 in software).</summary>
    public bool CodecUsable(VideoCodec codec)
    {
        if (!Probed) return true;
        if (Hardware.Any(b => Usable(b, codec))) return true;
        return !AnyHardware && codec == VideoCodec.H264 && Usable(EncoderBackend.Software, codec);
    }

    /// <summary>Best codec this PC can record in, preferring the requested one.</summary>
    public VideoCodec Fallback(VideoCodec wanted)
    {
        if (CodecUsable(wanted)) return wanted;
        return CodecUsable(VideoCodec.Hevc) && wanted == VideoCodec.Av1 ? VideoCodec.Hevc : VideoCodec.H264;
    }

    public string Describe()
    {
        if (!Probed) return Loc.T("Caps_Checking");
        var parts = Hardware
            .Where(b => _supported.TryGetValue(b, out var c) && c.Count > 0)
            .Select(b => $"{PipelineBuilder.BackendName(b)}: " +
                         string.Join(", ", _supported[b].OrderBy(c => c).Select(PipelineBuilder.CodecName)))
            .ToList();
        return parts.Count == 0 ? Loc.T("Caps_None") : string.Join("\n", parts);
    }
}

public static class EncoderProbe
{
    private static readonly EncoderBackend[] Hardware =
        { EncoderBackend.Nvenc, EncoderBackend.Amf, EncoderBackend.Qsv, EncoderBackend.D3D12 };

    private static readonly VideoCodec[] Codecs = { VideoCodec.H264, VideoCodec.Hevc, VideoCodec.Av1 };

    private static string CacheFile => Path.Combine(AppPaths.DataDir, "encoders.json");

    private sealed class CacheEntry
    {
        public string Key { get; set; } = "";
        public Dictionary<string, List<string>> Supported { get; set; } = new();
    }

    /// <summary>Uses the cached result for the same FFmpeg, graphics adapters and drivers, else tests.</summary>
    public static async Task<EncoderCapabilities> GetAsync(FfmpegInfo ffmpeg, bool force, CancellationToken ct = default)
    {
        string key = Signature(ffmpeg);
        if (!force && TryLoad(key) is { } cached)
        {
            var caps = EncoderCapabilities.FromProbe(ffmpeg, cached);
            Log.Info("Hardware-Encoder (gespeichert): " + caps.Describe().Replace("\n", "; "));
            return caps;
        }

        // Vendors in parallel (they don't share hardware); codecs of one vendor one after another.
        var results = await Task.WhenAll(Hardware.Select(b => ProbeBackendAsync(ffmpeg, b, ct))).ConfigureAwait(false);
        var supported = new Dictionary<EncoderBackend, HashSet<VideoCodec>>();
        for (int i = 0; i < Hardware.Length; i++) supported[Hardware[i]] = results[i];

        var result = EncoderCapabilities.FromProbe(ffmpeg, supported);
        Log.Info("Hardware-Encoder geprüft: " + result.Describe().Replace("\n", "; "));
        Save(key, supported);
        return result;
    }

    private static async Task<HashSet<VideoCodec>> ProbeBackendAsync(FfmpegInfo ffmpeg, EncoderBackend backend, CancellationToken ct)
    {
        var ok = new HashSet<VideoCodec>();
        foreach (var codec in Codecs)
        {
            string encoder = PipelineBuilder.EncoderName(backend, codec);
            if (!ffmpeg.HasEncoder(encoder)) continue;
            var run = await ProcessRunner.RunAsync(ffmpeg.Path, TestArgs(backend, encoder), TimeSpan.FromSeconds(15), ct)
                                         .ConfigureAwait(false);
            if (run.Success)
            {
                ok.Add(codec);
                continue;
            }
            Log.Info($"{encoder} nicht nutzbar: {ProcessRunner.Tail(run.StdErr, 1)}");
            // Without H.264 this vendor's encoder is not present at all: skip the other codecs.
            if (codec == VideoCodec.H264) break;
        }
        return ok;
    }

    private static string[] TestArgs(EncoderBackend backend, string encoder)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        if (backend == EncoderBackend.D3D12)
            a.AddRange(new[] { "-init_hw_device", "d3d12va=dx12", "-filter_hw_device", "dx12" });
        a.AddRange(new[] { "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=30", "-frames:v", "3" });
        a.AddRange(backend == EncoderBackend.D3D12
            ? new[] { "-vf", "format=nv12,hwupload" }
            : new[] { "-pix_fmt", "nv12" });
        a.AddRange(new[] { "-c:v", encoder, "-f", "null", "-" });
        return a.ToArray();
    }

    /// <summary>FFmpeg build + graphics adapters + encoder driver versions: a change means testing again.</summary>
    private static string Signature(FfmpegInfo ffmpeg)
    {
        var parts = new List<string> { ffmpeg.Path, ffmpeg.Version };
        parts.AddRange(Win32.DisplayAdapters());
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (var dll in new[] { "nvEncodeAPI64.dll", "amfrt64.dll", "libmfxhw64.dll" })
        {
            try
            {
                var path = Path.Combine(system, dll);
                if (File.Exists(path)) parts.Add($"{dll}={FileVersionInfo.GetVersionInfo(path).FileVersion}");
            }
            catch { /* not important */ }
        }
        return string.Join("|", parts);
    }

    private static Dictionary<EncoderBackend, HashSet<VideoCodec>>? TryLoad(string key)
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(CacheFile));
            if (entry is null || entry.Key != key) return null;
            var map = new Dictionary<EncoderBackend, HashSet<VideoCodec>>();
            foreach (var (backend, codecs) in entry.Supported)
            {
                if (!Enum.TryParse<EncoderBackend>(backend, out var b)) continue;
                map[b] = codecs.Select(c => Enum.TryParse<VideoCodec>(c, out var v) ? (VideoCodec?)v : null)
                               .Where(v => v is not null).Select(v => v!.Value).ToHashSet();
            }
            return map;
        }
        catch
        {
            return null;
        }
    }

    private static void Save(string key, Dictionary<EncoderBackend, HashSet<VideoCodec>> supported)
    {
        try
        {
            var entry = new CacheEntry
            {
                Key = key,
                Supported = supported.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Select(c => c.ToString()).ToList()),
            };
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn("Encoder-Ergebnis nicht gespeichert: " + ex.Message);
        }
    }
}
