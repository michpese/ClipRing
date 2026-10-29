using System.Text.RegularExpressions;
using ClipRing.Core;

namespace ClipRing.Capture;

public sealed class FfmpegInfo
{
    public required string Path { get; init; }
    public required string Version { get; init; }
    public required HashSet<string> Encoders { get; init; }
    public required HashSet<string> Filters { get; init; }

    public bool HasEncoder(string name) => Encoders.Contains(name);
    public bool HasFilter(string name) => Filters.Contains(name);
}

public static partial class FfmpegLocator
{
    [GeneratedRegex(@"^\s*[VAS][A-Z.]{5}\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex EncoderLine();

    // " TSC ddagrab           |->V       Grab Windows Desktop images ..."
    [GeneratedRegex(@"^\s*[TSC.]{2,3}\s+(\S+)\s+\S*->\S*", RegexOptions.Multiline)]
    private static partial Regex FilterLine();

    /// <summary>
    /// External ffmpeg.exe locations, used when the user picked one or ClipRing was built without FFmpeg.
    /// </summary>
    public static IEnumerable<string> ExternalCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return System.IO.Path.Combine(baseDir, "ffmpeg.exe");
        yield return System.IO.Path.Combine(baseDir, "ffmpeg", "ffmpeg.exe");
        yield return System.IO.Path.Combine(baseDir, "ffmpeg", "bin", "ffmpeg.exe");

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = System.IO.Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"); }
            catch { continue; }
            yield return candidate;
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return System.IO.Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe");
    }

    /// <summary>
    /// Order: the path chosen by the user, the FFmpeg built into ClipRing.exe, then external copies
    /// (next to the program, in PATH, WinGet).
    /// </summary>
    public static async Task<FfmpegInfo?> DetectAsync(string? configured, CancellationToken ct = default)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
            candidates.Add(Directory.Exists(configured) ? System.IO.Path.Combine(configured, "ffmpeg.exe") : configured);
        var embedded = await EmbeddedFfmpeg.EnsureExtractedAsync(ct).ConfigureAwait(false);
        if (embedded is not null) candidates.Add(embedded);
        candidates.AddRange(ExternalCandidates());

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var info = await QueryAsync(candidate, ct).ConfigureAwait(false);
                if (info is not null)
                {
                    Log.Info($"FFmpeg gefunden: {candidate} (Version {info.Version}, {info.Encoders.Count} Encoder)");
                    return info;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warn($"FFmpeg-Kandidat {candidate} unbrauchbar: {ex.Message}");
            }
        }
        return null;
    }

    public static async Task<FfmpegInfo?> QueryAsync(string exe, CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromSeconds(10);
        var version = await ProcessRunner.RunAsync(exe, new[] { "-hide_banner", "-version" }, timeout, ct);
        if (!version.Success) return null;

        var firstLine = version.StdOut.Split('\n').FirstOrDefault() ?? "";
        var match = Regex.Match(firstLine, @"version\s+(\S+)");
        string ver = match.Success ? match.Groups[1].Value : "?";

        var encoders = await ProcessRunner.RunAsync(exe, new[] { "-hide_banner", "-encoders" }, timeout, ct);
        var filters = await ProcessRunner.RunAsync(exe, new[] { "-hide_banner", "-filters" }, timeout, ct);

        var encoderSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in EncoderLine().Matches(encoders.StdOut)) encoderSet.Add(m.Groups[1].Value);

        var filterSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in FilterLine().Matches(filters.StdOut)) filterSet.Add(m.Groups[1].Value);

        return new FfmpegInfo { Path = exe, Version = ver, Encoders = encoderSet, Filters = filterSet };
    }
}
