using System.Buffers;
using System.Diagnostics;
using System.Text;
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

    public ReplayRecorder(ReplayBuffer buffer) => _buffer = buffer;

    /// <summary>Raised once when the first fragment arrived (capture + encoder are working).</summary>
    public event Action? Running;

    /// <summary>Raised when ffmpeg exits or the stream breaks while not stopping. Argument: error text.</summary>
    public event Action<string>? Faulted;

    public DateTime StartedUtc { get; private set; }
    public bool HasProducedData { get; private set; }

    public void Start(string ffmpegPath, IReadOnlyList<string> args, AudioEngine? audio)
    {
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
        StartedUtc = DateTime.UtcNow;
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
                if (string.IsNullOrWhiteSpace(line)) continue;
                lock (_stderrTail)
                {
                    _stderrTail.Enqueue(line.Trim());
                    while (_stderrTail.Count > 20) _stderrTail.Dequeue();
                }
                Log.Warn("ffmpeg: " + line.Trim());
            }
        }
        catch { /* process gone */ }
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
