using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClipRing.Audio;
using ClipRing.Core;
using ClipRing.Native;

namespace ClipRing.Capture;

/// <summary>
/// Owns the long-running ffmpeg process: feeds it audio on stdin and reads the fragmented
/// MP4 from stdout into the <see cref="ReplayBuffer"/>.
/// </summary>
public sealed class ReplayRecorder : IDisposable
{
    private const long MaxSmallBox = 64L * 1024 * 1024;

    private readonly ReplayBuffer _buffer;
    private readonly Queue<string> _stderrTail = new();
    private Process? _process;
    private Thread? _stdoutThread;
    private Thread? _stderrThread;
    private AudioEngine? _audio;
    private volatile bool _stopping;
    private int _faultRaised;

    // Diagnostics: what came out of ffmpeg so far.
    private long _stdoutBytes;
    private readonly List<string> _boxTrail = new();
    private int _droppedFragments;
    private string _lastStats = "";
    private int _statsLines;

    public ReplayRecorder(ReplayBuffer buffer) => _buffer = buffer;

    /// <summary>Raised once when the first fragment arrived (capture + encoder are working).</summary>
    public event Action? Running;

    /// <summary>Raised when ffmpeg exits or the stream breaks while not stopping. Argument: error text.</summary>
    public event Action<string>? Faulted;

    public DateTime StartedUtc { get; private set; }
    public bool HasProducedData { get; private set; }

    /// <summary>Average encoded frames per second reported by ffmpeg (null until the first status line).</summary>
    public double? EncoderFps { get; private set; }

