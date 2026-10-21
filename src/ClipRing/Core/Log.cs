using System.Diagnostics;
using System.Text;

namespace ClipRing.Core;

/// <summary>Tiny thread-safe file logger (one file per day, 7 days kept).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static string? CurrentFile { get; private set; }

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            CurrentFile = Path.Combine(AppPaths.LogDir, $"cliprings-{DateTime.Now:yyyy-MM-dd}.log");
            var fs = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };

            foreach (var old in Directory.EnumerateFiles(AppPaths.LogDir, "cliprings-*.log"))
            {
                if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-7))
                {
                    try { File.Delete(old); } catch { /* in use or locked: ignore */ }
                }
            }
        }
        catch
        {
            _writer = null; // logging must never take the app down
        }
    }

    public static void Info(string message) => Write("INF", message);
    public static void Warn(string message) => Write("WRN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            try { _writer?.WriteLine(line); } catch { /* disk full etc. */ }
        }
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
