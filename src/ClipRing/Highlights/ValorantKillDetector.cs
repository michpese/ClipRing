using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipRing.Capture;
using ClipRing.Core;
using ClipRing.Native;

namespace ClipRing.Highlights;

/// <summary>
/// EXPERIMENTAL. VALORANT has no official live event API and Vanguard forbids reading game
/// memory, so kills are detected visually: a second, very light ffmpeg capture grabs only the
/// kill-banner area (10 fps, half resolution, grayscale) and compares it with reference
/// images ("templates") the user captured once. Matching uses normalized cross-correlation of
/// edge images, which is robust against the changing game scene behind the banner.
/// </summary>
public sealed class ValorantKillDetector : IDisposable
{
    public const string GameName = "VALORANT";
    public const string ProcessName = "VALORANT-Win64-Shipping";
    private const int Fps = 10;
    private const int MaxShift = 3;

    private readonly int _width, _height;          // analysed (half-resolution) size
    private readonly int _captureW, _captureH, _captureX, _captureY;
    private readonly int _outputIndex;
    private readonly double _threshold;
    private volatile float[][] _templates = Array.Empty<float[]>(); // copy-on-write
    private readonly object _frameGate = new();
    private byte[]? _lastFrame;
    private Process? _process;
    private Thread? _thread;
    private volatile bool _stopping;
    private bool _active;
    private DateTime _lastTrigger = DateTime.MinValue;

    public ValorantKillDetector(MonitorInfo monitor, AppSettings s)
    {
        _outputIndex = monitor.DdaIndex; // the banner capture uses ddagrab (10 fps is fine with its timer)
        _threshold = s.ValorantThreshold;
        _captureX = (int)(monitor.Width * s.ValorantRegionX) & ~1;
        _captureY = (int)(monitor.Height * s.ValorantRegionY) & ~1;
        _captureW = Math.Min(Even(monitor.Width * s.ValorantRegionW), (monitor.Width - _captureX) & ~1);
        _captureH = Math.Min(Even(monitor.Height * s.ValorantRegionH), (monitor.Height - _captureY) & ~1);
        _width = Math.Max(8, _captureW / 2);
        _height = Math.Max(8, _captureH / 2);
        LoadTemplates();
    }

    private static int Even(double v) => Math.Max(16, (int)v & ~1);

    /// <summary>Raised on the analysis thread when a kill banner appeared.</summary>
    public event Action? KillDetected;

    /// <summary>Best match score of the latest frame (0..1), for the calibration display.</summary>
    public double LastScore { get; private set; }
    public int TemplateCount => _templates.Length;
    public string TemplateFolder => Path.Combine(AppPaths.ValorantTemplateDir, $"{_width}x{_height}");

    private void LoadTemplates()
    {
        var list = new List<float[]>();
        if (Directory.Exists(TemplateFolder))
        {
            foreach (var file in Directory.GetFiles(TemplateFolder, "*.gray"))
            {
                try
                {
                    var gray = File.ReadAllBytes(file);
                    if (gray.Length == _width * _height) list.Add(EdgeImage(gray, _width, _height));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Valorant-Vorlage {file} unlesbar: {ex.Message}");
                }
            }
        }
        _templates = list.ToArray();
        Log.Info($"Valorant-Erkennung: {list.Count} Vorlage(n) für {_width}x{_height}");
    }

    public void Start(FfmpegInfo ffmpeg)
    {
        var args = new[]
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-f", "lavfi", "-i",
            $"ddagrab=output_idx={_outputIndex}:framerate={Fps}:draw_mouse=0:video_size={_captureW}x{_captureH}:offset_x={_captureX}:offset_y={_captureY}",
            "-vf", $"hwdownload,format=bgra,scale={_width}:{_height}:flags=area,format=gray",
            "-f", "rawvideo", "pipe:1",
        };
        var psi = new ProcessStartInfo(ffmpeg.Path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _process = Process.Start(psi);
        if (_process is null) return;
        ChildProcessJob.Add(_process);
        _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn("valorant-capture: " + e.Data); };
        _process.BeginErrorReadLine();

        var stdout = _process.StandardOutput.BaseStream;
        _thread = new Thread(() => AnalyseLoop(stdout)) { IsBackground = true, Name = "valorant-detector", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
        Log.Info($"Valorant-Erkennung gestartet (Bereich {_captureW}x{_captureH} bei {_captureX},{_captureY})");
    }

