using System.ComponentModel;
using System.Windows.Input;
using ClipRing.Audio;
using ClipRing.Capture;
using ClipRing.Core;
using ClipRing.Highlights;
using ClipRing.Services;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace ClipRing.UI;

public sealed record Option<T>(T Value, string Label);

public sealed class MainViewModel : ObservableObject
{
    public MainViewModel(ReplayService service)
    {
        Service = service;
        Settings = service.Settings;

        FpsOptions = BuildFpsOptions();
        RebuildEncoderOptions();
        RebuildMonitorOptions();
        RefreshDevices();

        Service.PropertyChanged += OnServicePropertyChanged;
        Settings.PropertyChanged += OnSettingsPropertyChanged;
        Service.ClipSaved += _ => SaveFlashCount++;

        SaveClipCommand = new RelayCommand(() => _ = Service.SaveClipAsync(Settings.BufferSeconds));
        SaveShortClipCommand = new RelayCommand(() => _ = Service.SaveClipAsync(Settings.ShortClipSeconds));
        TogglePauseCommand = new RelayCommand(Service.TogglePause);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder);
        BrowseOutputFolderCommand = new RelayCommand(BrowseOutputFolder);
        OpenClipCommand = new RelayCommand(p => { if (p is SavedClip c) OpenClip(c); });
        ShowClipCommand = new RelayCommand(p => { if (p is SavedClip c) Shell.ShowInExplorer(c.FilePath); });
        InstallCs2Command = new RelayCommand(InstallCs2);
        OpenValorantTemplatesCommand = new RelayCommand(OpenValorantTemplates);
        BrowseFfmpegCommand = new RelayCommand(BrowseFfmpeg);
        RedetectCommand = new RelayCommand(() => _ = Service.RedetectAsync());
        OpenLogsCommand = new RelayCommand(() => Shell.Open(AppPaths.LogDir));
        RefreshDevicesCommand = new RelayCommand(RefreshDevices);

