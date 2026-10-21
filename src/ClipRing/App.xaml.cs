using System.Windows;
using System.Windows.Threading;
using ClipRing.Core;
using ClipRing.Hotkeys;
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

    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
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
        var service = _service = new ReplayService(settings, Dispatcher);
        var viewModel = _viewModel = new MainViewModel(service);

        _hotkeys = new HotkeyManager();
        RegisterHotkeys();
        settings.PropertyChanged += (_, args) =>
        {
            var name = args.PropertyName ?? "";
            if (AppSettings.HotkeyProperties.Contains(name) || name == nameof(AppSettings.ValorantEnabled))
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
        service.Notify += (title, text) => Dispatcher.BeginInvoke(new Action(() => _tray?.ShowBalloon(title, text)));
        service.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ReplayService.StateTitle))
                Dispatcher.BeginInvoke(new Action(() => _tray?.SetText("ClipRing: " + service.StateTitle)));
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
        if (_hotkeys is null || _settings is null || _service is null || _viewModel is null) return;
        var service = _service;
        var settings = _settings;
        var errors = new List<string>();

        void Register(string name, string text, Action action)
        {
            var error = _hotkeys!.Register(name, text, action);
            if (error is not null) errors.Add(error);
        }

        Register("save", settings.HotkeySave, () => _ = service.SaveClipAsync(settings.BufferSeconds));
        Register("short", settings.HotkeySaveShort, () => _ = service.SaveClipAsync(settings.ShortClipSeconds));
        Register("toggle", settings.HotkeyToggle, service.TogglePause);
        if (settings.ValorantEnabled)
            Register("valorant", settings.HotkeyValorantCalibrate, service.CaptureValorantTemplate);
        else
            _hotkeys.Unregister("valorant");

        _viewModel.HotkeyErrors = string.Join(Environment.NewLine, errors);
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
