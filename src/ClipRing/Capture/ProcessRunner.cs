using System.Diagnostics;
using ClipRing.Native;

namespace ClipRing.Capture;

public static class ProcessRunner
{
    public sealed record Result(int ExitCode, string StdOut, string StdErr, bool TimedOut)
    {
        public bool Success => !TimedOut && ExitCode == 0;
    }

    /// <summary>Runs a short-lived process (ffmpeg probes) and collects its output.</summary>
    public static async Task<Result> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout,
                                              CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();
        ChildProcessJob.Add(process);

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        int exitCode = -1;
        if (!timedOut)
        {
            try { exitCode = process.ExitCode; } catch { /* not available */ }
        }
        ct.ThrowIfCancellationRequested();
        return new Result(exitCode, stdout, stderr, timedOut);
    }

    /// <summary>Last non-empty lines of an ffmpeg stderr dump, for error messages.</summary>
    public static string Tail(string text, int lines = 3)
    {
        var parts = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" | ", parts.TakeLast(lines));
    }
}
