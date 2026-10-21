using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using ClipRing.Audio;
using ClipRing.Capture;
using ClipRing.Core;
using ClipRing.Games;
using ClipRing.Highlights;
using ClipRing.Native;

namespace ClipRing.Services;

/// <summary>
/// Orchestrates everything: finds ffmpeg, probes monitors and encoders, runs the recorder,
/// mixes audio, detects games, saves clips and reacts to settings changes.
/// </summary>
public sealed class ReplayService : ObservableObject, IDisposable
{
    private static readonly TimeSpan RestartDebounce = TimeSpan.FromSeconds(1.2);

    private readonly Dispatcher _ui;
    private readonly AudioEngine _audio = new();
    private readonly ReplayBuffer _buffer = new();
    private readonly ClipSaver _saver = new();
    private readonly GameLibrary _library;
    private readonly GameDetector _games;
    private readonly HighlightManager _highlights;
    private readonly SemaphoreSlim _life = new(1, 1);
    private readonly object _timerGate = new();
    private readonly Timer _statsTimer;
    private Timer? _restartTimer;
    private Timer? _saveTimer;

    private ReplayRecorder? _recorder;
    private PipelineSpec? _pipeline;
    private bool _pipelineHasAudio;
    private Cs2GameStateServer? _cs2;
    private ValorantKillDetector? _valorant;
    private bool _userPaused;
    private int _failures;
    private int _statsTick;
    private int _statsBusy;
    private DateTime _gameGoneSinceUtc = DateTime.MinValue;
    private bool _disposed;

    // Watchdog: ffmpeg running but no data. After repeated failures with audio, record without it.
    private static readonly TimeSpan NoDataTimeout = TimeSpan.FromSeconds(20);
    private ReplayRecorder? _watchdogFiredFor;
    private int _noDataWithAudio;
    private bool _audioFallback;
    private ReplayRecorder? _slowEncoderWarnedFor;

    public ReplayService(AppSettings settings, Dispatcher ui)
    {
        Settings = settings;
        _ui = ui;
        _library = new GameLibrary();
        _games = new GameDetector(settings, _library);
        _highlights = new HighlightManager(settings);
        _statsTimer = new Timer(_ => UpdateStats(), null, Timeout.Infinite, Timeout.Infinite);
        TotalRamBytes = Win32.TotalPhysicalMemory();
    }

    // ================================================================ bindable state
    public AppSettings Settings { get; }
    public GameLibrary Library => _library;
    public ulong TotalRamBytes { get; }

    private FfmpegInfo? _ffmpeg;
    public FfmpegInfo? Ffmpeg
    {
        get => _ffmpeg;
        private set
        {
            if (Set(ref _ffmpeg, value))
            {
                OnPropertyChanged(nameof(FfmpegText));
                OnPropertyChanged(nameof(EncoderAvailability));
            }
        }
    }

    public string FfmpegText => _ffmpeg is null ? "Nicht gefunden" : $"Version {_ffmpeg.Version} in {_ffmpeg.Path}";

    private IReadOnlyList<MonitorInfo> _monitors = Array.Empty<MonitorInfo>();
    public IReadOnlyList<MonitorInfo> Monitors
    {
        get => _monitors;
        private set { if (Set(ref _monitors, value)) NotifyEstimates(); }
    }

