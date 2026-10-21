using ClipRing.Core;

namespace ClipRing.Capture;

/// <summary>
/// Finds a capture/encode pipeline that actually works on this machine by running a short
/// test encode for each candidate (hardware encoders can be listed by ffmpeg but still be
/// unusable, e.g. NVENC on an AMD system). The winner is cached in the settings.
/// </summary>
public static class PipelineSelector
{
    public static string CacheKey(FfmpegInfo ff, AppSettings s, MonitorInfo mon)
    {
        var (ow, oh) = PipelineBuilder.OutputSize(s.Resolution, mon.Width, mon.Height);
        return $"{ff.Path}|{ff.Version}|{mon.Index}|{mon.Width}x{mon.Height}|{ow}x{oh}|{s.Fps}|{s.Codec}|{s.Encoder}";
    }

    public static async Task<PipelineSpec?> SelectAsync(FfmpegInfo ff, AppSettings s, MonitorInfo mon,
                                                        Action<string>? progress, CancellationToken ct)
    {
        string key = CacheKey(ff, s, mon);
        var order = s.Encoder == EncoderBackend.Auto
            ? PipelineBuilder.AutoOrder
            : new[] { s.Encoder }.Concat(PipelineBuilder.AutoOrder.Where(b => b != s.Encoder)).ToArray();

        // 1) Cached winner from a previous run with identical hardware/settings.
        if (s.PipelineCacheKey == key && !string.IsNullOrEmpty(s.PipelineCacheId))
        {
            foreach (var backend in order)
            {
                var cached = PipelineBuilder.Candidates(ff, s, mon, backend)
                                            .FirstOrDefault(c => c.Id == s.PipelineCacheId);
                if (cached is not null) return cached with { FromCache = true };
            }
        }

        // 2) Probe candidates in order.
        foreach (var backend in order)
        {
            foreach (var candidate in PipelineBuilder.Candidates(ff, s, mon, backend))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke($"Teste {candidate.EncoderLabel}, {candidate.PathLabel} …");
                var args = PipelineBuilder.BuildTestArgs(candidate, mon, s);
                var result = await ProcessRunner.RunAsync(ff.Path, args, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                if (result.Success)
                {
                    Log.Info($"Pipeline gewählt: {candidate.Id} ({candidate.EncoderLabel}, {candidate.PathLabel})");
                    if (s.Encoder != EncoderBackend.Auto && backend != s.Encoder)
                        Log.Warn($"{PipelineBuilder.BackendName(s.Encoder)} ist nicht nutzbar, weiche auf {PipelineBuilder.BackendName(backend)} aus");
                    s.PipelineCacheKey = key;
                    s.PipelineCacheId = candidate.Id;
                    return candidate;
                }
                Log.Info($"Pipeline {candidate.Id} nicht nutzbar: {ProcessRunner.Tail(result.StdErr, 2)}");
            }
        }
        return null;
    }

    public static void InvalidateCache(AppSettings s)
    {
        s.PipelineCacheKey = "";
        s.PipelineCacheId = "";
    }

    /// <summary>Which backends are present in this ffmpeg build for the given codec.</summary>
    public static Dictionary<EncoderBackend, bool> Availability(FfmpegInfo? ff, VideoCodec codec)
    {
        var map = new Dictionary<EncoderBackend, bool>();
        foreach (var backend in PipelineBuilder.AutoOrder)
            map[backend] = ff is not null && ff.HasEncoder(PipelineBuilder.EncoderName(backend, codec));
        return map;
    }
}
