namespace ClipRing.Core;

public enum ResolutionMode { Native, P720, P1080, P1440, P2160 }

public enum VideoCodec { H264, Hevc, Av1 }

/// <summary>Which encoder family to use. Auto probes in the order NVENC, AMF, QSV, D3D12, Software.</summary>
public enum EncoderBackend { Auto, Nvenc, Amf, Qsv, D3D12, Software }

public enum QualityPreset { Low, Medium, High, Ultra, Custom }

public enum RateControlMode { Vbr, Cbr }

public enum EncoderSpeed { Fast, Balanced, Quality }

public enum ReplayState { Initializing, Starting, Running, Paused, WaitingForGame, Error }
