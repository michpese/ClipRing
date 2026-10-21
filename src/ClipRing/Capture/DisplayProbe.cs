using System.Globalization;
using System.Text.RegularExpressions;
using ClipRing.Core;
using ClipRing.Native;

namespace ClipRing.Capture;

/// <param name="Index">Position in EnumDisplayMonitors order; this is gfxcapture's monitor_idx.</param>
/// <param name="DeviceName">Windows device name such as \\.\DISPLAY1, stable across restarts.</param>
/// <param name="DdaIndex">Matching ddagrab output_idx (Desktop Duplication order), or -1 if none.</param>
/// <param name="RefreshHz">Current refresh rate, 0 if unknown. Graphics Capture can't deliver more frames than this.</param>
public sealed record MonitorInfo(int Index, int Width, int Height, string DeviceName, bool IsPrimary, int DdaIndex,
                                 int RefreshHz = 0)
{
    public string Label => $"Monitor {Index + 1}: {Width} × {Height}" +
                           (RefreshHz > 0 ? $", {RefreshHz} Hz" : "") +
                           (IsPrimary ? ", Hauptbildschirm" : "");
}

/// <summary>
/// Lists the monitors. Windows' own enumeration is the reference (gfxcapture uses the same order);
/// for the Desktop Duplication path each monitor is matched to a ddagrab output by probing ffmpeg,
/// because DXGI orders outputs differently.
/// </summary>
public static partial class DisplayProbe
{
    [GeneratedRegex(@"Stream #0:0.*?Video:.*?\b(\d{3,5})x(\d{3,5})\b")]
    private static partial Regex StreamSize();

    public static async Task<List<MonitorInfo>> ProbeAsync(FfmpegInfo ffmpeg, CancellationToken ct = default)
    {
        var windows = Win32.EnumerateMonitors();
        var dda = ffmpeg.HasFilter("ddagrab")
            ? await ProbeDdaOutputsAsync(ffmpeg, ct).ConfigureAwait(false)
            : new List<(int Index, int Width, int Height)>();

        var result = new List<MonitorInfo>();
        if (windows.Count == 0)
        {
            // Should not happen; fall back to what ddagrab sees.
            foreach (var d in dda)
                result.Add(new MonitorInfo(d.Index, d.Width, d.Height, $"DDA{d.Index}", d.Index == 0, d.Index));
            Log.Warn("Windows meldet keine Monitore, verwende die Desktop-Duplication-Liste");
            return result;
        }

        // Match each Windows monitor to a ddagrab output of the same size (first free one),
        // then hand out the remaining outputs in order.
        var used = new HashSet<int>();
        var ddaIndex = new int[windows.Count];
        for (int i = 0; i < windows.Count; i++)
        {
            var match = dda.FirstOrDefault(d => !used.Contains(d.Index) && d.Width == windows[i].Width && d.Height == windows[i].Height);
            ddaIndex[i] = dda.Count > 0 && (match.Width != 0) ? match.Index : -1;
            if (ddaIndex[i] >= 0) used.Add(ddaIndex[i]);
        }
        var spare = new Queue<int>(dda.Select(d => d.Index).Where(i => !used.Contains(i)));
        for (int i = 0; i < windows.Count; i++)
        {
            if (ddaIndex[i] < 0 && spare.Count > 0) ddaIndex[i] = spare.Dequeue();
        }

        for (int i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            result.Add(new MonitorInfo(i, w.Width, w.Height, w.Device, w.Primary, ddaIndex[i], w.RefreshHz));
        }

        Log.Info("Monitore: " + string.Join("; ", result.Select(m =>
            $"{m.Label} [{m.DeviceName}, DDA {(m.DdaIndex < 0 ? "-" : m.DdaIndex.ToString(CultureInfo.InvariantCulture))}]")));
        return result;
    }

    /// <summary>Grabs one frame per ddagrab output index to learn their sizes.</summary>
    private static async Task<List<(int Index, int Width, int Height)>> ProbeDdaOutputsAsync(FfmpegInfo ffmpeg, CancellationToken ct)
    {
        var result = new List<(int Index, int Width, int Height)>();
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
            result.Add((index, w, h));
        }
        return result;
    }
}
