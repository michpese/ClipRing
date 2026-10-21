namespace ClipRing.Core;

/// <summary>
/// All user settings. Persisted as JSON in %APPDATA%\ClipRing\settings.json.
/// Every property raises PropertyChanged so the UI can bind directly and the
/// ReplayService can react (restart pipeline, update gains, ...).
/// </summary>
public sealed class AppSettings : ObservableObject
{
    // ---------------------------------------------------------------- Video
    private int _monitorIndex;
    public int MonitorIndex { get => _monitorIndex; set => Set(ref _monitorIndex, Math.Max(0, value)); }

    private ResolutionMode _resolution = ResolutionMode.Native;
    public ResolutionMode Resolution { get => _resolution; set => Set(ref _resolution, value); }

    private int _fps = 60;
    public int Fps { get => _fps; set => Set(ref _fps, Math.Clamp(value, 10, 360)); }

    private VideoCodec _codec = VideoCodec.H264;
    public VideoCodec Codec { get => _codec; set => Set(ref _codec, value); }

    private EncoderBackend _encoder = EncoderBackend.Auto;
    public EncoderBackend Encoder { get => _encoder; set => Set(ref _encoder, value); }

    private QualityPreset _quality = QualityPreset.High;
    public QualityPreset Quality { get => _quality; set => Set(ref _quality, value); }

    private int _customBitrateKbps = 20000;
    public int CustomBitrateKbps { get => _customBitrateKbps; set => Set(ref _customBitrateKbps, Math.Clamp(value, 1000, 200000)); }

    private RateControlMode _rateControl = RateControlMode.Vbr;
    public RateControlMode RateControl { get => _rateControl; set => Set(ref _rateControl, value); }

    private EncoderSpeed _speed = EncoderSpeed.Balanced;
    public EncoderSpeed Speed { get => _speed; set => Set(ref _speed, value); }

    private double _keyframeIntervalSec = 1.0;
    /// <summary>Keyframe distance. Clips can only start on a keyframe, so this is the cut granularity.</summary>
    public double KeyframeIntervalSec { get => _keyframeIntervalSec; set => Set(ref _keyframeIntervalSec, Math.Clamp(value, 0.5, 4.0)); }

    private bool _captureCursor = true;
    public bool CaptureCursor { get => _captureCursor; set => Set(ref _captureCursor, value); }

    private int _bufferSeconds = 120;
    public int BufferSeconds { get => _bufferSeconds; set => Set(ref _bufferSeconds, Math.Clamp(value, 10, 1200)); }

    private bool _onlyWhenGameRunning;
    public bool OnlyWhenGameRunning { get => _onlyWhenGameRunning; set => Set(ref _onlyWhenGameRunning, value); }

    // ---------------------------------------------------------------- Audio
    private bool _captureSystemAudio = true;
    public bool CaptureSystemAudio { get => _captureSystemAudio; set => Set(ref _captureSystemAudio, value); }

    /// <summary>Empty string = Windows default output device.</summary>
    private string _systemAudioDeviceId = "";
    public string SystemAudioDeviceId { get => _systemAudioDeviceId; set => Set(ref _systemAudioDeviceId, value ?? ""); }

    private double _systemVolume = 1.0;
    public double SystemVolume { get => _systemVolume; set => Set(ref _systemVolume, Math.Clamp(value, 0, 2)); }

    private bool _captureMicrophone;
    public bool CaptureMicrophone { get => _captureMicrophone; set => Set(ref _captureMicrophone, value); }

    /// <summary>Empty string = Windows default recording device.</summary>
    private string _microphoneDeviceId = "";
    public string MicrophoneDeviceId { get => _microphoneDeviceId; set => Set(ref _microphoneDeviceId, value ?? ""); }

    private double _microphoneVolume = 1.0;
    public double MicrophoneVolume { get => _microphoneVolume; set => Set(ref _microphoneVolume, Math.Clamp(value, 0, 2)); }

