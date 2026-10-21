using System.Globalization;
using ClipRing.Core;

namespace ClipRing.Capture;

/// <summary>One concrete way to capture + encode (encoder plus the filter chain feeding it).</summary>
public sealed record PipelineSpec
{
    public required string Id { get; init; }
    public required EncoderBackend Backend { get; init; }
    public required VideoCodec Codec { get; init; }
    public required string EncoderName { get; init; }
    public string? VideoFilter { get; init; }
    public IReadOnlyList<string> GlobalArgs { get; init; } = Array.Empty<string>();
    public required IReadOnlyList<string> EncoderArgs { get; init; }
    public IReadOnlyList<string> ColorArgs { get; init; } = Array.Empty<string>();
    public required int OutWidth { get; init; }
    public required int OutHeight { get; init; }
    public required int BitrateKbps { get; init; }
    public required string PathLabel { get; init; }
    public bool FromCache { get; init; }

    public string EncoderLabel => $"{PipelineBuilder.BackendName(Backend)} ({PipelineBuilder.CodecName(Codec)})";
}

public static class PipelineBuilder
{
    public const int AudioSampleRate = 48000;

    public static readonly EncoderBackend[] AutoOrder =
    {
        EncoderBackend.Nvenc, EncoderBackend.Amf, EncoderBackend.Qsv, EncoderBackend.D3D12, EncoderBackend.Software,
    };

    public static string BackendName(EncoderBackend backend) => backend switch
    {
        EncoderBackend.Nvenc => "NVIDIA NVENC",
        EncoderBackend.Amf => "AMD AMF",
        EncoderBackend.Qsv => "Intel Quick Sync",
        EncoderBackend.D3D12 => "Direct3D 12 (herstellerunabhängig)",
        EncoderBackend.Software => "Software (CPU)",
        _ => "Automatisch",
    };

