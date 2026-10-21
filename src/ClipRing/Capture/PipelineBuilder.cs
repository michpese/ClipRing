using System.Globalization;
using ClipRing.Core;

namespace ClipRing.Capture;

/// <summary>One concrete way to capture + encode (encoder plus the filter chain feeding it).</summary>
public sealed record PipelineSpec
{
    public required string Id { get; init; }
    public required VideoSource Source { get; init; }
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
    public string SourceLabel => Source == VideoSource.Wgc ? "Windows Graphics Capture" : "Desktop Duplication";
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

    /// <summary>
    /// Desktop Duplication. ddagrab sleeps until the next frame is due, and Windows rounds Sleep() up to its
    /// 15.6 ms timer for processes that don't raise the timer resolution (ffmpeg doesn't). Asked for 120 fps
    /// it therefore delivered ~80. Asked for a rate well above what the monitor can show
    /// (<see cref="DdaCaptureRate"/>), it never gets ahead of schedule and never sleeps; with dup_frames=0 it
    /// then just waits in AcquireNextFrame for the next desktop update, like Graphics Capture does.
    /// </summary>
    public static string Ddagrab(int outputIndex, int rate, bool cursor, bool dupFrames)
        => $"ddagrab=output_idx={outputIndex}:framerate={rate}:draw_mouse={(cursor ? 1 : 0)}:dup_frames={(dupFrames ? 1 : 0)}";

    /// <summary>
    /// ddagrab advances its schedule by at most two frame intervals per delivered frame, so the rate has to be
    /// above twice the monitor's refresh rate to never sleep. Capped at 600: ddagrab waits half a frame in
    /// whole milliseconds for a new desktop image, and above ~666 that rounds to 0 ms (a busy loop).
    /// </summary>
    public static int DdaCaptureRate(int fps, int refreshHz)
        => Math.Clamp(2 * Math.Max(fps, refreshHz > 0 ? refreshHz : 240) + 20, 60, 600);

    /// <summary>
    /// Windows Graphics Capture: delivers a frame whenever the compositor has a new one.
    /// max_framerate becomes the session's MinUpdateInterval: Windows drops every frame that comes sooner than
    /// that after the previous one. At exactly the target rate a frame that is a hair early is lost and the next
    /// one comes a whole refresh later, which gave ~100 instead of 120 fps. So capture is allowed at twice the
    /// target and <see cref="Pacing"/> thins it out to one frame per 1/fps slot.
    /// </summary>
    public static string Gfxcapture(int monitorIndex, int fps, bool cursor)
        => $"gfxcapture=monitor_idx={monitorIndex}:max_framerate={WgcCaptureRate(fps)}" +
           $":capture_cursor={(cursor ? 1 : 0)}:display_border=0";

    public static int WgcCaptureRate(int fps) => Math.Clamp(fps * 2, 1, 1000);

    /// <summary>
    /// Both capture paths deliver frames faster than the target and only when the screen changes. This keeps
    /// the first frame of every 1/fps slot. No duplicates: filling a long still period with copies would flood
    /// the encoder once frames come again.
    /// The slot index is round(t*fps), the same rounding FFmpeg applies when it puts the timestamps on the
    /// 1/fps encoder time base, so kept frames never share a tick.
    /// </summary>
    public static string Pacing(int fps)
    {
        string f = fps.ToString(CultureInfo.InvariantCulture);
        return $"select=round(t*{f})-round(prev_selected_t*{f})";
    }

    /// <summary>
    /// Video filter chain with pacing. The declared frame rate (the capture rate, well above the target) is
    /// stripped at the end, so encoders take their rate-control frame rate from the 1/fps time base; otherwise
    /// NVENC would plan for 240 or 500 frames per second and reach a fraction of the bitrate.
    /// Stripped last because vpp_qsv and friends read the frame rate.
    /// </summary>
    public static string VideoFilterChain(PipelineSpec p, int fps)
    {
        var parts = new List<string> { Pacing(fps) };
        if (!string.IsNullOrEmpty(p.VideoFilter)) parts.Add(p.VideoFilter);
        parts.Add("setpts=strip_fps=1");
        return string.Join(',', parts);
    }

