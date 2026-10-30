using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using ClipRing.Audio;
using ClipRing.Capture;
using ClipRing.Core;
using ClipRing.Games;
using ClipRing.Highlights;
using ClipRing.Hotkeys;
using ClipRing.Localization;
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

    // Monitor mode the running recording was built for, and a changed mode seen once (confirmed on the next check).
    private MonitorInfo? _recordingMonitor;
    private string? _pendingModeKey;
    private int _modeChangeBusy;

    // "Spielfenster": window being recorded (0 = monitor), and a window that could not be recorded.
    private nint _recordingWindow;
    private nint _windowCaptureFailed;

    public ReplayService(AppSettings settings, Dispatcher ui)
    {
        Settings = settings;
        _ui = ui;
        _library = new GameLibrary();
        _games = new GameDetector(settings, _library);
        _highlights = new HighlightManager(settings);
        _statsTimer = new Timer(_ => UpdateStats(), null, Timeout.Infinite, Timeout.Infinite);
        TotalRamBytes = Win32.TotalPhysicalMemory();
        Loc.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Texts already on screen are re-rendered in the new language.</summary>
    private void OnLanguageChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(StateTitle), nameof(StatusText), nameof(FfmpegText), nameof(EncoderText), nameof(OutputText),
                     nameof(GameText), nameof(Cs2StatusText), nameof(EstimatedRamText), nameof(BufferBytesText),
                     nameof(CapabilitiesText),
                 })
            OnPropertyChanged(name);
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
            if (Set(ref _ffmpeg, value)) OnPropertyChanged(nameof(FfmpegText));
        }
    }

    public string FfmpegText => _ffmpeg is null ? Loc.T("Ffmpeg_NotFound")
        : EmbeddedFfmpeg.IsEmbeddedPath(_ffmpeg.Path)
            ? Loc.F("Ffmpeg_Embedded", _ffmpeg.Version)
            : Loc.F("Ffmpeg_External", _ffmpeg.Version, _ffmpeg.Path);

    private EncoderCapabilities _capabilities = EncoderCapabilities.Unknown(null);
    /// <summary>Which encoders/codecs this PC supports (after the hardware check).</summary>
    public EncoderCapabilities Capabilities
    {
        get => _capabilities;
        private set
        {
            _capabilities = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CapabilitiesText));
        }
    }

    public string CapabilitiesText => _capabilities.Describe();

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
        ReplayState.Running => Loc.T("State_Running"),
        ReplayState.Starting => Loc.T("State_Starting"),
        ReplayState.Paused => Loc.T("State_Paused"),
        ReplayState.WaitingForGame => Loc.T("State_Waiting"),
        ReplayState.Error => Loc.T("State_Error"),
        _ => Loc.T("State_Preparing"),
    };

    // Texts on screen are kept as "how to render" so a language switch can redraw them.
    private Func<string> _status = () => "";
    public string StatusText => _status();

    private void SetStatus(Func<string> text)
    {
        _status = text;
        OnPropertyChanged(nameof(StatusText));
    }

    private string? _lastError;
    public string? LastError { get => _lastError; private set => Set(ref _lastError, value); }

    private Func<string> _encoderText = () => "–";
    public string EncoderText => _encoderText();

    private Func<string> _outputText = () => "–";
    public string OutputText => _outputText();

    private double _bufferedSeconds;
    public double BufferedSeconds { get => _bufferedSeconds; private set => Set(ref _bufferedSeconds, value); }

    private double _bufferFill;
    public double BufferFill { get => _bufferFill; private set => Set(ref _bufferFill, value); }

    private string _bufferBytesText = "0 MB";
    public string BufferBytesText { get => _bufferBytesText; private set => Set(ref _bufferBytesText, value); }

    private string? _gameName;
    public string GameText => _gameName ?? Loc.T("Game_NoneInFront");

    private string _foregroundText = "";
    public string ForegroundText { get => _foregroundText; private set => Set(ref _foregroundText, value); }

    private float _systemPeak;
    public float SystemPeak { get => _systemPeak; private set => Set(ref _systemPeak, value); }

    private float _micPeak;
    public float MicPeak { get => _micPeak; private set => Set(ref _micPeak, value); }

    private Func<string> _cs2Status = () => Loc.T("Common_Off");
    public string Cs2StatusText => _cs2Status();

    private void SetCs2Status(Func<string> text)
    {
        _cs2Status = text;
        OnPropertyChanged(nameof(Cs2StatusText));
    }

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

    public string EstimatedRamText => Loc.F("Est_Ram", FormatBytes(EstimatedRamBytes));

    public bool RamWarning => TotalRamBytes > 0 && EstimatedRamBytes > (long)(TotalRamBytes * 0.35);

    public string BufferLengthText => FormatDuration(Settings.BufferSeconds);

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
        ? string.Format(Loc.Instance.Culture, "{0:0.0} GB", bytes / (1024.0 * 1024 * 1024))
        : string.Format(Loc.Instance.Culture, "{0:0} MB", bytes / (1024.0 * 1024));

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
    private async Task<bool> DetectEnvironmentAsync(bool forceEncoderCheck = false)
    {
        State = ReplayState.Initializing;
        SetStatus(() => Loc.T(EmbeddedFfmpeg.IsAvailable ? "Status_Preparing" : "Status_SearchingFfmpeg"));
        Ffmpeg = await FfmpegLocator.DetectAsync(Settings.FfmpegPath);
        if (Ffmpeg is null)
        {
            SetError(() => Loc.T(EmbeddedFfmpeg.IsAvailable ? "Err_EmbeddedFfmpeg" : "Err_NoFfmpeg"));
            return false;
        }
        var ffmpeg = Ffmpeg;
        if (!ffmpeg.HasFilter("ddagrab") && !ffmpeg.HasFilter("gfxcapture"))
        {
            SetError(() => Loc.F("Err_FfmpegCannotCapture", ffmpeg.Version));
            return false;
        }

        SetStatus(() => Loc.T("Status_CheckingEncoders"));
        Capabilities = await EncoderProbe.GetAsync(ffmpeg, forceEncoderCheck);
        AdjustToCapabilities();

        SetStatus(() => Loc.T("Status_DetectingMonitors"));
        await RefreshMonitorsAsync();
        if (Monitors.Count == 0)
        {
            SetError(() => Loc.T("Err_NoMonitor"));
            return false;
        }
        // The recording starts right after this: no extra restart from settings the profile changed.
        if (SelectedMonitor is { } monitor && ApplyMonitorModeProfile(monitor))
        {
            lock (_timerGate)
            {
                _restartTimer?.Dispose();
                _restartTimer = null;
            }
        }
        return true;
    }

    private static string ModeKey(int width, int height, int hz) => $"{width}x{height}@{hz}";

    private static string ModeKey(MonitorInfo m) => ModeKey(m.Width, m.Height, m.RefreshHz);

    /// <summary>
    /// Every 2 s: has the selected monitor switched to another resolution or refresh rate (a game in
    /// exclusive fullscreen, Windows display settings, or a dual-mode monitor switching between e.g. 4K 240 Hz
    /// and 1080p 480 Hz)? Graphics Capture keeps the size it started with, so the recording must be rebuilt.
    /// A new mode has to be seen twice in a row, because a switch takes a moment to settle.
    /// </summary>
    private void CheckMonitorMode()
    {
        var probed = SelectedMonitor;
        if (probed is null || Ffmpeg is null || Volatile.Read(ref _modeChangeBusy) == 1) return;

        var live = Win32.EnumerateMonitors();
        var match = live.FirstOrDefault(m => m.Device == probed.DeviceName);
        string liveKey = match.Device is null ? "gone" : ModeKey(match.Width, match.Height, match.RefreshHz);
        if (liveKey == ModeKey(probed))
        {
            _pendingModeKey = null;
            return;
        }
        if (_pendingModeKey != liveKey)
        {
            _pendingModeKey = liveKey;
            return;
        }

        _pendingModeKey = null;
        if (Interlocked.Exchange(ref _modeChangeBusy, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try { await OnMonitorModeChangedAsync(probed); }
            catch (Exception ex) { Log.Error("Monitormodus-Wechsel nicht verarbeitet", ex); }
            finally { Volatile.Write(ref _modeChangeBusy, 0); }
        });
    }

    private async Task OnMonitorModeChangedAsync(MonitorInfo before)
    {
        await RefreshMonitorsAsync();
        var after = SelectedMonitor;
        if (after is null) return;
        Log.Info($"Monitormodus gewechselt: {before.Width}x{before.Height} {before.RefreshHz} Hz → " +
                 $"{after.Width}x{after.Height} {after.RefreshHz} Hz ({after.DeviceName})");
        ApplyMonitorModeProfile(after);
        Notify?.Invoke(Loc.T("Notify_ModeChangedTitle"),
                       Loc.F("Notify_ModeChanged", after.Width, after.Height, after.RefreshHz, Settings.Fps));
        if (_recorder is not null || State is ReplayState.Running or ReplayState.Starting or ReplayState.Error)
            ScheduleRestart("Monitormodus", TimeSpan.FromMilliseconds(300), reprobe: false);
    }

    private bool _applyingModeProfile;

    /// <summary>
    /// Takes over frame rate and resolution remembered for this monitor mode. Without a stored profile,
    /// a frame rate above the new refresh rate is lowered to the highest one the monitor can show.
    /// </summary>
    /// <returns>True if frame rate or resolution changed.</returns>
    private bool ApplyMonitorModeProfile(MonitorInfo monitor)
    {
        if (!Settings.RememberPerMonitorMode) return false;
        var (fpsBefore, resolutionBefore) = (Settings.Fps, Settings.Resolution);
        _applyingModeProfile = true;
        try
        {
            if (Settings.MonitorModeProfiles.TryGetValue(ModeKey(monitor), out var profile))
            {
                if (profile.Fps != Settings.Fps || profile.Resolution != Settings.Resolution)
                    Log.Info($"Einstellungen für {ModeKey(monitor)} übernommen: {profile.Fps} fps, {profile.Resolution}");
                Settings.Fps = profile.Fps;
                Settings.Resolution = profile.Resolution;
            }
            else if (monitor.RefreshHz > 0 && Settings.Fps > monitor.RefreshHz + 1)
            {
                int[] common = { 30, 48, 60, 90, 120, 144, 165, 240, 360, 480 };
                Settings.Fps = common.Where(f => f <= monitor.RefreshHz + 1).DefaultIfEmpty(monitor.RefreshHz).Max();
            }
        }
        finally
        {
            _applyingModeProfile = false;
        }
        RememberMonitorModeProfile();
        return Settings.Fps != fpsBefore || Settings.Resolution != resolutionBefore;
    }

    private void RememberMonitorModeProfile()
    {
        if (!Settings.RememberPerMonitorMode || SelectedMonitor is not { } monitor) return;
        Settings.MonitorModeProfiles[ModeKey(monitor)] = new MonitorModeProfile { Fps = Settings.Fps, Resolution = Settings.Resolution };
        ScheduleSettingsSave();
    }

    /// <summary>
    /// Settings this PC cannot record with (e.g. AV1 without an AV1-capable GPU, or an encoder of another
    /// vendor) are switched to the nearest working choice before the recording starts.
    /// </summary>
    private void AdjustToCapabilities()
    {
        var caps = Capabilities;
        if (!caps.Probed) return;
        var codec = caps.Fallback(Settings.Codec);
        bool changed = false;
        if (codec != Settings.Codec)
        {
            Log.Warn($"{PipelineBuilder.CodecName(Settings.Codec)} wird von dieser Hardware nicht unterstützt, verwende {PipelineBuilder.CodecName(codec)}");
            var from = Settings.Codec;
            Settings.Codec = codec;
            Notify?.Invoke(Loc.T("Notify_CodecChangedTitle"),
                           Loc.F("Notify_CodecChanged", PipelineBuilder.CodecName(from), PipelineBuilder.CodecName(codec)));
            changed = true;
        }
        if (Settings.Encoder != EncoderBackend.Auto && !caps.Usable(Settings.Encoder, Settings.Codec))
        {
            Log.Warn($"{PipelineBuilder.BackendName(Settings.Encoder)} ist auf diesem PC nicht nutzbar, verwende Automatisch");
            Settings.Encoder = EncoderBackend.Auto;
            changed = true;
        }
        // The caller starts the recording right after this; no extra restart from the setting changes.
        if (changed)
        {
            lock (_timerGate)
            {
                _restartTimer?.Dispose();
                _restartTimer = null;
            }
        }
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
        if (await DetectEnvironmentAsync(forceEncoderCheck: true))
        {
            ApplyHighlights();
            await RestartAsync("Neu erkennen");
        }
    }

    private void SetError(Func<string> message)
    {
        LastError = message();
        SetStatus(message);
        State = ReplayState.Error;
        Log.Warn(LastError);
    }

    private void StopRecorder()
    {
        var recorder = _recorder;
        _recorder = null;
        _recordingMonitor = null;
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
                SetStatus(() => Loc.T("Status_PausedHint"));
                return;
            }
            if (Settings.OnlyWhenGameRunning && !_games.IsGameRunning)
            {
                State = ReplayState.WaitingForGame;
                SetStatus(() => Loc.T("Status_WaitingForGame"));
                return;
            }

            var monitor = SelectedMonitor;
            if (monitor is null)
            {
                SetError(() => Loc.T("Err_NoMonitorAvailable"));
                return;
            }

            Log.Info($"Starte Replay ({reason})");
            State = ReplayState.Starting;
            SetStatus(() => Loc.T("Status_SelectingEncoder"));
            var pipeline = await PipelineSelector.SelectAsync(Ffmpeg, Settings, monitor, Capabilities,
                                                              msg => SetStatus(() => msg), CancellationToken.None);
            if (pipeline is null)
            {
                SetError(() => Loc.T("Err_NoEncoder"));
                return;
            }
            ScheduleSettingsSave();

            bool withAudio = (Settings.CaptureSystemAudio || Settings.CaptureMicrophone) && !_audioFallback;
            if (_audioFallback) Log.Warn("Starte ohne Tonspur (Ausweichbetrieb nach Startproblemen mit Ton)");
            double? audioStart = null;
            if (withAudio) audioStart = _audio.BeginStream() + Settings.AudioOffsetMs / 1000.0;

            nint window = 0;
            if (Settings.CaptureMethod == CaptureMethod.GameWindow && pipeline.Source == VideoSource.Wgc)
            {
                window = _games.CaptureWindow();
                if (window != 0 && window == _windowCaptureFailed) window = 0;
                Log.Info(window != 0
                    ? $"Nehme das Spielfenster auf ({_games.LastSeen?.DisplayName})"
                    : "Kein Spielfenster verfügbar, nehme den Monitor auf");
            }

            var args = PipelineBuilder.BuildRecordArgs(pipeline, monitor, Settings, audioStart, window);
            _buffer.TargetSeconds = Settings.BufferSeconds;

            var recorder = new ReplayRecorder(_buffer);
            recorder.Running += () => OnRecorderRunning(recorder);
            recorder.Faulted += message => OnRecorderFaulted(recorder, message);
            _recorder = recorder; // before Start: its events may fire right away
            _pipeline = pipeline;
            _pipelineHasAudio = withAudio;
            _recordingWindow = window;
            _recordingMonitor = monitor;
            try
            {
                recorder.Start(Ffmpeg.Path, args, withAudio ? _audio : null);
            }
            catch (Exception ex)
            {
                _recorder = null;
                _audio.DetachOutput();
                recorder.Dispose();
                SetError(() => Loc.F("Err_FfmpegStart", ex.Message));
                return;
            }

            bool gameWindow = window != 0;
            int fps = Settings.Fps;
            _encoderText = () => $"{pipeline.EncoderLabel}, {pipeline.PathLabel}, {pipeline.SourceLabel}" +
                                 (gameWindow ? " (" + Loc.T("Opt_GameWindow") + ")" : "");
            _outputText = () => string.Format(Loc.Instance.Culture, "{0} × {1}, {2} fps, {3:0.#} Mbit/s",
                                              pipeline.OutWidth, pipeline.OutHeight, fps, pipeline.BitrateKbps / 1000.0);
            OnPropertyChanged(nameof(EncoderText));
            OnPropertyChanged(nameof(OutputText));
            SetStatus(() => Loc.T("Status_RecordingStarts"));
            LastError = null;
        }
        catch (Exception ex)
        {
            Log.Error("Replay konnte nicht gestartet werden", ex);
            SetError(() => Loc.F("Err_ReplayStart", ex.Message));
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
        bool fallback = _audioFallback;
        SetStatus(() => fallback
            ? Loc.T("Status_RunningWithoutAudio")
            : Loc.F("Status_Running", HotkeyBinding.DisplayText(Settings.HotkeySave), FormatDuration(Settings.BufferSeconds)));
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
            Notify?.Invoke(Loc.T("Notify_NoAudioTitle"), Loc.T("Status_RunningWithoutAudio"));
        }
        OnRecorderFaulted(recorder, Loc.F("Err_NoData", NoDataTimeout.TotalSeconds));
    }

    private DateTime _lowFpsSinceUtc = DateTime.MinValue;

    /// <summary>
    /// Warns once per recording when clearly fewer frames than requested arrive for half a minute while a game
    /// is in front (both capture paths only deliver changed frames, so a quiet desktop or a short loading
    /// screen is not a problem).
    /// </summary>
    private void CheckEncoderSpeed()
    {
        var recorder = _recorder;
        if (recorder is null || !recorder.HasProducedData || ReferenceEquals(recorder, _slowEncoderWarnedFor)) return;
        if (DateTime.UtcNow - recorder.StartedUtc < TimeSpan.FromSeconds(25))
        {
            _lowFpsSinceUtc = DateTime.MinValue;
            return;
        }
        if (recorder.RecentFps is not double fps || fps >= Settings.Fps * 0.9 || _games.Current is null)
        {
            _lowFpsSinceUtc = DateTime.MinValue;
            return;
        }
        if (_lowFpsSinceUtc == DateTime.MinValue) _lowFpsSinceUtc = DateTime.UtcNow;
        if (DateTime.UtcNow - _lowFpsSinceUtc < TimeSpan.FromSeconds(30)) return;

        _slowEncoderWarnedFor = recorder;
        _lowFpsSinceUtc = DateTime.MinValue;
        int hz = SelectedMonitor?.RefreshHz ?? 0;
        int target = Settings.Fps;
        bool monitorLimit = hz > 0 && hz < target * 0.95;
        Log.Warn($"Zu wenige Bilder: {fps:0.0} fps statt {Settings.Fps} ({_pipeline?.EncoderLabel}, {_pipeline?.SourceLabel}, " +
                 $"{Settings.Speed}, {_pipeline?.OutWidth}x{_pipeline?.OutHeight}, Monitor {(hz > 0 ? $"{hz} Hz" : "Hz unbekannt")})");
        SetStatus(() => monitorLimit
            ? Loc.F("Status_FewFramesMonitor", fps, target, hz)
            : Loc.F("Status_FewFrames", fps, target));
        Notify?.Invoke(Loc.T("Notify_StutterTitle"), Loc.F("Notify_Stutter", fps, target));
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
        if (_recordingWindow != 0 && !recorder.HasProducedData)
        {
            // Don't loop on a window Graphics Capture can't record (e.g. exclusive fullscreen): use the monitor.
            _windowCaptureFailed = _recordingWindow;
            Log.Warn("Das Spielfenster ließ sich nicht aufnehmen, nehme stattdessen den Monitor auf");
        }
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
        SetStatus(() => Loc.F("Status_Interrupted", delay.TotalSeconds, message));
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
        Notify?.Invoke(Loc.T(_userPaused ? "Notify_PausedTitle" : "Notify_ResumedTitle"),
                       Loc.T(_userPaused ? "Notify_Paused" : "Notify_Resumed"));
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
            Notify?.Invoke(Loc.T("Notify_NoClipTitle"), Loc.T("Notify_NoClipNotRunning"));
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
                Notify?.Invoke(Loc.T("Notify_ClipSavedTitle"), $"{clip.Game}: {clip.DurationText} min, {clip.SizeText}");
            if (manual && Settings.PlaySoundOnSave) Sounds.PlaySaved();
            return clip;
        }
        catch (Exception ex)
        {
            Log.Error("Clip konnte nicht gespeichert werden", ex);
            Notify?.Invoke(Loc.T("Notify_ClipFailedTitle"), ex.Message);
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
            if (!_applyingModeProfile && name is nameof(AppSettings.Fps) or nameof(AppSettings.Resolution))
                RememberMonitorModeProfile();
            // An encoder chosen by hand that cannot do the new codec: back to automatic.
            if (name == nameof(AppSettings.Codec) && Settings.Encoder != EncoderBackend.Auto &&
                !Capabilities.Usable(Settings.Encoder, Settings.Codec))
                Settings.Encoder = EncoderBackend.Auto;
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
        _gameName = game?.DisplayName;
        OnPropertyChanged(nameof(GameText));
        if (game is not null && Settings.OnlyWhenGameRunning && State == ReplayState.WaitingForGame)
        {
            ScheduleRestart("Spiel erkannt", TimeSpan.FromMilliseconds(500), reprobe: false);
            return;
        }

        // "Spielfenster": switch to the game's window when it is not the one being recorded yet.
        if (game is not null && Settings.CaptureMethod == CaptureMethod.GameWindow &&
            _pipeline?.Source == VideoSource.Wgc && State is ReplayState.Running or ReplayState.Starting)
        {
            var window = _games.CaptureWindow();
            if (window != 0 && window != _recordingWindow && window != _windowCaptureFailed)
                ScheduleRestart("Spielfenster", TimeSpan.FromMilliseconds(500), reprobe: false);
        }
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
                int port = Settings.Cs2Port;
                SetCs2Status(() => Loc.F("Cs2_PortBusy", port, ex.Message));
                Log.Warn("CS2-GSI-Empfänger: " + ex.Message);
            }
        }
        else
        {
            SetCs2Status(() => Loc.T("Common_Off"));
        }
    }

    private string? _cs2IgnoredMode;

    private string DescribeCs2(Cs2GameStateServer cs2)
    {
        var last = cs2.LastMessageUtc;
        if (last == DateTime.MinValue) return Loc.T("Cs2_Ready");
        if (DateTime.UtcNow - last >= TimeSpan.FromSeconds(45))
            return Loc.F("Cs2_LastData", (DateTime.UtcNow - last).TotalMinutes);
        string text = Loc.T("Cs2_Connected");
        if (!string.IsNullOrEmpty(cs2.LastMap)) text += ", " + cs2.LastMap;
        if (cs2.LastMode is not null)
        {
            text += ", " + Cs2GameStateServer.ModeName(cs2.LastMode);
            if (!Cs2ModeAllowed(cs2.LastMode)) text += " " + Loc.T("Cs2_NoHighlights");
        }
        return text;
    }

    private bool Cs2ModeAllowed(string? mode) => Settings.Cs2Modes switch
    {
        Cs2ModeFilter.All => true,
        Cs2ModeFilter.CompetitiveAndWingman => mode is "competitive" or "premier" or "scrimcomp2v2",
        _ => mode is "competitive" or "premier",
    };

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

            if (++_statsTick % 8 != 0) return; // the rest every 2 s

            CheckRecorderWatchdog();
            CheckMonitorMode();
            CheckEncoderSpeed();

            if (_cs2 is { } cs2) SetCs2Status(() => DescribeCs2(cs2));

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
        _cs2?.Dispose();
        _highlights.Dispose();
        _games.Dispose();
        _audio.Dispose();
        SettingsStore.Save(Settings);
    }
}
