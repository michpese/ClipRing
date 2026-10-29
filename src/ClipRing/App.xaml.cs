using System.Windows;
using System.Windows.Threading;
using ClipRing.Core;
using ClipRing.Hotkeys;
using ClipRing.Localization;
using ClipRing.Services;
using ClipRing.UI;

namespace ClipRing;

public partial class App : Application
{
    private const string MutexName = "ClipRing.SingleInstance.v1";
    private const string ShowEventName = "ClipRing.ShowWindow.v1";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private AppSettings? _settings;
    private ReplayService? _service;
    private MainViewModel? _viewModel;
    private HotkeyManager? _hotkeys;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _registeringHotkeys;

    public static bool IsExiting { get; private set; }

    private sealed class CollectingTraceListener : System.Diagnostics.TraceListener
    {
        public List<string> Messages { get; } = new();
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message is not null) Messages.Add(message); }
    }

    private async Task<int> RunUiSelfTestAsync(string reportPath)
    {
        var lines = new List<string>();
        int code = 1;
        var listener = new CollectingTraceListener();
        try
        {
            System.Diagnostics.PresentationTraceSources.Refresh();
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;

            var settings = new AppSettings();
            var service = new ReplayService(settings, Dispatcher);
            var viewModel = new MainViewModel(service);
            var window = new MainWindow
            {
                DataContext = viewModel,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -30000,
                Top = -30000,
            };
            window.Show();
            foreach (var language in new[] { "de", "en", "de" })
            {
                settings.Language = language; // live switch through the settings, like the user does it
                for (int page = 0; page < 8; page++)
                {
                    viewModel.SelectedPage = page;
                    window.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
                lines.Add($"language={Loc.Instance.Language}: ok");
            }
            window.Close();

            foreach (var language in new[] { "de", "en" })
            {
                var table = Loc.Table(language);
                lines.Add($"{language}.json={table.Count} keys");
            }
            var onlyDe = Loc.Table("de").Keys.Except(Loc.Table("en").Keys).ToList();
            var onlyEn = Loc.Table("en").Keys.Except(Loc.Table("de").Keys).ToList();
            if (onlyDe.Count > 0) lines.Add("only in de.json: " + string.Join(" ", onlyDe));
            if (onlyEn.Count > 0) lines.Add("only in en.json: " + string.Join(" ", onlyEn));
            lines.Add("missing=" + (Loc.Missing.IsEmpty ? "none" : string.Join(" ", Loc.Missing.Keys.OrderBy(k => k))));
            lines.Add($"bindingErrors={listener.Messages.Count}");
            lines.AddRange(listener.Messages.Distinct().Take(8).Select(m => "binding: " + m));
            if (Loc.Missing.IsEmpty && onlyDe.Count == 0 && onlyEn.Count == 0) code = 0;
        }
        catch (Exception ex)
        {
            lines.Add("error=" + ex.ToString().ReplaceLineEndings(" | "));
        }
        lines.Add($"result={(code == 0 ? "ok" : "failed")}");
        try { File.WriteAllLines(reportPath, lines); } catch { /* exit code still tells */ }
        return code;
    }

    private static async Task<int> RunFfmpegSelfTestAsync(string reportPath)
    {
        var lines = new List<string>();
        int code = 1;
        try
        {
            AppPaths.EnsureCreated();
            lines.Add($"embedded={Capture.EmbeddedFfmpeg.IsAvailable}");
            lines.Add($"embeddedVersion={Capture.EmbeddedFfmpeg.Version ?? "-"}");
            var path = await Capture.EmbeddedFfmpeg.EnsureExtractedAsync();
            lines.Add($"path={path ?? "-"}");
            // Second call must find the unpacked copy and accept its checksum.
            var again = await Capture.EmbeddedFfmpeg.EnsureExtractedAsync();
            lines.Add($"reuse={(again is not null && again == path)}");
            if (path is not null)
            {
                var info = await Capture.FfmpegLocator.QueryAsync(path);
                lines.Add($"version={info?.Version ?? "-"}");
                lines.Add($"encoders={info?.Encoders.Count ?? 0}");
                lines.Add($"gfxcapture={info?.HasFilter("gfxcapture") ?? false}");
                lines.Add($"ddagrab={info?.HasFilter("ddagrab") ?? false}");
                lines.Add($"av1_nvenc={info?.HasEncoder("av1_nvenc") ?? false}");
                if (info is not null)
                {
                    // No GPU on the build machine: every hardware test must fail cleanly, not hang or crash.
                    var caps = await Capture.EncoderProbe.GetAsync(info, force: true);
                    lines.Add("hardware=" + caps.Describe().Replace("\n", "; "));
                }
                if (info is not null && again == path && (info.HasFilter("gfxcapture") || info.HasFilter("ddagrab"))) code = 0;
            }
        }
        catch (Exception ex)
        {
            lines.Add("error=" + ex.ToString().ReplaceLineEndings(" | "));
        }
        lines.Add($"result={(code == 0 ? "ok" : "failed")}");
        try { File.WriteAllLines(reportPath, lines); } catch { /* exit code still tells */ }
        return code;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Build check (CI): unpack and query the embedded FFmpeg, write a report, exit with 0/1.
        int selfTest = Array.IndexOf(e.Args, "--selftest-ffmpeg");
        if (selfTest >= 0)
        {
            IsExiting = true;
            string report = selfTest + 1 < e.Args.Length
                ? e.Args[selfTest + 1]
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cliprings-selftest.txt");
            Shutdown(Task.Run(() => RunFfmpegSelfTestAsync(report)).GetAwaiter().GetResult());
            return;
        }

        // Build check (CI): open the window in German and English, visit every page, switch the language
        // live, and report missing texts and binding errors.
        int uiTest = Array.IndexOf(e.Args, "--selftest-ui");
        if (uiTest >= 0)
        {
            IsExiting = true;
            string report = uiTest + 1 < e.Args.Length
                ? e.Args[uiTest + 1]
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cliprings-selftest-ui.txt");
            Dispatcher.BeginInvoke(new Action(async () => Shutdown(await RunUiSelfTestAsync(report))));
            return;
        }

        // Single instance: a second start just brings the running one to the front.
        _mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                existing.Set();
            }
            catch { /* the other instance is just shutting down */ }
            IsExiting = true;
            Shutdown();
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(ShowMainWindow)), null, Timeout.Infinite, false);

        base.OnStartup(e);

        AppPaths.EnsureCreated();
        Log.Init();
        Log.Info($"ClipRing startet ({Environment.OSVersion}, .NET {Environment.Version})");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unbehandelter Fehler", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unbeobachteter Task-Fehler", args.Exception);
            args.SetObserved();
        };

        var settings = _settings = SettingsStore.Load();
        Loc.Instance.Apply(settings.Language);
        var service = _service = new ReplayService(settings, Dispatcher);
        var viewModel = _viewModel = new MainViewModel(service);

        _hotkeys = new HotkeyManager();
        settings.PropertyChanged += (_, args) =>
        {
            var name = args.PropertyName ?? "";
            if (AppSettings.HotkeyProperties.Contains(name))
                RegisterHotkeys();
        };
        HotkeyBox.CaptureStarted += () => _hotkeys?.Suspend();
        HotkeyBox.CaptureEnded += () => _hotkeys?.Resume();

        _tray = new TrayIcon(
            open: ShowMainWindow,
            saveClip: () => _ = service.SaveClipAsync(settings.BufferSeconds),
            togglePause: service.TogglePause,
            openFolder: () => viewModel.OpenOutputFolderCommand.Execute(null),
            exit: ExitApp,
            isPaused: () => service.IsPaused);
        RegisterHotkeys(); // after the tray icon exists, so a hotkey migration can be announced
        service.Notify += (title, text) => Dispatcher.BeginInvoke(new Action(() => _tray?.ShowBalloon(title, text)));
        service.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ReplayService.StateTitle))
                Dispatcher.BeginInvoke(new Action(() => _tray?.SetText("ClipRing: " + service.StateTitle)));
        };
        Loc.LanguageChanged += () =>
        {
            _tray?.ApplyTexts();
            RegisterHotkeys(); // error texts in the new language
        };

        _window = new MainWindow { DataContext = viewModel };
        MainWindow = _window;
        bool startHidden = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        if (!startHidden) _window.Show();

        // Keep the autostart entry pointing at the current exe (e.g. after moving the folder).
        if (settings.StartWithWindows) Autostart.Apply(true, settings.StartMinimized);

        _ = StartServiceAsync();
    }

    private async Task StartServiceAsync()
    {
        try
        {
            await _service!.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Initialisierung fehlgeschlagen", ex);
        }
    }

    private void RegisterHotkeys()
    {
        if (_registeringHotkeys || _hotkeys is null || _settings is null || _service is null || _viewModel is null) return;
        var hotkeys = _hotkeys;
        var service = _service;
        var settings = _settings;
        var errors = new List<string>();
        var migrated = new List<string>();
        _registeringHotkeys = true; // changing a setting below would re-enter via PropertyChanged
        try
        {
            void Register(string name, string settingName, Func<string> get, Action<string> set, Action action)
            {
                var error = hotkeys.Register(name, get(), action);
                // The v0.1 defaults collide with NVIDIA's overlay: move them to the new defaults once.
                if (error is not null && AppSettings.LegacyHotkeyDefaults.TryGetValue(settingName, out var legacy) &&
                    get() == legacy.Old)
                {
                    set(legacy.New);
                    migrated.Add($"{HotkeyBinding.DisplayText(legacy.Old)} → {HotkeyBinding.DisplayText(legacy.New)}");
                    error = hotkeys.Register(name, get(), action);
                }
                if (error is not null) errors.Add(error);
            }

            Register("save", nameof(AppSettings.HotkeySave), () => settings.HotkeySave, v => settings.HotkeySave = v,
                     () => _ = service.SaveClipAsync(settings.BufferSeconds));
            Register("short", nameof(AppSettings.HotkeySaveShort), () => settings.HotkeySaveShort, v => settings.HotkeySaveShort = v,
                     () => _ = service.SaveClipAsync(settings.ShortClipSeconds));
            Register("toggle", nameof(AppSettings.HotkeyToggle), () => settings.HotkeyToggle, v => settings.HotkeyToggle = v,
                     service.TogglePause);
        }
        finally
        {
            _registeringHotkeys = false;
        }

        _viewModel.HotkeyErrors = errors.Count == 0
            ? ""
            : string.Join(Environment.NewLine, errors) + Environment.NewLine + Loc.T("Hotkey_ChooseOther");

        if (migrated.Count > 0)
        {
            Log.Info("Hotkeys umgestellt, weil die alten belegt sind: " + string.Join(", ", migrated));
            _tray?.ShowBalloon(Loc.T("Notify_HotkeysChangedTitle"), Loc.F("Notify_HotkeysChanged", string.Join(", ", migrated)));
        }
    }

    public void ShowMainWindow()
    {
        if (_window is null || IsExiting) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;  // reliably come to the front …
        _window.Topmost = false; // … without staying on top
        _window.Focus();
    }

    public void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        Log.Info("ClipRing wird beendet");
        try { _hotkeys?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _service?.Dispose(); } catch (Exception ex) { Log.Error("Fehler beim Beenden", ex); }
        try { _window?.Close(); } catch { }
        Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unbehandelter Fehler in der Oberfläche", e.Exception);
        e.Handled = true; // keep the background recorder alive
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!IsExiting)
        {
            IsExiting = true;
            try { _hotkeys?.Dispose(); } catch { }
            try { _tray?.Dispose(); } catch { }
            try { _service?.Dispose(); } catch { }
        }
        _showEvent?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        Log.Shutdown();
        base.OnExit(e);
    }
}