    private ReplayState _state = ReplayState.Initializing;
    public ReplayState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(StateTitle));
            }
        }
    }

    public bool IsRunning => _state == ReplayState.Running;
    public bool IsPaused => _userPaused;

    public string StateTitle => _state switch
    {
        ReplayState.Running => "Replay läuft",
        ReplayState.Starting => "Replay startet",
        ReplayState.Paused => "Replay pausiert",
        ReplayState.WaitingForGame => "Wartet auf ein Spiel",
        ReplayState.Error => "Replay angehalten",
        _ => "Wird vorbereitet",
    };

    private string _statusText = "";
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    private string? _lastError;
    public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }

    private string _encoderText = "–";
    public string EncoderText { get => _encoderText; private set => Set(ref _encoderText, value); }

    private string _outputText = "–";
    public string OutputText { get => _outputText; private set => Set(ref _outputText, value); }

    private double _bufferedSeconds;
    public double BufferedSeconds { get => _bufferedSeconds; private set => Set(ref _bufferedSeconds, value); }

    private double _bufferFill;
    public double BufferFill { get => _bufferFill; private set => Set(ref _bufferFill, value); }

    private string _bufferBytesText = "0 MB";
    public string BufferBytesText { get => _bufferBytesText; private set => Set(ref _bufferBytesText, value); }

    private string _gameText = "Kein Spiel im Vordergrund";
    public string GameText { get => _gameText; private set => Set(ref _gameText, value); }

    private string _foregroundText = "";
    public string ForegroundText { get => _foregroundText; private set => Set(ref _foregroundText, value); }

    private float _systemPeak;
    public float SystemPeak { get => _systemPeak; private set => Set(ref _systemPeak, value); }

    private float _micPeak;
    public float MicPeak { get => _micPeak; private set => Set(ref _micPeak, value); }

    private string _cs2StatusText = "Aus";
    public string Cs2StatusText { get => _cs2StatusText; private set => Set(ref _cs2StatusText, value); }

    private string _valorantStatusText = "Aus";
    public string ValorantStatusText { get => _valorantStatusText; private set => Set(ref _valorantStatusText, value); }

    private double _valorantScore;
    public double ValorantScore { get => _valorantScore; private set => Set(ref _valorantScore, value); }

    private bool _isSaving;
    public bool IsSaving { get => _isSaving; private set => Set(ref _isSaving, value); }

    public ObservableCollection<SavedClip> RecentClips { get; } = new();

    /// <summary>(title, message) for tray notifications.</summary>
    public event Action<string, string>? Notify;

    /// <summary>Raised on the UI thread after a clip was written.</summary>
    public event Action<SavedClip>? ClipSaved;

    // ---------------------------------------------------------------- estimates for the settings page
    public MonitorInfo? SelectedMonitor =>
        (Settings.MonitorDevice.Length > 0 ? _monitors.FirstOrDefault(m => m.DeviceName == Settings.MonitorDevice) : null)
        ?? _monitors.FirstOrDefault(m => m.Index == Settings.MonitorIndex)
        ?? _monitors.FirstOrDefault(m => m.IsPrimary)
        ?? _monitors.FirstOrDefault();

    public (int Width, int Height) EstimatedOutputSize
    {
        get
        {
            var m = SelectedMonitor;
            return m is null ? (1920, 1080) : PipelineBuilder.OutputSize(Settings.Resolution, m.Width, m.Height);
        }
    }

    public int EstimatedBitrateKbps
    {
        get
        {
            var (w, h) = EstimatedOutputSize;
            return PipelineBuilder.ComputeBitrateKbps(Settings, w, h);
        }
    }

    public string EstimatedBitrateText => $"{EstimatedBitrateKbps / 1000.0:0.#} Mbit/s";

    public string EstimatedOutputText
    {
        get
        {
            var (w, h) = EstimatedOutputSize;
            return $"{w} × {h}";
        }
    }

    public long EstimatedRamBytes => PipelineBuilder.EstimateBufferBytes(Settings, EstimatedBitrateKbps);

    public string EstimatedRamText => $"ca. {FormatBytes(EstimatedRamBytes)} Arbeitsspeicher";

    public bool RamWarning => TotalRamBytes > 0 && EstimatedRamBytes > (long)(TotalRamBytes * 0.35);

    public string BufferLengthText => FormatDuration(Settings.BufferSeconds);

    public Dictionary<EncoderBackend, bool> EncoderAvailability => PipelineSelector.Availability(_ffmpeg, Settings.Codec);

    private void NotifyEstimates()
    {
        OnPropertyChanged(nameof(SelectedMonitor));
        OnPropertyChanged(nameof(EstimatedOutputSize));
        OnPropertyChanged(nameof(EstimatedBitrateKbps));
        OnPropertyChanged(nameof(EstimatedBitrateText));
        OnPropertyChanged(nameof(EstimatedOutputText));
        OnPropertyChanged(nameof(EstimatedRamBytes));
        OnPropertyChanged(nameof(EstimatedRamText));
        OnPropertyChanged(nameof(RamWarning));
        OnPropertyChanged(nameof(BufferLengthText));
    }

    public static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
        : $"{bytes / (1024.0 * 1024):0} MB";

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ================================================================ lifecycle
    public async Task InitializeAsync()
    {
        Settings.PropertyChanged += OnSettingsChanged;
        _games.CurrentChanged += OnGameChanged;
        _games.Start();
        _highlights.ClipRequested += clip => _ = SaveClipAsync(clip.Seconds, clip.Tag, clip.Game, "Highlight");
        _audio.SyncLost += () => ScheduleRestart("Audio-Uhr", TimeSpan.FromSeconds(1), reprobe: false);
        _audio.Start();
        ApplyGains();
        await Task.Run(ApplyAudioConfig);
        _ = Task.Run(LoadRecentClips);
        _statsTimer.Change(TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(250));

        if (await DetectEnvironmentAsync())
        {
            ApplyHighlights();
            await RestartAsync("Start");
        }
    }

    /// <summary>Finds ffmpeg and probes monitors. Returns false (and sets an error) if capture is impossible.</summary>
    private async Task<bool> DetectEnvironmentAsync()
    {
        State = ReplayState.Initializing;
        StatusText = "FFmpeg wird gesucht …";
        Ffmpeg = await FfmpegLocator.DetectAsync(Settings.FfmpegPath);
        if (Ffmpeg is null)
        {
            SetError("FFmpeg wurde nicht gefunden. Lege ffmpeg.exe in den Ordner „ffmpeg“ neben ClipRing.exe " +
                     "oder wähle den Pfad unter „Allgemein“ aus.");
            return false;
        }
        if (!Ffmpeg.HasFilter("ddagrab"))
        {
            SetError($"Dieses FFmpeg ({Ffmpeg.Version}) kann den Bildschirm nicht per Desktop Duplication aufnehmen. " +
                     "Benötigt wird ein Windows-Build ab Version 7.1, empfohlen 8.1 oder neuer.");
            return false;
        }

        StatusText = "Monitore werden erkannt …";
        await RefreshMonitorsAsync();
        if (Monitors.Count == 0)
        {
            SetError("Kein Monitor kann aufgenommen werden. Läuft eine Remotedesktop-Sitzung oder ist der Bildschirm gesperrt?");
            return false;
        }
        return true;
    }

    private async Task RefreshMonitorsAsync()
    {
        if (Ffmpeg is null) return;
        try
        {
            var monitors = await DisplayProbe.ProbeAsync(Ffmpeg);
            if (monitors.Count == 0) return;
            Monitors = monitors;
            SyncMonitorSelection();
        }
        catch (Exception ex)
        {
            Log.Warn("Monitorerkennung fehlgeschlagen: " + ex.Message);
        }
    }

    /// <summary>
    /// Keeps MonitorIndex (what the UI shows) and MonitorDevice (what identifies the monitor) consistent.
    /// Up to v0.1 MonitorIndex was the ddagrab output index; that selection is carried over once.
    /// </summary>
    private void SyncMonitorSelection()
    {
        MonitorInfo? selected;
        if (Settings.MonitorDevice.Length == 0)
        {
            selected = _monitors.FirstOrDefault(m => m.DdaIndex == Settings.MonitorIndex)
                       ?? _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.FirstOrDefault();
            if (selected is not null) Log.Info($"Monitorauswahl übernommen: {selected.Label} ({selected.DeviceName})");
        }
        else
        {
            selected = SelectedMonitor;
        }
        if (selected is null) return;
        // Device first: the MonitorIndex handler then sees no change of monitor and does not restart.
        Settings.MonitorDevice = selected.DeviceName;
        Settings.MonitorIndex = selected.Index;
    }

    /// <summary>Re-detects ffmpeg + hardware (after the user changed the ffmpeg path or drivers).</summary>
    public async Task RedetectAsync()
    {
        await _life.WaitAsync();
        try { StopRecorder(); }
        finally { _life.Release(); }

        PipelineSelector.InvalidateCache(Settings);
        ResetAudioFallback();
        if (await DetectEnvironmentAsync())
        {
            ApplyHighlights();
            await RestartAsync("Neu erkennen");
        }
    }

    private void SetError(string message)
    {
        LastError = message;
        StatusText = message;
        State = ReplayState.Error;
        Log.Warn(message);
    }

    private void StopRecorder()
    {
        var recorder = _recorder;
        _recorder = null;
        if (recorder is null) return;
        recorder.Stop();
        recorder.Dispose();
    }

    public async Task RestartAsync(string reason)
    {
        await _life.WaitAsync();
        try
        {
            if (_disposed) return;
            StopRecorder();
            _buffer.Reset(null);
            BufferedSeconds = 0;

            if (Ffmpeg is null) return;
            if (_userPaused)
            {
                State = ReplayState.Paused;
                StatusText = "Mit dem Hotkey oder hier wieder starten.";
                return;
            }
            if (Settings.OnlyWhenGameRunning && !_games.IsGameRunning)
            {
                State = ReplayState.WaitingForGame;
                StatusText = "Die Aufnahme beginnt, sobald ein Spiel erkannt wird.";
                return;
            }

            var monitor = SelectedMonitor;
            if (monitor is null)
            {
                SetError("Kein Monitor verfügbar.");
                return;
            }

            Log.Info($"Starte Replay ({reason})");
            State = ReplayState.Starting;
            StatusText = "Encoder wird ausgewählt …";
            var pipeline = await PipelineSelector.SelectAsync(Ffmpeg, Settings, monitor, msg => StatusText = msg, CancellationToken.None);
            if (pipeline is null)
            {
                SetError("Kein Encoder hat den Test bestanden. Prüfe Grafiktreiber und FFmpeg-Build; Details stehen im Protokoll.");
                return;
            }
            ScheduleSettingsSave();

            bool withAudio = (Settings.CaptureSystemAudio || Settings.CaptureMicrophone) && !_audioFallback;
            if (_audioFallback) Log.Warn("Starte ohne Tonspur (Ausweichbetrieb nach Startproblemen mit Ton)");
            double? audioStart = null;
            if (withAudio) audioStart = _audio.BeginStream() + Settings.AudioOffsetMs / 1000.0;

            var args = PipelineBuilder.BuildRecordArgs(pipeline, monitor, Settings, audioStart);
            _buffer.TargetSeconds = Settings.BufferSeconds;

            var recorder = new ReplayRecorder(_buffer);
            recorder.Running += () => OnRecorderRunning(recorder);
            recorder.Faulted += message => OnRecorderFaulted(recorder, message);
            _recorder = recorder; // before Start: its events may fire right away
            _pipeline = pipeline;
            _pipelineHasAudio = withAudio;
            try
            {
                recorder.Start(Ffmpeg.Path, args, withAudio ? _audio : null);
            }
            catch (Exception ex)
            {
                _recorder = null;
                _audio.DetachOutput();
                recorder.Dispose();
                SetError("FFmpeg konnte nicht gestartet werden: " + ex.Message);
                return;
            }

            EncoderText = $"{pipeline.EncoderLabel}, {pipeline.PathLabel}, {pipeline.SourceLabel}";
            OutputText = $"{pipeline.OutWidth} × {pipeline.OutHeight}, {Settings.Fps} fps, {pipeline.BitrateKbps / 1000.0:0.#} Mbit/s";
            StatusText = "Aufnahme startet …";
            LastError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Replay konnte nicht gestartet werden", ex);
            SetError("Replay konnte nicht gestartet werden: " + ex.Message);
        }
        finally
        {
            _life.Release();
        }
    }

    private void OnRecorderRunning(ReplayRecorder recorder)
    {
        if (!ReferenceEquals(recorder, _recorder)) return;
        if (_pipelineHasAudio) _noDataWithAudio = 0;
        State = ReplayState.Running;
        StatusText = _audioFallback
            ? "Läuft ohne Ton: Mit Tonspur hat FFmpeg keine Daten geliefert. Details stehen im Protokoll."
            : $"Hotkey {Settings.HotkeySave} speichert die letzten {FormatDuration(Settings.BufferSeconds)}.";
        Log.Info($"Replay läuft ({(_pipelineHasAudio ? "mit" : "ohne")} Ton)");
    }

    /// <summary>Called every 2 s: detects an ffmpeg that runs but never delivers data.</summary>
    private void CheckRecorderWatchdog()
    {
        var recorder = _recorder;
        if (recorder is null || recorder.HasProducedData || ReferenceEquals(recorder, _watchdogFiredFor)) return;
        if (recorder.StartedUtc == default || DateTime.UtcNow - recorder.StartedUtc < NoDataTimeout) return;

        _watchdogFiredFor = recorder;
        Log.Warn($"Keine Daten von FFmpeg nach {NoDataTimeout.TotalSeconds:0} s. {recorder.DiagnosticSummary()}");
        if (_pipelineHasAudio && ++_noDataWithAudio >= 2 && !_audioFallback)
        {
            _audioFallback = true;
            Log.Warn("Zweimal keine Daten mit Tonspur: nächster Versuch ohne Ton, um die Ursache einzugrenzen");
            Notify?.Invoke("Aufnahme vorerst ohne Ton",
                           "Mit Tonspur hat FFmpeg keine Daten geliefert. Details stehen im Protokoll.");
        }
        OnRecorderFaulted(recorder, $"FFmpeg hat nach {NoDataTimeout.TotalSeconds:0} s noch keine Daten geliefert");
    }

    /// <summary>Warns once per recording when clearly fewer frames than requested are recorded.</summary>
    private void CheckEncoderSpeed()
    {
        var recorder = _recorder;
        if (recorder is null || !recorder.HasProducedData || ReferenceEquals(recorder, _slowEncoderWarnedFor)) return;
        if (DateTime.UtcNow - recorder.StartedUtc < TimeSpan.FromSeconds(25)) return;
        if (recorder.RecentFps is not double fps || fps >= Settings.Fps * 0.9) return;

        bool wgc = _pipeline?.Source == VideoSource.Wgc;
        // Graphics Capture only delivers changed frames: a quiet desktop is not a problem, a game is.
        if (wgc && _games.Current is null) return;

        _slowEncoderWarnedFor = recorder;
        int hz = SelectedMonitor?.RefreshHz ?? 0;
        string message = wgc && hz > 0 && hz < Settings.Fps * 0.95
            ? $"Es kommen nur {fps:0} von {Settings.Fps} Bildern pro Sekunde an: der Monitor läuft mit {hz} Hz, und mehr Bilder " +
              "liefert Windows Graphics Capture nicht. In den Windows-Anzeigeeinstellungen die Bildwiederholrate erhöhen oder weniger fps wählen."
            : wgc
            ? $"Es kommen nur {fps:0} von {Settings.Fps} Bildern pro Sekunde an. Entweder läuft das Spiel selbst langsamer, " +
              "oder die Grafikkarte kommt nicht mit: dann Encoder-Tempo „Schnell“, weniger fps oder eine kleinere Auflösung wählen. " +
              "Läuft gleichzeitig NVIDIA Instant Replay, teilen sich beide den Encoder."
            : $"Es kommen nur {fps:0} von {Settings.Fps} Bildern pro Sekunde an, Clips ruckeln dadurch. " +
              "Unter „Video“ als Aufnahmemethode „Windows Graphics Capture“ wählen, sonst Encoder-Tempo „Schnell“ oder weniger fps.";
        Log.Warn($"Zu wenige Bilder: {fps:0.0} fps statt {Settings.Fps} ({_pipeline?.EncoderLabel}, {_pipeline?.SourceLabel}, " +
                 $"{Settings.Speed}, {_pipeline?.OutWidth}x{_pipeline?.OutHeight}, Monitor {(hz > 0 ? $"{hz} Hz" : "Hz unbekannt")})");
        StatusText = message;
        Notify?.Invoke("Aufnahme ruckelt", $"Nur {fps:0} von {Settings.Fps} fps. Details auf der Übersicht.");
    }

    private void ResetAudioFallback()
    {
        _noDataWithAudio = 0;
        if (!_audioFallback) return;
        _audioFallback = false;
        Log.Info("Tonspur wird wieder versucht");
    }

    private void OnRecorderFaulted(ReplayRecorder recorder, string message)
    {
        if (!ReferenceEquals(recorder, _recorder) || _disposed) return;

        var ranFor = DateTime.UtcNow - recorder.StartedUtc;
        if (recorder.HasProducedData && ranFor > TimeSpan.FromMinutes(1)) _failures = 0;
        _failures++;

        bool early = !recorder.HasProducedData || ranFor < TimeSpan.FromSeconds(10);
        if (early && _pipeline?.FromCache == true) PipelineSelector.InvalidateCache(Settings);
        if (!recorder.HasProducedData && !ReferenceEquals(recorder, _watchdogFiredFor))
            Log.Warn("Diagnose: " + recorder.DiagnosticSummary());

        var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(_failures, 5))));
        Log.Warn($"Aufnahme abgebrochen ({message}), neuer Versuch in {delay.TotalSeconds:0}s");
        LastError = message;
        State = ReplayState.Error;
        StatusText = $"Aufnahme unterbrochen, neuer Versuch in {delay.TotalSeconds:0} s. Ursache: {message}";
        ScheduleRestart("Fehler", delay, reprobe: true);
    }

    private void ScheduleRestart(string reason, TimeSpan delay, bool reprobe)
    {
        if (_disposed) return;
        lock (_timerGate)
        {
            _restartTimer?.Dispose();
            _restartTimer = new Timer(async _ =>
            {
                try
                {
                    // Resolution changes (fullscreen games switching modes) end the capture: re-measure.
                    if (reprobe) await RefreshMonitorsAsync();
                    await RestartAsync(reason);
                }
                catch (Exception ex)
                {
                    Log.Error("Neustart fehlgeschlagen", ex);
                }
            }, null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void TogglePause()
    {
        _userPaused = !_userPaused;
        OnPropertyChanged(nameof(IsPaused));
        Notify?.Invoke(_userPaused ? "Replay pausiert" : "Replay läuft wieder",
                       _userPaused ? "Es wird nichts aufgezeichnet." : "Der Puffer füllt sich neu.");
        ScheduleRestart(_userPaused ? "Pause" : "Fortsetzen", TimeSpan.Zero, reprobe: false);
    }

    // ================================================================ clips
    public async Task<SavedClip?> SaveClipAsync(double seconds, string? tag = null, string? gameOverride = null,
                                                string reason = "Hotkey")
    {
        // Confirmation sounds only for saves the player triggered; automatic highlights stay silent.
        bool manual = reason != "Highlight";
        var ffmpeg = Ffmpeg;
        var recorder = _recorder;
        if (ffmpeg is null || recorder is null || !recorder.HasProducedData)
        {
            Log.Warn($"Clip nicht gespeichert ({reason}{(tag is null ? "" : ", " + tag)}): Replay läuft nicht (Zustand: {StateTitle})");
            Notify?.Invoke("Kein Clip gespeichert", "Das Replay läuft gerade nicht.");
            if (manual && Settings.PlaySoundOnSave) Sounds.PlayFailed();
            return null;
        }

        string game = gameOverride ?? _games.ResolveForClip()?.DisplayName ?? "Desktop";
        var request = new ClipRequest(Math.Min(seconds, Settings.BufferSeconds), game, DateTime.Now, tag, reason);
        IsSaving = true;
        try
        {
            var clip = await _saver.SaveAsync(_buffer, ffmpeg, Settings, request);
            await _ui.InvokeAsync(() =>
            {
                RecentClips.Insert(0, clip);
                while (RecentClips.Count > 30) RecentClips.RemoveAt(RecentClips.Count - 1);
                ClipSaved?.Invoke(clip);
            });
            if (Settings.NotifyOnSave)
                Notify?.Invoke("Clip gespeichert", $"{clip.Game}: {clip.DurationText} min, {clip.SizeText}");
            if (manual && Settings.PlaySoundOnSave) Sounds.PlaySaved();
            return clip;
        }
        catch (Exception ex)
        {
            Log.Error("Clip konnte nicht gespeichert werden", ex);
            Notify?.Invoke("Clip nicht gespeichert", ex.Message);
            if (manual && Settings.PlaySoundOnSave) Sounds.PlayFailed();
            return null;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void LoadRecentClips()
    {
        try
        {
            var root = Settings.OutputFolder;
            if (!Directory.Exists(root)) return;

            // Leftovers of interrupted saves (".<name>.part.mp4") older than 10 minutes.
            foreach (var part in new DirectoryInfo(root).EnumerateFiles(".*.part.mp4", SearchOption.AllDirectories))
            {
                if (DateTime.Now - part.LastWriteTime < TimeSpan.FromMinutes(10)) continue;
                try
                {
                    part.Delete();
                    Log.Info("Reste eines abgebrochenen Speichervorgangs gelöscht: " + part.FullName);
                }
                catch { /* still in use */ }
            }

            var files = new DirectoryInfo(root)
                .EnumerateFiles("*.mp4", SearchOption.AllDirectories)
                .Where(f => !f.Name.StartsWith('.'))
                .OrderByDescending(f => f.LastWriteTime)
                .Take(15)
                .Select(f => new SavedClip(
                    f.FullName,
                    string.Equals(f.DirectoryName, root, StringComparison.OrdinalIgnoreCase) ? "" : f.Directory?.Name ?? "",
                    0, f.Length, null, f.LastWriteTime))
                .ToList();
            _ui.InvokeAsync(() =>
            {
                foreach (var clip in files)
                {
                    if (RecentClips.All(c => c.FilePath != clip.FilePath)) RecentClips.Add(clip);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("Letzte Clips konnten nicht gelesen werden: " + ex.Message);
        }
    }

    public void RemoveFromRecent(SavedClip clip) => RecentClips.Remove(clip);

    // ================================================================ reactions
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        string name = e.PropertyName ?? "";
        ScheduleSettingsSave();

        if (name == nameof(AppSettings.FfmpegPath))
        {
            _ = Task.Run(RedetectAsync);
            return;
        }
        if (AppSettings.PipelineProperties.Contains(name))
        {
            NotifyEstimates();
            if (name == nameof(AppSettings.Codec)) OnPropertyChanged(nameof(EncoderAvailability));
            ScheduleRestart(name, RestartDebounce, reprobe: false);
            return;
        }

        switch (name)
        {
            case nameof(AppSettings.MonitorIndex):
            {
                // The user picked another monitor in the list (or SyncMonitorSelection aligned the index).
                var picked = _monitors.FirstOrDefault(m => m.Index == Settings.MonitorIndex);
                if (picked is null || picked.DeviceName == Settings.MonitorDevice) break;
                Settings.MonitorDevice = picked.DeviceName;
                NotifyEstimates();
                ScheduleRestart(name, RestartDebounce, reprobe: false);
                break;
            }
            case nameof(AppSettings.BufferSeconds):
                _buffer.TargetSeconds = Settings.BufferSeconds;
                NotifyEstimates();
                break;
            case nameof(AppSettings.SystemVolume):
            case nameof(AppSettings.MicrophoneVolume):
                ApplyGains();
                break;
            case nameof(AppSettings.CaptureSystemAudio):
            case nameof(AppSettings.CaptureMicrophone):
            case nameof(AppSettings.SystemAudioDeviceId):
            case nameof(AppSettings.MicrophoneDeviceId):
                _ = Task.Run(ApplyAudioConfig);
                NotifyEstimates();
                bool wasFallback = _audioFallback;
                ResetAudioFallback();
                // Only the presence of an audio track needs a new ffmpeg process.
                bool wantsAudio = Settings.CaptureSystemAudio || Settings.CaptureMicrophone;
                if (_recorder is not null && (wantsAudio != _pipelineHasAudio || wasFallback))
                    ScheduleRestart(name, RestartDebounce, reprobe: false);
                break;
            case nameof(AppSettings.OnlyWhenGameRunning):
                ScheduleRestart(name, TimeSpan.FromMilliseconds(300), reprobe: false);
                break;
            case nameof(AppSettings.RateControl):
                NotifyEstimates();
                break;
            case nameof(AppSettings.StartWithWindows):
            case nameof(AppSettings.StartMinimized):
                Autostart.Apply(Settings.StartWithWindows, Settings.StartMinimized);
                break;
            case nameof(AppSettings.Cs2Enabled):
            case nameof(AppSettings.Cs2Port):
            case nameof(AppSettings.Cs2Token):
            case nameof(AppSettings.Cs2IgnoreWarmup):
            case nameof(AppSettings.ValorantEnabled):
            case nameof(AppSettings.ValorantThreshold):
            case nameof(AppSettings.ValorantRegionX):
            case nameof(AppSettings.ValorantRegionY):
            case nameof(AppSettings.ValorantRegionW):
            case nameof(AppSettings.ValorantRegionH):
                ApplyHighlights();
                break;
            case nameof(AppSettings.OutputFolder):
                _ = Task.Run(LoadRecentClips);
                break;
        }
    }

    private void ApplyGains()
    {
        _audio.SystemGain = (float)Settings.SystemVolume;
        _audio.MicGain = (float)Settings.MicrophoneVolume;
    }

    private void ApplyAudioConfig()
        => _audio.Configure(Settings.CaptureSystemAudio, Settings.SystemAudioDeviceId,
                            Settings.CaptureMicrophone, Settings.MicrophoneDeviceId);

    private void OnGameChanged(GameInfo? game)
    {
        GameText = game?.DisplayName ?? "Kein Spiel im Vordergrund";
        if (game is not null && Settings.OnlyWhenGameRunning && State == ReplayState.WaitingForGame)
            ScheduleRestart("Spiel erkannt", TimeSpan.FromMilliseconds(500), reprobe: false);
    }

    private void ApplyHighlights()
    {
        // CS2: (re)start the local GSI receiver.
        _cs2?.Dispose();
        _cs2 = null;
        if (Settings.Cs2Enabled)
        {
            try
            {
                var server = new Cs2GameStateServer(Settings.Cs2Port, Settings.Cs2Token, Settings.Cs2IgnoreWarmup);
                server.KillsDetected += (kills, hs, mode) =>
                {
                    if (!Cs2ModeAllowed(mode))
                    {
                        if (mode != _cs2IgnoredMode)
                            Log.Info($"CS2-Kills im Modus {Cs2GameStateServer.ModeName(mode)} werden ignoriert (Einstellung)");
                        _cs2IgnoredMode = mode;
                        return;
                    }
                    _cs2IgnoredMode = null;
                    _highlights.ReportKills(Cs2GameStateServer.GameName, kills, hs, Settings.Cs2MinKills);
                };
                server.Start();
                _cs2 = server;
            }
            catch (Exception ex)
            {
                Cs2StatusText = $"Port {Settings.Cs2Port} ist belegt ({ex.Message}). Wähle einen anderen Port.";
                Log.Warn("CS2-GSI-Empfänger: " + ex.Message);
            }
        }
        else
        {
            Cs2StatusText = "Aus";
        }

        // VALORANT: detector is (re)created by UpdateValorantDetector when the game runs.
        StopValorantDetector();
    }

    private string? _cs2IgnoredMode;

    private bool Cs2ModeAllowed(string? mode) => Settings.Cs2Modes switch
    {
        Cs2ModeFilter.All => true,
        Cs2ModeFilter.CompetitiveAndWingman => mode is "competitive" or "premier" or "scrimcomp2v2",
        _ => mode is "competitive" or "premier",
    };

    private void StopValorantDetector()
    {
        var detector = _valorant;
        _valorant = null;
        detector?.Dispose();
    }

    private void UpdateValorantDetector()
    {
        bool shouldRun = Settings.ValorantEnabled && Ffmpeg is not null && SelectedMonitor is { DdaIndex: >= 0 } &&
                         Ffmpeg.HasFilter("ddagrab") && IsProcessRunning(ValorantKillDetector.ProcessName);
        if (shouldRun && _valorant is null)
        {
            try
            {
                var detector = new ValorantKillDetector(SelectedMonitor!, Settings);
                detector.KillDetected += () =>
                    _highlights.ReportKills(ValorantKillDetector.GameName, 1, 0, Settings.ValorantMinKills);
                detector.Start(Ffmpeg!);
                _valorant = detector;
            }
            catch (Exception ex)
            {
                Log.Warn("Valorant-Erkennung konnte nicht starten: " + ex.Message);
            }
        }
        else if (!shouldRun && _valorant is not null)
        {
            StopValorantDetector();
        }

        if (!Settings.ValorantEnabled) ValorantStatusText = "Aus";
        else if (_valorant is null) ValorantStatusText = "Wartet auf VALORANT";
        else if (_valorant.TemplateCount == 0)
            ValorantStatusText = $"Keine Vorlage: im Spiel nach einem Kill {Settings.HotkeyValorantCalibrate} drücken.";
        else ValorantStatusText = $"Aktiv mit {_valorant.TemplateCount} Vorlage(n)";
    }

    private static bool IsProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Hotkey handler: store the current kill-banner area as reference image.</summary>
    public void CaptureValorantTemplate()
    {
        var detector = _valorant;
        if (detector is null)
        {
            Notify?.Invoke("Keine Vorlage gespeichert", "Die VALORANT-Erkennung ist nicht aktiv (Spiel läuft nicht oder Funktion aus).");
            return;
        }
        var path = detector.CaptureTemplate();
        Notify?.Invoke(path is null ? "Keine Vorlage gespeichert" : "Vorlage gespeichert",
                       path is null ? "Noch kein Bild vom Bannerbereich empfangen." : Path.GetFileName(path));
        if (path is not null && Settings.PlaySoundOnSave) Sounds.PlaySaved();
    }

    public string ValorantTemplateFolder => _valorant?.TemplateFolder ?? AppPaths.ValorantTemplateDir;

    private void UpdateStats()
    {
        if (Interlocked.Exchange(ref _statsBusy, 1) == 1) return; // timer callbacks may overlap
        try
        {
            var (seconds, bytes, _) = _buffer.Stats;
            bool producing = _recorder?.HasProducedData == true;
            BufferedSeconds = producing ? seconds : 0;
            BufferFill = producing && Settings.BufferSeconds > 0 ? Math.Clamp(seconds / Settings.BufferSeconds, 0, 1) : 0;
            BufferBytesText = FormatBytes(producing ? bytes : 0);
            SystemPeak = _audio.SystemPeak;
            MicPeak = _audio.MicPeak;
            ForegroundText = _games.ForegroundDescription;
            ValorantScore = _valorant?.LastScore ?? 0;

            if (++_statsTick % 8 != 0) return; // the rest every 2 s

            CheckRecorderWatchdog();
            CheckEncoderSpeed();

            if (_cs2 is not null)
            {
                var last = _cs2.LastMessageUtc;
                Cs2StatusText = last == DateTime.MinValue
                    ? "Bereit. Warte auf Daten von CS2 (nach dem Einrichten das Spiel neu starten)."
                    : DateTime.UtcNow - last < TimeSpan.FromSeconds(45)
                        ? $"Verbunden{(string.IsNullOrEmpty(_cs2.LastMap) ? "" : $", {_cs2.LastMap}")}" +
                          (_cs2.LastMode is null ? "" :
                              $", {Cs2GameStateServer.ModeName(_cs2.LastMode)}{(Cs2ModeAllowed(_cs2.LastMode) ? "" : " (keine Highlights)")}")
                        : $"Letzte Daten vor {(DateTime.UtcNow - last).TotalMinutes:0} min";
            }

            UpdateValorantDetector();

            // "Only while a game runs": stop 15 s after the game process ended.
            if (Settings.OnlyWhenGameRunning && (State == ReplayState.Running || State == ReplayState.Starting))
            {
                if (_games.IsGameRunning) _gameGoneSinceUtc = DateTime.MinValue;
                else if (_gameGoneSinceUtc == DateTime.MinValue) _gameGoneSinceUtc = DateTime.UtcNow;
                else if (DateTime.UtcNow - _gameGoneSinceUtc > TimeSpan.FromSeconds(15))
                {
                    _gameGoneSinceUtc = DateTime.MinValue;
                    ScheduleRestart("Spiel beendet", TimeSpan.Zero, reprobe: false);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Statusaktualisierung: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _statsBusy, 0);
        }
    }

    private void ScheduleSettingsSave()
    {
        lock (_timerGate)
        {
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ => SettingsStore.Save(Settings), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Settings.PropertyChanged -= OnSettingsChanged;
        lock (_timerGate)
        {
            _restartTimer?.Dispose();
            _saveTimer?.Dispose();
        }
        _statsTimer.Dispose();
        StopRecorder();
        StopValorantDetector();
        _cs2?.Dispose();
        _highlights.Dispose();
        _games.Dispose();
        _audio.Dispose();
        SettingsStore.Save(Settings);
    }
}
