using ClipRing.Core;
using ClipRing.Localization;

namespace ClipRing.Capture;

/// <summary>
/// Finds a capture/encode pipeline that actually works on this machine by running a short
/// test encode for each candidate (hardware encoders can be listed by ffmpeg but still be
/// unusable, e.g. NVENC on an AMD system). The winner is cached in the settings.
/// </summary>
public static class PipelineSelector
{
    /// <summary>Raised when the candidate list changes, so cached winners from older versions are re-tested.</summary>
    private const int CandidateVersion = 2;

    public static string CacheKey(FfmpegInfo ff, AppSettings s, MonitorInfo mon)
    {
        var (ow, oh) = PipelineBuilder.OutputSize(s, mon);
        return $"v{CandidateVersion}|{ff.Path}|{ff.Version}|{mon.DeviceName}|{mon.Index}|{mon.DdaIndex}|{mon.Width}x{mon.Height}|{ow}x{oh}|" +
               $"{s.Fps}|{s.Codec}|{s.Encoder}|{s.CaptureMethod}";
    }

    /// <param name="caps">Encoders this PC supports: others are not even tried.</param>
    public static async Task<PipelineSpec?> SelectAsync(FfmpegInfo ff, AppSettings s, MonitorInfo mon, EncoderCapabilities caps,
                                                        Action<string>? progress, CancellationToken ct)
    {
        string key = CacheKey(ff, s, mon);
        var order = (s.Encoder == EncoderBackend.Auto
                ? PipelineBuilder.AutoOrder
                : new[] { s.Encoder }.Concat(PipelineBuilder.AutoOrder.Where(b => b != s.Encoder)).ToArray())
            .Where(b => caps.Usable(b, s.Codec))
            .ToArray();

        var sources = PipelineBuilder.Sources(ff, s, mon);
        if (sources.Count == 0)
        {
            Log.Warn("Dieses FFmpeg kann den Bildschirm weder mit gfxcapture noch mit ddagrab aufnehmen");
            return null;
        }

        // 1) Cached winner from a previous run with identical hardware/settings.
        if (s.PipelineCacheKey == key && !string.IsNullOrEmpty(s.PipelineCacheId))
        {
            foreach (var source in sources)
            foreach (var backend in order)
            {
                var cached = PipelineBuilder.Candidates(ff, s, mon, backend, source)
                                            .FirstOrDefault(c => c.Id == s.PipelineCacheId);
                if (cached is not null) return cached with { FromCache = true };
            }
        }

        // 2) Probe candidates: hardware encoder first, then the preferred capture source
        //    (a GPU encoder with the second-best capture beats a CPU encoder with the best one).
        foreach (var backend in order)
        foreach (var source in sources)
        {
            foreach (var candidate in PipelineBuilder.Candidates(ff, s, mon, backend, source))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(Loc.F("Status_Testing", candidate.EncoderLabel, candidate.PathLabel, candidate.SourceLabel));
                var args = PipelineBuilder.BuildTestArgs(candidate, mon, s);
                var result = await ProcessRunner.RunAsync(ff.Path, args, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                if (result.Success)
                {
                    Log.Info($"Pipeline gewählt: {candidate.Id} ({candidate.EncoderLabel}, {candidate.PathLabel}, {candidate.SourceLabel})");
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
}