    private int _audioBitrateKbps = 192;
    public int AudioBitrateKbps { get => _audioBitrateKbps; set => Set(ref _audioBitrateKbps, Math.Clamp(value, 64, 512)); }

    private int _audioOffsetMs;
    /// <summary>Positive values delay the audio relative to the picture.</summary>
    public int AudioOffsetMs { get => _audioOffsetMs; set => Set(ref _audioOffsetMs, Math.Clamp(value, -1000, 1000)); }

    // ---------------------------------------------------------------- Output
    private string _outputFolder = DefaultOutputFolder();
    public string OutputFolder { get => _outputFolder; set => Set(ref _outputFolder, string.IsNullOrWhiteSpace(value) ? DefaultOutputFolder() : value); }

    private bool _gameSubfolders = true;
    public bool GameSubfolders { get => _gameSubfolders; set => Set(ref _gameSubfolders, value); }

    private string _fileNameTemplate = "{game}_{date}_{time}";
    public string FileNameTemplate { get => _fileNameTemplate; set => Set(ref _fileNameTemplate, string.IsNullOrWhiteSpace(value) ? "{game}_{date}_{time}" : value); }

    private bool _remuxToStandardMp4 = true;
    public bool RemuxToStandardMp4 { get => _remuxToStandardMp4; set => Set(ref _remuxToStandardMp4, value); }

    private bool _notifyOnSave = true;
    public bool NotifyOnSave { get => _notifyOnSave; set => Set(ref _notifyOnSave, value); }

    private bool _playSoundOnSave = true;
    public bool PlaySoundOnSave { get => _playSoundOnSave; set => Set(ref _playSoundOnSave, value); }

    // ---------------------------------------------------------------- Hotkeys
    private string _hotkeySave = "Alt+F10";
    public string HotkeySave { get => _hotkeySave; set => Set(ref _hotkeySave, value ?? ""); }

    private string _hotkeySaveShort = "Alt+F9";
    public string HotkeySaveShort { get => _hotkeySaveShort; set => Set(ref _hotkeySaveShort, value ?? ""); }

    private int _shortClipSeconds = 30;
    public int ShortClipSeconds { get => _shortClipSeconds; set => Set(ref _shortClipSeconds, Math.Clamp(value, 5, 600)); }

    private string _hotkeyToggle = "Alt+Shift+F10";
    public string HotkeyToggle { get => _hotkeyToggle; set => Set(ref _hotkeyToggle, value ?? ""); }

    // ---------------------------------------------------------------- Highlights
    private int _highlightPreRollSec = 8;
    public int HighlightPreRollSec { get => _highlightPreRollSec; set => Set(ref _highlightPreRollSec, Math.Clamp(value, 2, 60)); }

    private int _highlightPostRollSec = 4;
    public int HighlightPostRollSec { get => _highlightPostRollSec; set => Set(ref _highlightPostRollSec, Math.Clamp(value, 1, 30)); }

    private bool _cs2Enabled;
    public bool Cs2Enabled { get => _cs2Enabled; set => Set(ref _cs2Enabled, value); }

    private int _cs2Port = 3210;
    public int Cs2Port { get => _cs2Port; set => Set(ref _cs2Port, Math.Clamp(value, 1024, 65535)); }

    private string _cs2Token = Guid.NewGuid().ToString("N");
    public string Cs2Token { get => _cs2Token; set => Set(ref _cs2Token, string.IsNullOrWhiteSpace(value) ? Guid.NewGuid().ToString("N") : value); }

    private int _cs2MinKills = 1;
    public int Cs2MinKills { get => _cs2MinKills; set => Set(ref _cs2MinKills, Math.Clamp(value, 1, 5)); }

    private bool _cs2IgnoreWarmup = true;
    public bool Cs2IgnoreWarmup { get => _cs2IgnoreWarmup; set => Set(ref _cs2IgnoreWarmup, value); }

    private bool _valorantEnabled;
    public bool ValorantEnabled { get => _valorantEnabled; set => Set(ref _valorantEnabled, value); }

