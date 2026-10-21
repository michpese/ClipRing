using System.Globalization;
using System.Text.RegularExpressions;
using ClipRing.Core;

namespace ClipRing.Capture;

public sealed record MonitorInfo(int Index, int Width, int Height)
{
    public string Label => $"Monitor {Index + 1} ({Width} × {Height})";
}

/// <summary>
/// Enumerates the outputs ddagrab can capture by grabbing one frame per output index.
/// Using ffmpeg itself guarantees the index and native size match exactly what the
/// recorder will see (DXGI order can differ from the Windows display numbering).
/// </summary>
public static partial class DisplayProbe
{
    [GeneratedRegex(@"Stream #0:0.*?Video:.*?\b(\d{3,5})x(\d{3,5})\b")]
    private static partial Regex StreamSize();

    public static async Task<List<MonitorInfo>> ProbeAsync(FfmpegInfo ffmpeg, CancellationToken ct = default)
    {
        var result = new List<MonitorInfo>();
        for (int index = 0; index < 8; index++)
        {
            var args = new[]
            {
                "-hide_banner", "-nostdin",
                "-f", "lavfi", "-i", $"ddagrab=output_idx={index}:framerate=10:draw_mouse=0",
                "-frames:v", "1", "-f", "null", "-",
            };
            var run = await ProcessRunner.RunAsync(ffmpeg.Path, args, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            var match = StreamSize().Match(run.StdErr);
            if (!run.Success || !match.Success)
            {
                if (index == 0)
                    Log.Warn($"ddagrab konnte Ausgabe 0 nicht öffnen: {ProcessRunner.Tail(run.StdErr)}");
                break;
            }
            int w = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int h = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            result.Add(new MonitorInfo(index, w, h));
        }
        Log.Info("Monitore: " + string.Join(", ", result.Select(m => m.Label)));
        return result;
    }
}