        UpdateCs2InstallStatus();
    }

    public ReplayService Service { get; }
    public AppSettings Settings { get; }

    public string VersionText => $"ClipRing {typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}";

    private int _selectedPage;
    public int SelectedPage { get => _selectedPage; set => Set(ref _selectedPage, value); }

    private int _saveFlashCount;
    public int SaveFlashCount { get => _saveFlashCount; private set => Set(ref _saveFlashCount, value); }

    private string _hotkeyErrors = "";
    public string HotkeyErrors { get => _hotkeyErrors; set => Set(ref _hotkeyErrors, value); }

    private string _cs2InstallStatus = "";
    public string Cs2InstallStatus { get => _cs2InstallStatus; private set => Set(ref _cs2InstallStatus, value); }

    public string PauseButtonText => Service.IsPaused ? "Replay fortsetzen" : "Replay pausieren";

    // ---------------------------------------------------------------- options
    public IReadOnlyList<Option<ResolutionMode>> ResolutionOptions { get; } = new Option<ResolutionMode>[]
    {
        new(ResolutionMode.Native, "Nativ (Monitorauflösung)"),
        new(ResolutionMode.P2160, "2160p (4K)"),
        new(ResolutionMode.P1440, "1440p"),
        new(ResolutionMode.P1080, "1080p"),
        new(ResolutionMode.P720, "720p"),
    };

    public IReadOnlyList<Option<int>> FpsOptions { get; }

    public IReadOnlyList<Option<CaptureMethod>> CaptureMethodOptions { get; } = new Option<CaptureMethod>[]
    {
        new(CaptureMethod.Auto, "Automatisch (empfohlen)"),
        new(CaptureMethod.WindowsGraphicsCapture, "Windows Graphics Capture"),
        new(CaptureMethod.DesktopDuplication, "Desktop Duplication"),
        new(CaptureMethod.GameWindow, "Spielfenster (Graphics Capture)"),
    };

    public IReadOnlyList<Option<VideoCodec>> CodecOptions { get; } = new Option<VideoCodec>[]
    {
        new(VideoCodec.H264, "H.264 (überall abspielbar)"),
        new(VideoCodec.Hevc, "HEVC / H.265 (kleiner)"),
        new(VideoCodec.Av1, "AV1 (am kleinsten, neuere GPUs)"),
    };

    private IReadOnlyList<Option<EncoderBackend>> _encoderOptions = Array.Empty<Option<EncoderBackend>>();
    public IReadOnlyList<Option<EncoderBackend>> EncoderOptions { get => _encoderOptions; private set => Set(ref _encoderOptions, value); }

    public IReadOnlyList<Option<QualityPreset>> QualityOptions { get; } = new Option<QualityPreset>[]
    {
        new(QualityPreset.Low, "Niedrig (spart Speicher)"),
        new(QualityPreset.Medium, "Mittel"),
        new(QualityPreset.High, "Hoch (empfohlen)"),
        new(QualityPreset.Ultra, "Sehr hoch"),
        new(QualityPreset.Custom, "Eigene Bitrate"),
    };

    public IReadOnlyList<Option<RateControlMode>> RateControlOptions { get; } = new Option<RateControlMode>[]
    {
        new(RateControlMode.Vbr, "Variabel (VBR)"),
        new(RateControlMode.Cbr, "Konstant (CBR)"),
    };

    public IReadOnlyList<Option<EncoderSpeed>> SpeedOptions { get; } = new Option<EncoderSpeed>[]
    {
        new(EncoderSpeed.Fast, "Schnell (geringste Last)"),
        new(EncoderSpeed.Balanced, "Ausgewogen"),
        new(EncoderSpeed.Quality, "Qualität"),
    };

    public IReadOnlyList<Option<int>> AudioBitrateOptions { get; } = new Option<int>[]
    {
        new(128, "128 kbit/s"), new(160, "160 kbit/s"), new(192, "192 kbit/s"), new(256, "256 kbit/s"), new(320, "320 kbit/s"),
    };

    public IReadOnlyList<Option<int>> ShortClipOptions { get; } = new[] { 10, 15, 20, 30, 45, 60, 90, 120 }
        .Select(s => new Option<int>(s, $"{s} Sekunden")).ToArray();

    public IReadOnlyList<Option<Cs2ModeFilter>> Cs2ModeOptions { get; } = new Option<Cs2ModeFilter>[]
    {
        new(Cs2ModeFilter.Competitive, "Nur Wettkampf und Premier"),
        new(Cs2ModeFilter.CompetitiveAndWingman, "Wettkampf, Premier und Wingman"),
        new(Cs2ModeFilter.All, "Alle Modi"),
    };

    public IReadOnlyList<Option<int>> KillOptions { get; } = new Option<int>[]
    {
        new(1, "Jeder Kill"), new(2, "Ab 2 Kills"), new(3, "Ab 3 Kills"), new(4, "Ab 4 Kills"), new(5, "Nur Ace"),
    };

    private IReadOnlyList<Option<int>> _monitorOptions = Array.Empty<Option<int>>();
    public IReadOnlyList<Option<int>> MonitorOptions { get => _monitorOptions; private set => Set(ref _monitorOptions, value); }

    private IReadOnlyList<AudioDeviceOption> _outputDevices = Array.Empty<AudioDeviceOption>();
    public IReadOnlyList<AudioDeviceOption> OutputDevices { get => _outputDevices; private set => Set(ref _outputDevices, value); }

    private IReadOnlyList<AudioDeviceOption> _inputDevices = Array.Empty<AudioDeviceOption>();
    public IReadOnlyList<AudioDeviceOption> InputDevices { get => _inputDevices; private set => Set(ref _inputDevices, value); }

    private IReadOnlyList<Option<int>> BuildFpsOptions()
    {
        var values = new List<int> { 30, 48, 60, 90, 120, 144, 165, 240 };
        if (!values.Contains(Settings.Fps)) values.Add(Settings.Fps);
        return values.OrderBy(v => v).Select(v => new Option<int>(v, $"{v} fps")).ToArray();
    }

    private void RebuildEncoderOptions()
    {
        var available = Service.EncoderAvailability;
        string Label(EncoderBackend b)
        {
            string name = PipelineBuilder.BackendName(b);
            return available.TryGetValue(b, out var ok) && !ok && Service.Ffmpeg is not null
                ? $"{name} (nicht im FFmpeg-Build)"
                : name;
        }
        EncoderOptions = new[]
        {
            new Option<EncoderBackend>(EncoderBackend.Auto, "Automatisch (empfohlen)"),
            new Option<EncoderBackend>(EncoderBackend.Nvenc, Label(EncoderBackend.Nvenc)),
            new Option<EncoderBackend>(EncoderBackend.Amf, Label(EncoderBackend.Amf)),
            new Option<EncoderBackend>(EncoderBackend.Qsv, Label(EncoderBackend.Qsv)),
            new Option<EncoderBackend>(EncoderBackend.D3D12, Label(EncoderBackend.D3D12)),
            new Option<EncoderBackend>(EncoderBackend.Software, Label(EncoderBackend.Software)),
        };
    }

    private void RebuildMonitorOptions()
    {
        var monitors = Service.Monitors;
        if (monitors.Count == 0)
        {
            MonitorOptions = new[] { new Option<int>(Settings.MonitorIndex, $"Monitor {Settings.MonitorIndex + 1}") };
            return;
        }
        MonitorOptions = monitors.Select(m => new Option<int>(m.Index, m.Label)).ToArray();
    }

    private void RefreshDevices()
    {
        OutputDevices = AudioDevices.List(DataFlow.Render);
        InputDevices = AudioDevices.List(DataFlow.Capture);
    }

    // ---------------------------------------------------------------- derived settings
    public bool IsCustomBitrate => Settings.Quality == QualityPreset.Custom;

    public double CustomBitrateMbps
    {
        get => Settings.CustomBitrateKbps / 1000.0;
        set => Settings.CustomBitrateKbps = (int)Math.Round(value * 1000);
    }

    public string FileNamePreview
        => ClipSaver.BuildFileName(Settings.FileNameTemplate, "Counter-Strike 2", DateTime.Now, "3K") + ".mp4";

    public string ExcludedProcessesText
    {
        get => string.Join(Environment.NewLine, Settings.ExcludedProcesses);
        set => Settings.ExcludedProcesses = ParseList(value);
    }

    public string ExtraGamesText
    {
        get => string.Join(Environment.NewLine, Settings.ExtraGameProcesses);
        set => Settings.ExtraGameProcesses = ParseList(value);
    }

    public string CustomNamesText
    {
        get => string.Join(Environment.NewLine, Settings.CustomGameNames.Select(kv => $"{kv.Key} = {kv.Value}"));
        set
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in (value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var process = StripExe(line[..eq].Trim());
                var name = line[(eq + 1)..].Trim();
                if (process.Length > 0 && name.Length > 0) map[process] = name;
            }
            Settings.CustomGameNames = map;
        }
    }

    private static string StripExe(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static List<string> ParseList(string? text)
        => (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(StripExe)
                       .Where(x => x.Length > 0)
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .ToList();

    // ---------------------------------------------------------------- commands
    public ICommand SaveClipCommand { get; }
    public ICommand SaveShortClipCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand OpenOutputFolderCommand { get; }
    public ICommand BrowseOutputFolderCommand { get; }
    public ICommand OpenClipCommand { get; }
    public ICommand ShowClipCommand { get; }
    public ICommand InstallCs2Command { get; }
    public ICommand OpenValorantTemplatesCommand { get; }
    public ICommand BrowseFfmpegCommand { get; }
    public ICommand RedetectCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand RefreshDevicesCommand { get; }

    private void OpenOutputFolder()
    {
        Directory.CreateDirectory(Settings.OutputFolder);
        Shell.Open(Settings.OutputFolder);
    }

    private void BrowseOutputFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Speicherort für Clips wählen",
            InitialDirectory = Directory.Exists(Settings.OutputFolder) ? Settings.OutputFolder : "",
        };
        if (dialog.ShowDialog() == true) Settings.OutputFolder = dialog.FolderName;
    }

    private void BrowseFfmpeg()
    {
        var dialog = new OpenFileDialog
        {
            Title = "ffmpeg.exe auswählen",
            Filter = "ffmpeg.exe|ffmpeg.exe|Programme (*.exe)|*.exe",
        };
        if (dialog.ShowDialog() == true) Settings.FfmpegPath = dialog.FileName;
    }

    private void OpenClip(SavedClip clip)
    {
        if (File.Exists(clip.FilePath)) Shell.Open(clip.FilePath);
        else Service.RemoveFromRecent(clip);
    }

    private void InstallCs2()
    {
        try
        {
            var path = Cs2GsiInstaller.Install(Service.Library, Settings.Cs2Port, Settings.Cs2Token);
            if (!Settings.Cs2Enabled) Settings.Cs2Enabled = true;
            Cs2InstallStatus = $"Eingerichtet in {Path.GetDirectoryName(path)}. Starte CS2 neu, falls es gerade läuft.";
        }
        catch (Exception ex)
        {
            Cs2InstallStatus = ex.Message;
        }
    }

    private void UpdateCs2InstallStatus()
    {
        try
        {
            Cs2InstallStatus = Cs2GsiInstaller.IsInstalled(Service.Library, Settings.Cs2Port, Settings.Cs2Token)
                ? "Die Konfigurationsdatei ist in CS2 eingerichtet."
                : Cs2GsiInstaller.FindCfgDirectory(Service.Library) is null
                    ? "CS2 wurde in keiner Steam-Bibliothek gefunden."
                    : "Noch nicht eingerichtet.";
        }
        catch
        {
            Cs2InstallStatus = "";
        }
    }

    private void OpenValorantTemplates()
    {
        var folder = Service.ValorantTemplateFolder;
        Directory.CreateDirectory(folder);
        Shell.Open(folder);
    }

    // ---------------------------------------------------------------- change tracking
    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ReplayService.EncoderAvailability):
            case nameof(ReplayService.Ffmpeg):
                App.Current.Dispatcher.InvokeAsync(RebuildEncoderOptions);
                break;
            case nameof(ReplayService.Monitors):
                App.Current.Dispatcher.InvokeAsync(RebuildMonitorOptions);
                break;
            case nameof(ReplayService.IsPaused):
                OnPropertyChanged(nameof(PauseButtonText));
                break;
        }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Quality):
                OnPropertyChanged(nameof(IsCustomBitrate));
                break;
            case nameof(AppSettings.CustomBitrateKbps):
                OnPropertyChanged(nameof(CustomBitrateMbps));
                break;
            case nameof(AppSettings.FileNameTemplate):
                OnPropertyChanged(nameof(FileNamePreview));
                break;
            case nameof(AppSettings.Cs2Port):
            case nameof(AppSettings.Cs2Token):
                UpdateCs2InstallStatus();
                break;
        }
    }
}
