using System.ComponentModel;
using System.Windows.Input;
using ClipRing.Audio;
using ClipRing.Capture;
using ClipRing.Core;
using ClipRing.Highlights;
using ClipRing.Hotkeys;
using ClipRing.Localization;
using ClipRing.Services;
using Microsoft.Win32;
using NAudio.CoreAudioApi;

namespace ClipRing.UI;

public sealed class MainViewModel : ObservableObject
{
    public MainViewModel(ReplayService service)
    {
        Service = service;
        Settings = service.Settings;

        RebuildOptions();
        RefreshDevices();
        Loc.LanguageChanged += OnLanguageChanged;

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

    public string PauseButtonText => Loc.T(Service.IsPaused ? "Btn_ResumeReplay" : "Btn_PauseReplay");

    public string HotkeySaveText => HotkeyBinding.DisplayText(Settings.HotkeySave);

    // ---------------------------------------------------------------- options
    // All lists update in place (LiveOptions), so a language switch or a later detection result never
    // clears the selection in a ComboBox.
    public LiveOptions<string> LanguageOptions { get; } = new();
    public LiveOptions<ResolutionMode> ResolutionOptions { get; } = new();
    public LiveOptions<int> FpsOptions { get; } = new();
    public LiveOptions<CaptureMethod> CaptureMethodOptions { get; } = new();
    public LiveOptions<VideoCodec> CodecOptions { get; } = new();
    public LiveOptions<EncoderBackend> EncoderOptions { get; } = new();
    public LiveOptions<QualityPreset> QualityOptions { get; } = new();
    public LiveOptions<RateControlMode> RateControlOptions { get; } = new();
    public LiveOptions<EncoderSpeed> SpeedOptions { get; } = new();
    public LiveOptions<int> AudioBitrateOptions { get; } = new();
    public LiveOptions<int> ShortClipOptions { get; } = new();
    public LiveOptions<Cs2ModeFilter> Cs2ModeOptions { get; } = new();
    public LiveOptions<int> KillOptions { get; } = new();
    public LiveOptions<int> MonitorOptions { get; } = new();
    public LiveOptions<string> OutputDevices { get; } = new();
    public LiveOptions<string> InputDevices { get; } = new();

    private static (T, string) O<T>(T value, string key) => (value, Loc.T(key));

    private void RebuildOptions()
    {
        LanguageOptions.Update(new[]
        {
            ("system", Loc.T("Opt_LanguageSystem")), ("de", "Deutsch"), ("en", "English"),
        });
        ResolutionOptions.Update(new[]
        {
            O(ResolutionMode.Native, "Opt_ResNative"), (ResolutionMode.P2160, "2160p (4K)"), (ResolutionMode.P1440, "1440p"),
            (ResolutionMode.P1080, "1080p"), (ResolutionMode.P720, "720p"),
        });
        var fps = new List<int> { 30, 48, 60, 90, 120, 144, 165, 240, 360, 480 };
        if (!fps.Contains(Settings.Fps)) fps.Add(Settings.Fps);
        FpsOptions.Update(fps.OrderBy(v => v).Select(v => (v, $"{v} fps")));
        CaptureMethodOptions.Update(new[]
        {
            O(CaptureMethod.Auto, "Opt_Automatic"),
            (CaptureMethod.WindowsGraphicsCapture, "Windows Graphics Capture"),
            (CaptureMethod.DesktopDuplication, "Desktop Duplication"),
            O(CaptureMethod.GameWindow, "Opt_GameWindow"),
        });
        QualityOptions.Update(new[]
        {
            O(QualityPreset.Low, "Opt_QualityLow"), O(QualityPreset.Medium, "Opt_QualityMedium"),
            O(QualityPreset.High, "Opt_QualityHigh"), O(QualityPreset.Ultra, "Opt_QualityUltra"),
            O(QualityPreset.Custom, "Opt_QualityCustom"),
        });
        RateControlOptions.Update(new[] { O(RateControlMode.Vbr, "Opt_Vbr"), O(RateControlMode.Cbr, "Opt_Cbr") });
        SpeedOptions.Update(new[]
        {
            O(EncoderSpeed.Fast, "Opt_SpeedFast"), O(EncoderSpeed.Balanced, "Opt_SpeedBalanced"), O(EncoderSpeed.Quality, "Opt_SpeedQuality"),
        });
        AudioBitrateOptions.Update(new[] { 128, 160, 192, 256, 320 }.Select(k => (k, $"{k} kbit/s")));
        ShortClipOptions.Update(new[] { 10, 15, 20, 30, 45, 60, 90, 120 }.Select(sec => (sec, Loc.F("Opt_Seconds", sec))));
        Cs2ModeOptions.Update(new[]
        {
            O(Cs2ModeFilter.Competitive, "Opt_Cs2Competitive"), O(Cs2ModeFilter.CompetitiveAndWingman, "Opt_Cs2Wingman"),
            O(Cs2ModeFilter.All, "Opt_Cs2All"),
        });
        KillOptions.Update(new[]
        {
            O(1, "Opt_KillsAny"), (2, Loc.F("Opt_KillsFrom", 2)), (3, Loc.F("Opt_KillsFrom", 3)), (4, Loc.F("Opt_KillsFrom", 4)),
            O(5, "Opt_KillsAce"),
        });
        RebuildCodecAndEncoderOptions();
        RebuildMonitorOptions();
        ApplyMonitorLimits();
    }

    /// <summary>
    /// Frame rates above the monitor's refresh rate and resolutions above its size can't be recorded
    /// (Windows delivers no more frames, and ClipRing only scales down): those entries are greyed out.
    /// </summary>
    private void ApplyMonitorLimits()
    {
        var monitor = Service.SelectedMonitor;
        foreach (var option in FpsOptions)
            option.IsEnabled = monitor is null || monitor.RefreshHz <= 0 || option.Value <= monitor.RefreshHz + 1;
        foreach (var option in ResolutionOptions)
        {
            int height = option.Value switch
            {
                ResolutionMode.P2160 => 2160, ResolutionMode.P1440 => 1440, ResolutionMode.P1080 => 1080, ResolutionMode.P720 => 720, _ => 0,
            };
            option.IsEnabled = monitor is null || height == 0 || height <= monitor.Height;
        }
    }

    /// <summary>
    /// Codecs and encoders this PC cannot use are shown but disabled. Until the hardware check has run,
    /// everything that is in the FFmpeg build stays selectable.
    /// </summary>
    private void RebuildCodecAndEncoderOptions()
    {
        var caps = Service.Capabilities;
        CodecOptions.Update(new[]
        {
            (VideoCodec.H264, Loc.T("Opt_CodecH264")), (VideoCodec.Hevc, Loc.T("Opt_CodecHevc")), (VideoCodec.Av1, Loc.T("Opt_CodecAv1")),
        }.Select(c => (c.Item1, caps.CodecUsable(c.Item1) ? c.Item2 : Loc.F("Opt_NotSupported", c.Item2))));
        foreach (var option in CodecOptions) option.IsEnabled = caps.CodecUsable(option.Value);

        var codec = Settings.Codec;
        var backends = new[] { EncoderBackend.Nvenc, EncoderBackend.Amf, EncoderBackend.Qsv, EncoderBackend.D3D12, EncoderBackend.Software };
        EncoderOptions.Update(new[] { (EncoderBackend.Auto, Loc.T("Opt_Automatic")) }.Concat(backends.Select(b =>
        {
            string name = PipelineBuilder.BackendName(b);
            return (b, caps.Usable(b, codec) ? name : Loc.F("Opt_NotAvailable", name));
        })));
        foreach (var option in EncoderOptions)
            option.IsEnabled = option.Value == EncoderBackend.Auto || caps.Usable(option.Value, codec);
    }

    private void RebuildMonitorOptions()
    {
        var monitors = Service.Monitors;
        if (monitors.Count == 0)
        {
            // Until detection finishes: a placeholder for the saved choice, replaced in place afterwards.
            MonitorOptions.Update(new[] { (Settings.MonitorIndex, Loc.F("Opt_MonitorDetecting", Settings.MonitorIndex + 1)) });
            return;
        }
        MonitorOptions.Update(monitors.Select(m => (m.Index, m.Label)));
    }

    private void OnLanguageChanged()
    {
        RebuildOptions();
        RefreshDevices();
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(HotkeySaveText));
        UpdateCs2InstallStatus();
        System.Windows.Data.CollectionViewSource.GetDefaultView(Service.RecentClips)?.Refresh();
    }

