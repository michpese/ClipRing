namespace ClipRing.Core;

public enum ResolutionMode { Native, P720, P1080, P1440, P2160 }

public enum VideoCodec { H264, Hevc, Av1 }

/// <summary>Which encoder family to use. Auto probes in the order NVENC, AMF, QSV, D3D12, Software.</summary>
public enum EncoderBackend { Auto, Nvenc, Amf, Qsv, D3D12, Software }

public enum QualityPreset { Low, Medium, High, Ultra, Custom }

public enum RateControlMode { Vbr, Cbr }

public enum EncoderSpeed { Fast, Balanced, Quality }

public enum ReplayState { Initializing, Starting, Running, Paused, WaitingForGame, Error }

/// <summary>Which Counter-Strike 2 modes create automatic highlight clips.</summary>
public enum Cs2ModeFilter { Competitive, CompetitiveAndWingman, All }

/// <summary>How the screen is captured. Auto prefers Windows Graphics Capture (event driven, reaches high frame rates).</summary>
/// <summary>GameWindow: Graphics Capture of the detected game window instead of the monitor.</summary>
public enum CaptureMethod { Auto, WindowsGraphicsCapture, DesktopDuplication, GameWindow }

/// <summary>The capture filter a pipeline actually uses.</summary>
public enum VideoSource { Wgc, Dda }