    public void Start(string ffmpegPath, IReadOnlyList<string> args, AudioEngine? audio)
    {
        StartedUtc = DateTime.UtcNow; // before anything else: the watchdog may look at it right away
        var psi = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = audio is not null,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Log.Info("Starte Aufnahme: ffmpeg " + PipelineBuilder.FormatArgs(args));

        _buffer.Reset(null);
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += OnProcessExited;
        _process.Start();
        ChildProcessJob.Add(_process);
        try { _process.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { /* not allowed: fine */ }

        if (audio is not null)
        {
            _audio = audio;
            audio.AttachOutput(_process.StandardInput.BaseStream);
        }

        var stdout = _process.StandardOutput.BaseStream;
        _stdoutThread = new Thread(() => ReadLoop(stdout))
        {
            IsBackground = true,
            Name = "ffmpeg-stdout",
            Priority = ThreadPriority.AboveNormal,
        };
        _stdoutThread.Start();

        var stderr = _process.StandardError;
        _stderrThread = new Thread(() => StderrLoop(stderr)) { IsBackground = true, Name = "ffmpeg-stderr" };
        _stderrThread.Start();
    }

    private void StderrLoop(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                line = line.Trim();
                if (line.Length == 0) continue;

                // Progress lines ("frame=  600 fps=120 ... speed=1x") come every 5 s. Log them while waiting
                // for data, for the first 30 s (shows whether the encoder keeps up) and then every 5 minutes.
                if (line.StartsWith("frame=", StringComparison.Ordinal) || line.StartsWith("size=", StringComparison.Ordinal))
                {
                    _lastStats = line;
                    _statsLines++;
                    var fps = Regex.Match(line, @"\bfps=\s*([0-9]+(?:\.[0-9]+)?)");
                    if (fps.Success && double.TryParse(fps.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        EncoderFps = value;
                    if (!HasProducedData || _statsLines <= 6 || _statsLines % 60 == 0) Log.Info("ffmpeg-Status: " + line);
                    continue;
                }

                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line);
                    while (_stderrTail.Count > 20) _stderrTail.Dequeue();
                }
                Log.Info("ffmpeg: " + line);
            }
        }
        catch { /* process gone */ }
    }

    /// <summary>One line describing how far the pipeline got, for "no data" diagnostics.</summary>
    public string DiagnosticSummary()
    {
        string boxes;
        lock (_boxTrail) boxes = _boxTrail.Count == 0 ? "keine" : string.Join(" ", _boxTrail);
        bool exited = false;
        try { exited = _process?.HasExited ?? true; } catch { }
        var text = $"FFmpeg {(exited ? "beendet" : "läuft")}; Ausgabe: {Interlocked.Read(ref _stdoutBytes)} Bytes, MP4-Boxen: {boxes}";
        if (_droppedFragments > 0) text += $", {_droppedFragments} Fragmente nicht lesbar";
        text += $"; letzter Status: {(_lastStats.Length == 0 ? "keiner" : _lastStats)}";
        if (_audio is not null) text += "; " + _audio.PipeDiagnostics();
        return text;
    }

    private void NoteBox(string type, long size)
    {
        Interlocked.Add(ref _stdoutBytes, size);
        lock (_boxTrail)
        {
            if (_boxTrail.Count < 8) _boxTrail.Add($"{type}({size})");
        }
    }

    private string ErrorTail()
    {
        lock (_stderrTail)
            return _stderrTail.Count == 0 ? "" : string.Join(" | ", _stderrTail.TakeLast(3));
    }

    private static bool ReadExact(Stream s, byte[] buffer, int offset, int count)
    {
        while (count > 0)
        {
            int n = s.Read(buffer, offset, count);
            if (n <= 0) return false;
            offset += n;
            count -= n;
        }
        return true;
    }

    private static void Skip(Stream s, long count)
    {
        var scratch = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (count > 0)
            {
                int n = s.Read(scratch, 0, (int)Math.Min(scratch.Length, count));
                if (n <= 0) throw new EndOfStreamException();
                count -= n;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    private void ReadLoop(Stream stdout)
    {
        var header = new byte[16];
        using var initBytes = new MemoryStream();
        Mp4InitInfo? init = null;
        byte[]? pendingMoof = null;

        try
        {
            while (true)
            {
                if (!ReadExact(stdout, header, 0, 8)) break;
                long size = Mp4Parser.U32(header, 0);
                string type = Encoding.ASCII.GetString(header, 4, 4);
                int headerLength = 8;
                if (size == 1)
                {
                    if (!ReadExact(stdout, header, 8, 8)) break;
                    size = (long)Mp4Parser.U64(header, 8);
                    headerLength = 16;
                }
                if (size < headerLength) throw new InvalidDataException($"Ungültige MP4-Box '{type}' ({size} Bytes)");
                long payload = size - headerLength;
                NoteBox(type, size);

                switch (type)
                {
                    case "ftyp":
                    case "moov":
                    {
                        if (size > MaxSmallBox) throw new InvalidDataException($"Box '{type}' zu groß");
                        var box = new byte[size];
                        Array.Copy(header, box, headerLength);
                        if (!ReadExact(stdout, box, headerLength, (int)payload)) return;
                        initBytes.Write(box);
                        if (type == "moov")
                        {
                            var initArray = initBytes.ToArray();
                            init = Mp4Parser.ParseInit(initArray);
                            if (init.Video is null) throw new InvalidDataException("Kein Videotrack im Stream");
                            _buffer.Reset(initArray);
                        }
                        break;
                    }
                    case "moof":
                    {
                        if (size > MaxSmallBox) throw new InvalidDataException("moof zu groß");
                        pendingMoof = new byte[size];
                        Array.Copy(header, pendingMoof, headerLength);
                        if (!ReadExact(stdout, pendingMoof, headerLength, (int)payload)) return;
                        break;
                    }
                    case "mdat" when pendingMoof is not null && init is not null:
                    {
                        var data = new SegmentedBuffer();
                        try
                        {
                            data.Write(pendingMoof);
                            data.Write(header.AsSpan(0, headerLength));
                            data.ReadFrom(stdout, payload);
                        }
                        catch
                        {
                            data.Dispose();
                            throw;
                        }

                        FragmentTiming? timing = null;
                        try { timing = Mp4Parser.ParseMoof(pendingMoof, pendingMoof.Length, init); }
                        catch (Exception ex) { Log.Warn("moof nicht lesbar: " + ex.Message); }
                        pendingMoof = null;

                        if (timing is null)
                        {
                            if (++_droppedFragments <= 3)
                                Log.Warn($"MP4-Fragment ohne lesbare Videozeit verworfen ({data.Length} Bytes)");
                            data.Dispose();
                            break;
                        }
                        _buffer.Add(new BufferedFragment(data, timing.Value));

                        if (!HasProducedData)
                        {
                            HasProducedData = true;
                            Log.Info("Erstes Fragment empfangen, Replay-Puffer läuft");
                            Running?.Invoke();
                        }
                        break;
                    }
                    default:
                        // styp, sidx, prft, free, mfra or mdat without moof: not needed
                        Skip(stdout, payload);
                        break;
                }
            }
        }
        catch (Exception ex) when (!_stopping)
        {
            Log.Error("Fehler beim Lesen des Aufnahme-Streams", ex);
            RaiseFault("Datenstrom unterbrochen: " + ex.Message);
        }
        catch
        {
            // stopping: pipe closed
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_stopping) return;
        // Give the stderr reader a moment to collect the final error lines.
        _stderrThread?.Join(500);
        int code = -1;
        try { code = _process?.ExitCode ?? -1; } catch { }
        var tail = ErrorTail();
        RaiseFault(string.IsNullOrEmpty(tail) ? $"FFmpeg wurde beendet (Code {code})" : tail);
    }

    private void RaiseFault(string message)
    {
        if (_stopping) return;
        if (Interlocked.Exchange(ref _faultRaised, 1) == 1) return;
        _audio?.DetachOutput();
        Faulted?.Invoke(message);
    }

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        var process = _process;
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { /* already exited */ }
            try { process.WaitForExit(3000); } catch { }
        }
        _audio?.DetachOutput();
        _stdoutThread?.Join(2000);
        _stderrThread?.Join(1000);
        try { process?.Dispose(); } catch { }
        _process = null;
    }

    public void Dispose() => Stop();
}