    private void AnalyseLoop(Stream stdout)
    {
        int frameSize = _width * _height;
        var frame = new byte[frameSize];
        try
        {
            while (!_stopping)
            {
                int read = 0;
                while (read < frameSize)
                {
                    int n = stdout.Read(frame, read, frameSize - read);
                    if (n <= 0) return;
                    read += n;
                }
                lock (_frameGate) _lastFrame = (byte[])frame.Clone();
                var templates = _templates;
                if (templates.Length == 0) continue;

                var edges = EdgeImage(frame, _width, _height);
                double best = 0;
                foreach (var t in templates) best = Math.Max(best, BestNcc(edges, t, _width, _height));
                LastScore = best;

                var now = DateTime.UtcNow;
                if (!_active && best >= _threshold && now - _lastTrigger > TimeSpan.FromSeconds(0.8))
                {
                    _active = true;
                    _lastTrigger = now;
                    KillDetected?.Invoke();
                }
                else if (_active && best < _threshold * 0.75)
                {
                    _active = false; // banner gone: ready for the next kill
                }
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            Log.Warn("Valorant-Erkennung beendet: " + ex.Message);
        }
        catch { }
    }

    /// <summary>
    /// Stores the current banner area as a new reference (raw .gray used for matching,
    /// .png next to it so the user can see what was captured). Call on the UI thread.
    /// </summary>
    public string? CaptureTemplate()
    {
        byte[]? frame;
        lock (_frameGate) frame = _lastFrame is null ? null : (byte[])_lastFrame.Clone();
        if (frame is null) return null;

        Directory.CreateDirectory(TemplateFolder);
        var stem = Path.Combine(TemplateFolder, $"vorlage_{DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)}");
        File.WriteAllBytes(stem + ".gray", frame);
        try
        {
            var bitmap = BitmapSource.Create(_width, _height, 96, 96, PixelFormats.Gray8, null, frame, _width);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var fs = File.Create(stem + ".png");
            encoder.Save(fs);
        }
        catch (Exception ex)
        {
            Log.Warn("Vorschaubild der Vorlage nicht gespeichert: " + ex.Message);
        }

        var list = _templates.ToList();
        list.Add(EdgeImage(frame, _width, _height));
        _templates = list.ToArray();
        Log.Info("Valorant-Vorlage gespeichert: " + stem + ".gray");
        return stem + ".png";
    }

    /// <summary>Gradient magnitude, zero-mean and unit-norm (ready for NCC by dot product).</summary>
    private static float[] EdgeImage(byte[] gray, int w, int h)
    {
        var e = new float[w * h];
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                int gx = gray[i + 1] - gray[i - 1];
                int gy = gray[i + w] - gray[i - w];
                e[i] = Math.Abs(gx) + Math.Abs(gy);
            }
        }
        return e;
    }

    /// <summary>Best normalized cross-correlation over small shifts (HUD animations wobble a little).</summary>
    private static double BestNcc(float[] img, float[] tpl, int w, int h)
    {
        double best = 0;
        int m = MaxShift;
        for (int dy = -m; dy <= m; dy++)
        {
            for (int dx = -m; dx <= m; dx++)
            {
                double sumA = 0, sumB = 0, sumAA = 0, sumBB = 0, sumAB = 0;
                int n = 0;
                for (int y = m; y < h - m; y++)
                {
                    int rowA = (y + dy) * w + dx;
                    int rowB = y * w;
                    for (int x = m; x < w - m; x++)
                    {
                        double a = img[rowA + x];
                        double b = tpl[rowB + x];
                        sumA += a; sumB += b; sumAA += a * a; sumBB += b * b; sumAB += a * b;
                        n++;
                    }
                }
                if (n == 0) continue;
                double cov = sumAB - sumA * sumB / n;
                double varA = sumAA - sumA * sumA / n;
                double varB = sumBB - sumB * sumB / n;
                if (varA <= 1e-6 || varB <= 1e-6) continue;
                double ncc = cov / Math.Sqrt(varA * varB);
                if (ncc > best) best = ncc;
            }
        }
        return best;
    }

    public void Dispose()
    {
        _stopping = true;
        try { if (_process is { HasExited: false }) _process.Kill(); } catch { }
        _thread?.Join(1000);
        _process?.Dispose();
    }
}