    public static string CodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => "HEVC/H.265",
        VideoCodec.Av1 => "AV1",
        _ => "H.264",
    };

    public static string EncoderName(EncoderBackend backend, VideoCodec codec)
    {
        string c = codec switch { VideoCodec.Hevc => "hevc", VideoCodec.Av1 => "av1", _ => "h264" };
        return backend switch
        {
            EncoderBackend.Nvenc => $"{c}_nvenc",
            EncoderBackend.Amf => $"{c}_amf",
            EncoderBackend.Qsv => $"{c}_qsv",
            EncoderBackend.D3D12 => $"{c}_d3d12va",
            EncoderBackend.Software => codec switch
            {
                VideoCodec.Hevc => "libx265",
                VideoCodec.Av1 => "libsvtav1",
                _ => "libx264",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(backend)),
        };
    }

    public static (int Width, int Height) OutputSize(ResolutionMode mode, int nativeWidth, int nativeHeight)
    {
        int targetHeight = mode switch
        {
            ResolutionMode.P720 => 720,
            ResolutionMode.P1080 => 1080,
            ResolutionMode.P1440 => 1440,
            ResolutionMode.P2160 => 2160,
            _ => nativeHeight,
        };
        if (targetHeight >= nativeHeight)
            return (nativeWidth & ~1, nativeHeight & ~1); // never upscale; 4:2:0 needs even sizes

        int width = (int)Math.Round(nativeWidth * (double)targetHeight / nativeHeight / 2.0) * 2;
        return (width, targetHeight & ~1);
    }

    /// <summary>Bitrate for the quality preset, scaled by pixels, frame rate and codec efficiency.</summary>
    public static int ComputeBitrateKbps(AppSettings s, int width, int height)
    {
        if (s.Quality == QualityPreset.Custom) return s.CustomBitrateKbps;

        double bitsPerPixel = s.Quality switch
        {
            QualityPreset.Low => 0.05,
            QualityPreset.Medium => 0.085,
            QualityPreset.Ultra => 0.20,
            _ => 0.13,
        };
        double codecFactor = s.Codec == VideoCodec.H264 ? 1.0 : 0.7;
        // Above 60 fps consecutive frames are more similar, so bitrate grows sub-linearly.
        double effectiveFps = s.Fps <= 60 ? s.Fps : 60 + (s.Fps - 60) * 0.5;
        double kbps = width * (double)height * effectiveFps * bitsPerPixel * codecFactor / 1000.0;
        return (int)Math.Clamp(Math.Round(kbps / 500.0) * 500, 2000, 150000);
    }

    public static long EstimateBufferBytes(AppSettings s, int videoKbps)
    {
        double peakFactor = s.RateControl == RateControlMode.Vbr ? 1.1 : 1.0;
        double audioKbps = s.CaptureSystemAudio || s.CaptureMicrophone ? s.AudioBitrateKbps : 0;
        return (long)((videoKbps * peakFactor + audioKbps) * 1000 / 8 * s.BufferSeconds);
    }

    public static int GopFrames(AppSettings s) => Math.Max(1, (int)Math.Round(s.Fps * s.KeyframeIntervalSec));

    public static string Ddagrab(int outputIndex, int fps, bool cursor)
        => $"ddagrab=output_idx={outputIndex}:framerate={fps}:draw_mouse={(cursor ? 1 : 0)}";

    /// <summary>
    /// Candidate pipelines for one backend, best first. GPU paths keep frames in VRAM
    /// (zero-copy); the CPU path downloads frames and converts with swscale, which works
    /// everywhere (e.g. hybrid laptops where the display GPU is not the encoder GPU).
    /// </summary>
    public static IEnumerable<PipelineSpec> Candidates(FfmpegInfo ff, AppSettings s, MonitorInfo mon, EncoderBackend backend)
    {
        string encoder = EncoderName(backend, s.Codec);
        if (!ff.HasEncoder(encoder)) yield break;

        var (ow, oh) = OutputSize(s.Resolution, mon.Width, mon.Height);
        bool scale = ow != mon.Width || oh != mon.Height;
        int kbps = ComputeBitrateKbps(s, ow, oh);
        var encArgs = EncoderArgs(backend, s.Codec, encoder, kbps, s);

        string cpuChain(string pixFmt) =>
            $"hwdownload,format=bgra,scale={ow}:{oh}:flags=bicubic:out_color_matrix=bt709:out_range=tv,format={pixFmt}";
        string[] bt709 = { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv" };

        PipelineSpec Make(string id, string? filter, string pathLabel, IReadOnlyList<string>? color = null,
                          IReadOnlyList<string>? global = null) => new()
        {
            Id = id,
            Backend = backend,
            Codec = s.Codec,
            EncoderName = encoder,
            VideoFilter = filter,
            EncoderArgs = encArgs,
            ColorArgs = color ?? Array.Empty<string>(),
            GlobalArgs = global ?? Array.Empty<string>(),
            OutWidth = ow,
            OutHeight = oh,
            BitrateKbps = kbps,
            PathLabel = pathLabel,
        };

        switch (backend)
        {
            case EncoderBackend.Nvenc:
                if (!scale) yield return Make("nvenc/direct", null, "GPU, ohne Kopie");
                if (scale && ff.HasFilter("scale_d3d11"))
                    yield return Make("nvenc/scale_d3d11", $"scale_d3d11=width={ow}:height={oh}:format=nv12", "GPU-Skalierung");
                yield return Make("nvenc/cpu", cpuChain("nv12"), "CPU-Konvertierung", bt709);
                break;

            case EncoderBackend.Amf:
                if (!scale) yield return Make("amf/direct", null, "GPU, ohne Kopie");
                if (scale && ff.HasFilter("vpp_amf"))
                    yield return Make("amf/vpp_amf", $"vpp_amf=w={ow}:h={oh}:format=nv12", "GPU-Skalierung (AMF)");
                if (scale && ff.HasFilter("scale_d3d11"))
                    yield return Make("amf/scale_d3d11", $"scale_d3d11=width={ow}:height={oh}:format=nv12", "GPU-Skalierung");
                yield return Make("amf/cpu", cpuChain("nv12"), "CPU-Konvertierung", bt709);
                break;

            case EncoderBackend.Qsv:
                if (ff.HasFilter("vpp_qsv") && ff.HasFilter("hwmap"))
                {
                    string size = scale ? $":w={ow}:h={oh}" : "";
                    yield return Make("qsv/vpp_qsv", $"hwmap=derive_device=qsv,format=qsv,vpp_qsv=format=nv12{size}",
                                      scale ? "GPU-Skalierung (QSV)" : "GPU, ohne Kopie");
                }
                yield return Make("qsv/cpu", cpuChain("nv12"), "CPU-Konvertierung", bt709);
                break;

            case EncoderBackend.D3D12:
                yield return Make("d3d12/cpu", cpuChain("nv12") + ",hwupload", "CPU-Konvertierung", bt709,
                                  new[] { "-init_hw_device", "d3d12va=dx12", "-filter_hw_device", "dx12" });
                break;

            case EncoderBackend.Software:
                yield return Make("sw/cpu", cpuChain("yuv420p"), "CPU", bt709);
                break;
        }
    }

    public static List<string> EncoderArgs(EncoderBackend backend, VideoCodec codec, string encoder, int kbps, AppSettings s)
    {
        bool cbr = s.RateControl == RateControlMode.Cbr;
        int maxrate = cbr ? kbps : (int)(kbps * 1.5);
        int bufsize = cbr ? kbps : kbps * 2;
        string gop = GopFrames(s).ToString(CultureInfo.InvariantCulture);
        string b = $"{kbps}k", max = $"{maxrate}k", buf = $"{bufsize}k";

        var a = new List<string> { "-c:v", encoder };
        switch (backend)
        {
            case EncoderBackend.Nvenc:
                // Realtime capture needs fast presets: p5-p7 are far too slow for 4K/120 and also keep the
                // driver from splitting 4K frames across several NVENC units (split_encode_mode=auto).
                a.AddRange(new[]
                {
                    "-preset", s.Speed switch { EncoderSpeed.Fast => "p1", EncoderSpeed.Quality => "p4", _ => "p3" },
                    "-tune", s.Speed == EncoderSpeed.Fast ? "ll" : "hq",
                    "-rc", cbr ? "cbr" : "vbr",
                    "-b:v", b, "-maxrate", max, "-bufsize", buf,
                    "-g", gop,
                });
                break;

            case EncoderBackend.Amf:
                a.AddRange(new[]
                {
                    "-usage", "transcoding",
                    "-quality", s.Speed switch { EncoderSpeed.Fast => "speed", EncoderSpeed.Quality => "quality", _ => "balanced" },
                    "-rc", cbr ? "cbr" : "vbr_peak",
                    "-b:v", b, "-maxrate", max, "-bufsize", buf,
                    "-g", gop,
                });
                break;

            case EncoderBackend.Qsv:
                // QSV picks CBR when maxrate == bitrate, VBR otherwise.
                a.AddRange(new[]
                {
                    "-preset", s.Speed switch { EncoderSpeed.Fast => "veryfast", EncoderSpeed.Quality => "medium", _ => "faster" },
                    "-b:v", b, "-maxrate", max, "-bufsize", buf,
                    "-g", gop,
                });
                break;

            case EncoderBackend.D3D12:
                a.AddRange(new[]
                {
                    "-rc_mode", cbr ? "CBR" : "VBR",
                    "-b:v", b, "-maxrate", max,
                    "-g", gop,
                });
                break;

            case EncoderBackend.Software:
                switch (codec)
                {
                    case VideoCodec.Av1:
                        a.AddRange(new[]
                        {
                            "-preset", s.Speed switch { EncoderSpeed.Fast => "11", EncoderSpeed.Quality => "7", _ => "9" },
                            "-b:v", b, "-g", gop,
                        });
                        break;
                    case VideoCodec.Hevc:
                        a.AddRange(new[]
                        {
                            "-preset", s.Speed switch { EncoderSpeed.Fast => "ultrafast", EncoderSpeed.Quality => "faster", _ => "superfast" },
                            "-b:v", b, "-maxrate", max, "-bufsize", buf, "-g", gop,
                            "-x265-params", "log-level=error",
                        });
                        break;
                    default:
                        a.AddRange(new[]
                        {
                            "-preset", s.Speed switch { EncoderSpeed.Fast => "veryfast", EncoderSpeed.Quality => "medium", _ => "faster" },
                            "-b:v", b, "-maxrate", max, "-bufsize", buf, "-g", gop,
                        });
                        break;
                }
                break;
        }
        // Closed GOPs: every fragment in the RAM buffer must be decodable on its own.
        a.AddRange(new[] { "-flags", "+cgop" });
        return a;
    }

    /// <summary>
    /// Full recorder command line. Timing model (one common timeline that starts at the first audio sample):
    ///  - audio is a gap-free PCM stream on stdin. Since FFmpeg 7 timestamps of raw PCM input are ignored and
    ///    audio always starts at 0, so audio sample n has the time n / 48000,
    ///  - video packets are stamped with the wall clock when ffmpeg reads them from ddagrab and shifted by
    ///    -<paramref name="audioStartUnix"/> (the wall-clock time of the first audio sample) via -itsoffset,
    ///  - -copyts keeps both on that timeline; the fragmented MP4 muxer then shifts both by the same amount.
    /// Putting the offset on the audio input instead stalls FFmpeg 7+: audio would sit at 0 while video
    /// is at ~1.8e9 s, and FFmpeg's scheduler holds the video back until audio "catches up".
    /// Output is fragmented MP4 (one fragment per keyframe) on stdout, which the app keeps in RAM.
    /// </summary>
    public static List<string> BuildRecordArgs(PipelineSpec p, MonitorInfo mon, AppSettings s, double? audioStartUnix)
    {
        bool audio = audioStartUnix.HasValue;
        // -nostdin only disables keyboard commands; the PCM stream on pipe:0 still works.
        // Info level + periodic stats show in the log how far ffmpeg got (inputs opened, output started, frames encoded).
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "info", "-stats", "-stats_period", "5" };
        a.AddRange(p.GlobalArgs);
        if (audio)
            a.AddRange(new[] { "-itsoffset", (-audioStartUnix!.Value).ToString("F6", CultureInfo.InvariantCulture) });
        a.AddRange(new[] { "-use_wallclock_as_timestamps", "1", "-f", "lavfi", "-i", Ddagrab(mon.Index, s.Fps, s.CaptureCursor) });

        if (audio)
        {
            a.AddRange(new[]
            {
                "-thread_queue_size", "4096",
                "-f", "f32le", "-ar", AudioSampleRate.ToString(CultureInfo.InvariantCulture), "-ch_layout", "stereo",
                "-i", "pipe:0",
            });
        }

        // VFR passthrough keeps the exact capture times (a CFR conversion shifts video by up to a frame);
        // the fine encoder time base avoids two frames rounding onto the same tick.
        a.AddRange(new[] { "-copyts", "-fps_mode", "passthrough", "-enc_time_base:v", "1:90000", "-map", "0:v" });
        if (audio) a.AddRange(new[] { "-map", "1:a" });
        if (!string.IsNullOrEmpty(p.VideoFilter)) a.AddRange(new[] { "-vf", p.VideoFilter });
        a.AddRange(p.EncoderArgs);
        a.AddRange(p.ColorArgs);
        if (audio) a.AddRange(new[] { "-c:a", "aac", "-b:a", $"{s.AudioBitrateKbps}k" });

        a.AddRange(new[]
        {
            "-max_muxing_queue_size", "4096",
            "-f", "mp4",
            "-movflags", "frag_keyframe+empty_moov+default_base_moof",
            "-flush_packets", "1",
            "pipe:1",
        });
        return a;
    }

    /// <summary>Short encode test used to validate a candidate on this machine.</summary>
    public static List<string> BuildTestArgs(PipelineSpec p, MonitorInfo mon, AppSettings s)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        a.AddRange(p.GlobalArgs);
        a.AddRange(new[] { "-f", "lavfi", "-i", Ddagrab(mon.Index, s.Fps, false), "-frames:v", "15" });
        if (!string.IsNullOrEmpty(p.VideoFilter)) a.AddRange(new[] { "-vf", p.VideoFilter });
        a.AddRange(p.EncoderArgs);
        a.AddRange(p.ColorArgs);
        a.AddRange(new[] { "-f", "null", "-" });
        return a;
    }

    public static string FormatArgs(IEnumerable<string> args)
        => string.Join(' ', args.Select(x => x.Contains(' ') || x.Contains(',') ? $"\"{x}\"" : x));
}
