using System.Diagnostics;
using System.Globalization;
using System.Text;
using ClipRing.Core;
using ClipRing.Native;

namespace ClipRing.Capture;

/// <param name="EndAtTimestamp">Cut the clip at <paramref name="Timestamp"/> (highlights); otherwise it runs to the newest frame.</param>
public sealed record ClipRequest(double Seconds, string Game, DateTime Timestamp, string? Tag, string Reason,
                                 bool EndAtTimestamp = false);

public sealed record SavedClip(string FilePath, string Game, double DurationSeconds, long SizeBytes, string? Tag, DateTime SavedAt)
{
    public string FileName => Path.GetFileName(FilePath);
    public string Title => string.IsNullOrEmpty(Game) ? "Clip" : Game;
    public string Summary => DurationSeconds > 0
        ? $"{TimeText}, {DurationText} min, {SizeText}"
        : $"{TimeText}, {SizeText}";
    public string DurationText => DurationSeconds <= 0
        ? "–"
        : TimeSpan.FromSeconds(Math.Round(DurationSeconds)).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / (1024.0 * 1024 * 1024):0.0} GB"
        : $"{SizeBytes / (1024.0 * 1024):0} MB";
    public string TimeText => SavedAt.Date == DateTime.Today
        ? SavedAt.ToString("HH:mm", CultureInfo.InvariantCulture)
        : SavedAt.ToString(Localization.Loc.T("Format_ClipDate"), CultureInfo.InvariantCulture);
}

/// <summary>Writes a snapshot of the RAM buffer to disk as a regular MP4.</summary>
public sealed class ClipSaver
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SavedClip> SaveAsync(ReplayBuffer buffer, FfmpegInfo ffmpeg, AppSettings settings,
                                           ClipRequest request, CancellationToken ct = default)
    {
        // The newest moments are still inside the encoder (lookahead, open GOP): wait until the
        // buffer reaches the moment the hotkey was pressed.
        var anchor = buffer.WallToStream(request.Timestamp.ToUniversalTime());
        await WaitForStreamTimeAsync(buffer, anchor, TimeSpan.FromSeconds(settings.KeyframeIntervalSec * 2 + 2), ct);

        using var snapshot = buffer.Snapshot(request.Seconds, anchor, trimEnd: request.EndAtTimestamp)
            ?? throw new InvalidOperationException(Localization.Loc.T("Err_BufferEmpty"));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string folder = settings.OutputFolder;
            if (settings.GameSubfolders) folder = Path.Combine(folder, SanitizeName(request.Game, "Desktop"));
            Directory.CreateDirectory(folder);

            string name = BuildFileName(settings.FileNameTemplate, request.Game, request.Timestamp, request.Tag);
            string target = UniquePath(Path.Combine(folder, name + ".mp4"));
            string temp = Path.Combine(folder, "." + Path.GetFileNameWithoutExtension(target) + ".part.mp4");

            try
            {
                // Standard MP4 with the index at the front (-c copy, no re-encoding), which every editor opens.
                bool done = await RemuxAsync(ffmpeg, snapshot, temp, ct).ConfigureAwait(false);
                if (!done)
                {
                    // Fallback: the fragmented MP4 from the RAM buffer as-is (plays everywhere, some editors dislike it).
                    await using var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                    await snapshot.WriteToAsync(fs, ct).ConfigureAwait(false);
                }

                await MoveIntoPlaceAsync(temp, target, ct).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
            var size = new FileInfo(target).Length;
            Log.Info($"Clip gespeichert ({request.Reason}): {target} ({snapshot.Duration:F1}s, {size / 1048576.0:F1} MB)");
            return new SavedClip(target, request.Game, snapshot.Duration, size, request.Tag, DateTime.Now);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Renames the finished temp file. Virus scanners and Explorer's thumbnailer often open a brand-new
    /// file for a moment, which blocks renaming; retry for a while, then copy instead.
    /// </summary>
    private static async Task MoveIntoPlaceAsync(string temp, string target, CancellationToken ct)
    {
        IOException? last = null;
        for (int attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                File.Move(temp, target, overwrite: false);
                if (attempt > 0) Log.Info($"Clip nach {attempt + 1} Versuchen umbenannt (Datei war kurz gesperrt)");
                return;
            }
            catch (IOException ex) when (!File.Exists(target))
            {
                last = ex;
                await Task.Delay(150 + attempt * 100, ct).ConfigureAwait(false);
            }
        }

        // Reading is usually still allowed while another program holds the file: copy, then clean up.
        Log.Warn($"Umbenennen blockiert ({last?.Message}), Clip wird kopiert");
        File.Copy(temp, target, overwrite: false);
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 30 && File.Exists(temp); i++)
            {
                TryDelete(temp);
                await Task.Delay(2000).ConfigureAwait(false);
            }
        });
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* still locked: try again later */ }
    }

    private static async Task WaitForStreamTimeAsync(ReplayBuffer buffer, double? anchor, TimeSpan timeout, CancellationToken ct)
    {
        long startVersion = buffer.Version;
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (anchor is double a ? buffer.StreamEnd >= a : buffer.Version != startVersion) return;
            await Task.Delay(40, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Pipes init + fragments into ffmpeg and remuxes to a normal MP4 with moov at the front.</summary>
    private static async Task<bool> RemuxAsync(FfmpegInfo ffmpeg, ClipSnapshot snapshot, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg.Path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     "-f", "mp4", "-i", "pipe:0",
                     "-map", "0", "-c", "copy",
                     "-movflags", "+faststart",
                     "-y", output,
                 })
            psi.ArgumentList.Add(a);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            ChildProcessJob.Add(process);
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            try
            {
                var stdin = process.StandardInput.BaseStream;
                await snapshot.WriteToAsync(stdin, ct).ConfigureAwait(false);
                await stdin.FlushAsync(ct).ConfigureAwait(false);
                stdin.Close();
            }
            catch (IOException)
            {
                // ffmpeg closed the pipe early: the error text tells why
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            string stderr = await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode == 0 && File.Exists(output) && new FileInfo(output).Length > 0) return true;
            Log.Warn($"Remux fehlgeschlagen (Code {process.ExitCode}): {ProcessRunner.Tail(stderr)}");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn("Remux fehlgeschlagen: " + ex.Message);
        }
        try { if (File.Exists(output)) File.Delete(output); } catch { }
        return false;
    }

    public static string BuildFileName(string template, string game, DateTime time, string? tag)
    {
        var name = template
            .Replace("{game}", SanitizeName(game, "Desktop"), StringComparison.OrdinalIgnoreCase)
            .Replace("{date}", time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", time.ToString("HH-mm-ss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

        bool templateHasTag = template.Contains("{tag}", StringComparison.OrdinalIgnoreCase);
        name = name.Replace("{tag}", tag ?? "", StringComparison.OrdinalIgnoreCase);
        if (!templateHasTag && !string.IsNullOrEmpty(tag)) name += "_" + tag;

        name = SanitizeName(name, "Clip");
        while (name.Contains("__")) name = name.Replace("__", "_");
        return name.Trim('_', ' ', '-');
    }

    public static string SanitizeName(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value) sb.Append(invalid.Contains(ch) ? '_' : ch);
        var result = sb.ToString().Trim().TrimEnd('.');
        if (result.Length > 80) result = result[..80].Trim();
        return result.Length == 0 ? fallback : result;
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
