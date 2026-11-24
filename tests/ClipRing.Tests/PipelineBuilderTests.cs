using ClipRing.Capture;
using ClipRing.Core;
using Xunit;

namespace ClipRing.Tests;

public class PipelineBuilderTests
{
    private static readonly MonitorInfo Monitor1080p = new(0, 1920, 1080, @"\\.\DISPLAY1", true, 0, 240, 1920, 1080);

    private static FfmpegInfo FullFfmpeg() => new()
    {
        Path = "ffmpeg.exe",
        Version = "9.0",
        Encoders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "h264_nvenc", "hevc_nvenc", "av1_nvenc", "h264_amf", "libx264",
        },
        Filters = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "gfxcapture", "ddagrab", "scale_d3d11",
        },
    };

    [Fact]
    public void StretchesFourByThreeToPanelAspectWhenEnabled()
    {
        var stretched = new MonitorInfo(0, 1280, 960, @"\\.\DISPLAY1", true, 0, 240, 2560, 1440);
        var settings = new AppSettings { StretchToScreen = true, Resolution = ResolutionMode.Native };

        var (w, h) = PipelineBuilder.OutputSize(settings, stretched);
        Assert.Equal(1706, w);
        Assert.Equal(960, h);
    }

    [Fact]
    public void NeverUpscalesAboveNativeHeight()
    {
        var (w, h) = PipelineBuilder.OutputSize(ResolutionMode.P2160, 1920, 1080);
        Assert.Equal((1920, 1080), (w, h));
    }

    [Fact]
    public void EstimateBufferBytesCountsThreeTracksForSeparateAudio()
    {
        var single = new AppSettings
        {
            BufferSeconds = 100,
            RateControl = RateControlMode.Cbr,
            CaptureSystemAudio = true,
            CaptureMicrophone = true,
            SeparateAudioTracks = false,
            AudioBitrateKbps = 200,
        };
        var separate = new AppSettings
        {
            BufferSeconds = 100,
            RateControl = RateControlMode.Cbr,
            CaptureSystemAudio = true,
            CaptureMicrophone = true,
            SeparateAudioTracks = true,
            AudioBitrateKbps = 200,
        };

        long diff = PipelineBuilder.EstimateBufferBytes(separate, 20000) - PipelineBuilder.EstimateBufferBytes(single, 20000);
        // 2 extra tracks * 200 kbps * 100 s / 8 = 5,000,000 bytes
        Assert.Equal(5_000_000L, diff);
    }

    [Fact]
    public void SeparateAudioTracksRequiresBothSources()
    {
        var onlySystem = new AppSettings { CaptureSystemAudio = true, CaptureMicrophone = false, SeparateAudioTracks = true };
        var both = new AppSettings { CaptureSystemAudio = true, CaptureMicrophone = true, SeparateAudioTracks = true };

        Assert.False(PipelineBuilder.SeparateAudioTracks(onlySystem));
        Assert.True(PipelineBuilder.SeparateAudioTracks(both));
    }

    [Fact]
    public void BuildRecordArgsConfiguresThreeAudioTracksWhenSeparateEnabled()
    {
        var settings = new AppSettings
        {
            CaptureSystemAudio = true,
            CaptureMicrophone = true,
            SeparateAudioTracks = true,
            Fps = 120,
        };
        var spec = PipelineBuilder.Candidates(FullFfmpeg(), settings, Monitor1080p, EncoderBackend.Nvenc, VideoSource.Wgc).First();

        var args = PipelineBuilder.BuildRecordArgs(spec, Monitor1080p, settings, audioStartUnix: 1000.0);

        Assert.Contains("5.1", args);
        Assert.Contains("-filter_complex", args);
        Assert.Contains("[mix]", args);
        Assert.Contains("[game]", args);
        Assert.Contains("[mic]", args);
        Assert.Contains("title=Mix", args);
        Assert.Contains("title=PC audio", args);
        Assert.Contains("title=Microphone", args);
    }

    [Fact]
    public void EncoderCapabilitiesFallsBackFromUnsupportedAv1ToHevcOrH264()
    {
        var ff = FullFfmpeg();
        var hevcOnly = EncoderCapabilities.FromProbe(ff, new Dictionary<EncoderBackend, HashSet<VideoCodec>>
        {
            [EncoderBackend.Nvenc] = new() { VideoCodec.H264, VideoCodec.Hevc },
        });
        Assert.Equal(VideoCodec.Hevc, hevcOnly.Fallback(VideoCodec.Av1));

        var noHw = EncoderCapabilities.FromProbe(ff, new Dictionary<EncoderBackend, HashSet<VideoCodec>>());
        Assert.Equal(VideoCodec.H264, noHw.Fallback(VideoCodec.Av1));
        Assert.True(noHw.Usable(EncoderBackend.Software, VideoCodec.H264));
        Assert.False(noHw.Usable(EncoderBackend.Software, VideoCodec.Hevc));
    }
}