    /// <summary>Encoder time base: the 1/fps slot grid, which also tells the encoder the frame rate.</summary>
    public static string EncoderTimeBase(int fps) => "1:" + fps.ToString(CultureInfo.InvariantCulture);

    /// <param name="test">Encoder tests duplicate frames on a still desktop instead of waiting for a change.</param>
    public static string SourceGraph(PipelineSpec p, MonitorInfo mon, int fps, bool cursor, bool test = false)
        => p.Source == VideoSource.Wgc
            ? Gfxcapture(mon.Index, fps, cursor)
            : Ddagrab(mon.DdaIndex, DdaCaptureRate(fps, mon.RefreshHz), cursor, dupFrames: test);

    /// <summary>Capture sources to try, preferred first.</summary>
    public static IReadOnlyList<VideoSource> Sources(FfmpegInfo ff, AppSettings s, MonitorInfo mon)
    {
        bool wgc = ff.HasFilter("gfxcapture");
        bool dda = ff.HasFilter("ddagrab") && mon.DdaIndex >= 0;
        var list = new List<VideoSource>();
        if (s.CaptureMethod == CaptureMethod.DesktopDuplication)
        {
            if (dda) list.Add(VideoSource.Dda);
            if (wgc) list.Add(VideoSource.Wgc);
        }
        else
        {
            if (wgc) list.Add(VideoSource.Wgc);
            if (dda) list.Add(VideoSource.Dda);
        }
        return list;
    }

    /// <summary>
    /// Candidate pipelines for one backend, best first. GPU paths keep frames in VRAM
    /// (zero-copy); the CPU path downloads frames and converts with swscale, which works
    /// everywhere (e.g. hybrid laptops where the display GPU is not the encoder GPU).
    /// </summary>
    public static IEnumerable<PipelineSpec> Candidates(FfmpegInfo ff, AppSettings s, MonitorInfo mon, EncoderBackend backend,
                                                     VideoSource source)
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

        string prefix = source == VideoSource.Wgc ? "wgc/" : "dda/";
        PipelineSpec Make(string id, string? filter, string pathLabel, IReadOnlyList<string>? color = null,
                          IReadOnlyList<string>? global = null) => new()
        {
            Id = prefix + id,
            Source = source,
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
    ///  - video packets are stamped with the wall clock when ffmpeg reads them from the capture source and shifted by
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
        a.AddRange(new[] { "-use_wallclock_as_timestamps", "1", "-f", "lavfi", "-i", SourceGraph(p, mon, s.Fps, s.CaptureCursor) });

        if (audio)
        {
            a.AddRange(new[]
            {
                "-thread_queue_size", "4096",
                "-f", "f32le", "-ar", AudioSampleRate.ToString(CultureInfo.InvariantCulture), "-ch_layout", "stereo",
                "-i", "pipe:0",
            });
        }

        // VFR passthrough keeps the capture times (a CFR conversion would duplicate frames to fill every gap).
        // Pacing and the 1/fps time base: see VideoFilterChain.
        a.AddRange(new[] { "-copyts", "-fps_mode", "passthrough", "-enc_time_base:v", EncoderTimeBase(s.Fps), "-map", "0:v" });
        if (audio) a.AddRange(new[] { "-map", "1:a" });
        a.AddRange(new[] { "-vf", VideoFilterChain(p, s.Fps) });
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
        // Graphics Capture only delivers frames when the screen changes; one frame is enough to validate the path.
        string frames = p.Source == VideoSource.Wgc ? "1" : "15";
        a.AddRange(new[] { "-f", "lavfi", "-i", SourceGraph(p, mon, s.Fps, false, test: true), "-frames:v", frames });
        // Same chain and time base as the recording, so the test covers them too.
        a.AddRange(new[] { "-enc_time_base:v", EncoderTimeBase(s.Fps), "-vf", VideoFilterChain(p, s.Fps) });
        a.AddRange(p.EncoderArgs);
        a.AddRange(p.ColorArgs);
        a.AddRange(new[] { "-f", "null", "-" });
        return a;
    }

    public static string FormatArgs(IEnumerable<string> args)
        => string.Join(' ', args.Select(x => x.Contains(' ') || x.Contains(',') ? $"\"{x}\"" : x));
}
