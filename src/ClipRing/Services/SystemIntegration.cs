using System.Diagnostics;
using System.Media;
using ClipRing.Core;
using Microsoft.Win32;

namespace ClipRing.Services;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipRing";

    /// <param name="minimized">Start hidden in the notification area.</param>
    public static void Apply(bool enabled, bool minimized)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, minimized ? $"\"{exe}\" --minimized" : $"\"{exe}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn("Autostart konnte nicht gesetzt werden: " + ex.Message);
        }
    }
}

public static class Shell
{
    public static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Konnte {path} nicht öffnen: {ex.Message}");
        }
    }

    public static void ShowInExplorer(string file)
    {
        try
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            psi.ArgumentList.Add("/select," + file);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Warn($"Explorer konnte nicht geöffnet werden: {ex.Message}");
        }
    }
}

/// <summary>Short confirmation chirp, generated in memory (no sound files to ship).</summary>
public static class Sounds
{
    private static readonly Lazy<SoundPlayer?> Saved = new(() => Create(new[] { (880.0, 0.06), (1320.0, 0.09) }));
    private static readonly Lazy<SoundPlayer?> Failed = new(() => Create(new[] { (330.0, 0.12), (220.0, 0.14) }));

    public static void PlaySaved() => Play(Saved);
    public static void PlayFailed() => Play(Failed);

    private static void Play(Lazy<SoundPlayer?> player)
    {
        try { player.Value?.Play(); } catch { /* no audio device */ }
    }

    private static SoundPlayer? Create((double Freq, double Seconds)[] tones)
    {
        try
        {
            const int rate = 44100;
            var samples = new List<short>();
            foreach (var (freq, seconds) in tones)
            {
                int n = (int)(rate * seconds);
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)rate;
                    double envelope = Math.Min(1, Math.Min(i, n - i) / (rate * 0.008)); // 8 ms fades, no clicks
                    samples.Add((short)(Math.Sin(2 * Math.PI * freq * t) * envelope * 0.25 * short.MaxValue));
                }
            }

            var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                int dataBytes = samples.Count * 2;
                w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
                w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1);
                w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write("data"u8.ToArray()); w.Write(dataBytes);
                foreach (var s in samples) w.Write(s);
            }
            ms.Position = 0;
            var player = new SoundPlayer(ms);
            player.Load();
            return player;
        }
        catch
        {
            return null;
        }
    }
}
