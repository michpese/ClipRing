using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using ClipRing.Core;

namespace ClipRing.Capture;

/// <summary>
/// FFmpeg built into ClipRing.exe. build.ps1 stores ffmpeg.exe gzip-compressed as an embedded resource and
/// records its SHA-256 as assembly metadata. On start the exe is unpacked once to
/// %LOCALAPPDATA%\ClipRing\ffmpeg\&lt;hash&gt;\ffmpeg.exe and checked against that hash on every start,
/// so a damaged or swapped file is replaced before it runs.
/// </summary>
public static class EmbeddedFfmpeg
{
    private const string ResourceName = "ClipRing.ffmpeg.exe.gz";
    private static readonly Assembly Self = typeof(EmbeddedFfmpeg).Assembly;

    public static bool IsAvailable => Self.GetManifestResourceInfo(ResourceName) is not null;

    /// <summary>FFmpeg version of the embedded build (from build.ps1), or null.</summary>
    public static string? Version => Metadata("FfmpegVersion");

    private static string? ExpectedSha256 => Metadata("FfmpegSha256")?.ToLowerInvariant();

    private static string BaseDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipRing", "ffmpeg");

    public static bool IsEmbeddedPath(string path)
        => path.StartsWith(BaseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string? Metadata(string key)
    {
        var value = Self.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Returns the path of the unpacked ffmpeg.exe, unpacking it first if needed. Null if this build has no
    /// embedded FFmpeg or unpacking failed (the caller then looks for an external ffmpeg.exe).
    /// </summary>
    public static async Task<string?> EnsureExtractedAsync(CancellationToken ct = default)
    {
        if (!IsAvailable) return null;
        try
        {
            return await Task.Run(() => EnsureExtracted(ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Error("Eingebautes FFmpeg konnte nicht entpackt werden", ex);
            return null;
        }
    }

    private static string? EnsureExtracted(CancellationToken ct)
    {
        string? expected = ExpectedSha256;
        // Without a recorded hash (hand-made dev build), key the folder by this exact build of ClipRing.
        string key = expected is not null ? expected[..16] : "build-" + Self.ManifestModule.ModuleVersionId.ToString("N")[..16];
        string dir = Path.Combine(BaseDir, key);
        string exe = Path.Combine(dir, "ffmpeg.exe");

        if (File.Exists(exe))
        {
            if (expected is null || HashFile(exe) == expected)
            {
                CleanupOldVersions(key);
                return exe;
            }
            Log.Warn("Entpacktes FFmpeg stimmt nicht mit dem eingebauten überein und wird ersetzt");
        }

        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $"ffmpeg.{Guid.NewGuid():N}.tmp");
        string actual;
        try
        {
            using (var resource = Self.GetManifestResourceStream(ResourceName)
                                  ?? throw new InvalidOperationException("Ressource fehlt"))
            using (var gzip = new GZipStream(resource, CompressionMode.Decompress))
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 20];
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    hash.AppendData(buffer, 0, read);
                    file.Write(buffer, 0, read);
                }
                actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (expected is not null && actual != expected)
                throw new InvalidDataException($"Prüfsumme falsch (erwartet {expected}, erhalten {actual})");

            File.Move(temp, exe, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }

        Log.Info($"Eingebautes FFmpeg{(Version is null ? "" : " " + Version)} entpackt nach {exe}");
        CleanupOldVersions(key);
        return exe;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Removes FFmpeg copies of earlier ClipRing versions (skips any that are still running).</summary>
    private static void CleanupOldVersions(string keep)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(BaseDir))
            {
                if (string.Equals(Path.GetFileName(dir), keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(dir, recursive: true); }
                catch { /* in use: next time */ }
            }
        }
        catch { /* not important */ }
    }
}