    private void RefreshDevices()
    {
        try
        {
            OutputDevices.Update(AudioDevices.List(DataFlow.Render).Select(d => (d.Id, d.Name)));
            InputDevices.Update(AudioDevices.List(DataFlow.Capture).Select(d => (d.Id, d.Name)));
        }
        catch (Exception ex)
        {
            Log.Warn("Audiogeräte nicht lesbar: " + ex.Message);
        }
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
            Title = Loc.T("Dialog_ChooseClipFolder"),
            InitialDirectory = Directory.Exists(Settings.OutputFolder) ? Settings.OutputFolder : "",
        };
        if (dialog.ShowDialog() == true) Settings.OutputFolder = dialog.FolderName;
    }

    private void BrowseFfmpeg()
    {
        var dialog = new OpenFileDialog
        {
            Title = Loc.T("Dialog_ChooseFfmpeg"),
            Filter = "ffmpeg.exe|ffmpeg.exe|" + Loc.T("Dialog_Programs") + " (*.exe)|*.exe",
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
            Cs2InstallStatus = Loc.F("Cs2_InstalledIn", Path.GetDirectoryName(path));
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
                ? Loc.T("Cs2_Installed")
                : Cs2GsiInstaller.FindCfgDirectory(Service.Library) is null
                    ? Loc.T("Cs2_NotFound")
                    : Loc.T("Cs2_NotInstalled");
        }
        catch
        {
            Cs2InstallStatus = "";
        }
    }

    // ---------------------------------------------------------------- change tracking
    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ReplayService.Capabilities):
                App.Current.Dispatcher.InvokeAsync(RebuildCodecAndEncoderOptions);
                break;
            case nameof(ReplayService.Monitors):
            case nameof(ReplayService.SelectedMonitor):
                App.Current.Dispatcher.InvokeAsync(() =>
                {
                    RebuildMonitorOptions();
                    ApplyMonitorLimits();
                });
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
            case nameof(AppSettings.Codec):
                RebuildCodecAndEncoderOptions();
                break;
            case nameof(AppSettings.HotkeySave):
                OnPropertyChanged(nameof(HotkeySaveText));
                break;
            case nameof(AppSettings.Language):
                Loc.Instance.Apply(Settings.Language);
                break;
        }
    }
}