    private double _valorantThreshold = 0.60;
    public double ValorantThreshold { get => _valorantThreshold; set => Set(ref _valorantThreshold, Math.Clamp(value, 0.3, 0.95)); }

    private int _valorantMinKills = 1;
    public int ValorantMinKills { get => _valorantMinKills; set => Set(ref _valorantMinKills, Math.Clamp(value, 1, 5)); }

    private string _hotkeyValorantCalibrate = "Ctrl+Alt+K";
    public string HotkeyValorantCalibrate { get => _hotkeyValorantCalibrate; set => Set(ref _hotkeyValorantCalibrate, value ?? ""); }

    // Region of the kill banner, relative to the monitor (0..1).
    private double _valorantRegionX = 0.40;
    public double ValorantRegionX { get => _valorantRegionX; set => Set(ref _valorantRegionX, Math.Clamp(value, 0, 0.95)); }
    private double _valorantRegionY = 0.66;
    public double ValorantRegionY { get => _valorantRegionY; set => Set(ref _valorantRegionY, Math.Clamp(value, 0, 0.95)); }
    private double _valorantRegionW = 0.20;
    public double ValorantRegionW { get => _valorantRegionW; set => Set(ref _valorantRegionW, Math.Clamp(value, 0.03, 1)); }
    private double _valorantRegionH = 0.22;
    public double ValorantRegionH { get => _valorantRegionH; set => Set(ref _valorantRegionH, Math.Clamp(value, 0.03, 1)); }

    // ---------------------------------------------------------------- Games
    private bool _detectFullscreenApps = true;
    public bool DetectFullscreenApps { get => _detectFullscreenApps; set => Set(ref _detectFullscreenApps, value); }

    private List<string> _excludedProcesses = new();
    /// <summary>Process names (without .exe) that are never treated as a game.</summary>
    public List<string> ExcludedProcesses { get => _excludedProcesses; set => Set(ref _excludedProcesses, value ?? new()); }

    private List<string> _extraGameProcesses = new();
    /// <summary>Process names (without .exe) that are always treated as a game.</summary>
    public List<string> ExtraGameProcesses { get => _extraGameProcesses; set => Set(ref _extraGameProcesses, value ?? new()); }

    private Dictionary<string, string> _customGameNames = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Process name (without .exe) → display/folder name.</summary>
    public Dictionary<string, string> CustomGameNames
    {
        get => _customGameNames;
        set => Set(ref _customGameNames, value is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- General
    private bool _startWithWindows;
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }

    private bool _startMinimized = true;
    public bool StartMinimized { get => _startMinimized; set => Set(ref _startMinimized, value); }

    private bool _closeToTray = true;
    public bool CloseToTray { get => _closeToTray; set => Set(ref _closeToTray, value); }

    private string _ffmpegPath = "";
    /// <summary>Empty = search next to the app, in PATH and in the WinGet links folder.</summary>
    public string FfmpegPath { get => _ffmpegPath; set => Set(ref _ffmpegPath, value ?? ""); }

    // ---------------------------------------------------------------- Internal cache
    /// <summary>Key of the last successfully probed capture/encode pipeline (see PipelineSelector).</summary>
    public string PipelineCacheKey { get; set; } = "";
    public string PipelineCacheId { get; set; } = "";

    public static string DefaultOutputFolder()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ClipRing");

    /// <summary>Names of properties whose change requires restarting the capture pipeline.</summary>
    public static readonly HashSet<string> PipelineProperties = new()
    {
        nameof(MonitorIndex), nameof(Resolution), nameof(Fps), nameof(Codec), nameof(Encoder),
        nameof(Quality), nameof(CustomBitrateKbps), nameof(RateControl), nameof(Speed),
        nameof(KeyframeIntervalSec), nameof(CaptureCursor), nameof(AudioBitrateKbps),
        nameof(AudioOffsetMs), nameof(FfmpegPath),
    };

    public static readonly HashSet<string> HotkeyProperties = new()
    {
        nameof(HotkeySave), nameof(HotkeySaveShort), nameof(HotkeyToggle), nameof(HotkeyValorantCalibrate),
    };
}
