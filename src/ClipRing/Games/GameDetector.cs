using System.Diagnostics;
using System.Text.RegularExpressions;
using ClipRing.Core;
using ClipRing.Native;

namespace ClipRing.Games;

public sealed record GameInfo(string ProcessName, string ExePath, string DisplayName, int ProcessId, string Source)
{
    public bool IsSameGame(GameInfo? other)
        => other is not null && other.ProcessId == ProcessId &&
           string.Equals(other.DisplayName, DisplayName, StringComparison.Ordinal);
}

/// <summary>
/// Polls the foreground window and decides whether it is a game. Order of evidence:
/// user exclusions → user names/additions → built-in exe list → launcher library folder →
/// borderless/exclusive fullscreen window.
/// </summary>
public sealed partial class GameDetector : IDisposable
{
    private static readonly HashSet<string> NeverGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp", "TextInputHost",
        "LockApp", "ApplicationFrameHost", "SystemSettings", "Taskmgr", "mmc", "dwm", "ClipRing",
        "chrome", "msedge", "firefox", "opera", "opera_gx", "brave", "vivaldi", "arc", "iexplore",
        "vlc", "mpc-hc64", "mpc-hc", "mpc-be64", "PotPlayerMini64", "mpv", "wmplayer", "Microsoft.Media.Player",
        "Video.UI", "Photos", "PhotosApp", "Spotify", "Discord", "slack", "Teams", "ms-teams", "Zoom", "Telegram",
        "WhatsApp", "obs64", "obs32", "Code", "devenv", "rider64", "idea64", "WINWORD", "EXCEL", "POWERPNT",
        "OUTLOOK", "olk", "AcroRd32", "Acrobat", "mstsc", "vmware-vmx", "VirtualBoxVM", "steamwebhelper", "steam",
        "EpicGamesLauncher", "RiotClientUx", "RiotClientServices", "Battle.net", "EADesktop", "UbisoftConnect",
        "upc", "GalaxyClient", "Playnite.DesktopApp", "Playnite.FullscreenApp", "XboxPcApp", "GameBar",
        "NVIDIA app", "NVIDIA Share", "RadeonSoftware", "cncmd", "notepad", "WindowsTerminal", "powershell", "cmd",
    };

    private static readonly HashSet<string> DesktopClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    private static readonly string[] GenericProductNames =
    {
        "Microsoft® Windows® Operating System", "Unreal Engine", "UnrealGame", "UE4Game", "UE5Game",
        "Unity", "Unity Player", "BootstrapPackagedGame", "Game", "Launcher",
    };

    private readonly AppSettings _settings;
    private readonly GameLibrary _library;
    private readonly int _ownPid = Environment.ProcessId;
    private readonly Dictionary<string, string> _nameCache = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private int _ticking;

    [GeneratedRegex(@"(-Win64-Shipping|-WinGDK-Shipping|-Shipping|_x64|_x86|x64|Win64|_dx11|_dx12|DX11|DX12|_vulkan)$",
                    RegexOptions.IgnoreCase)]
    private static partial Regex ExeSuffix();

    public GameDetector(AppSettings settings, GameLibrary library)
    {
        _settings = settings;
        _library = library;
    }

    /// <summary>Game currently in the foreground (null = desktop/other app).</summary>
    public GameInfo? Current { get; private set; }

    /// <summary>The last game that was in the foreground.</summary>
    public GameInfo? LastSeen { get; private set; }
    public DateTime LastSeenUtc { get; private set; }

    /// <summary>Description of the foreground process for the UI ("cs2.exe → Counter-Strike 2").</summary>
    public string ForegroundDescription { get; private set; } = "";

    public event Action<GameInfo?>? CurrentChanged;

    public void Start()
    {
        _timer ??= new Timer(_ => Tick(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1.5));
    }

    /// <summary>Game to use for naming a clip: the foreground game, or the last one if it is still running.</summary>
    public GameInfo? ResolveForClip()
    {
        if (Current is not null) return Current;
        var last = LastSeen;
        if (last is not null && IsRunning(last) && DateTime.UtcNow - LastSeenUtc < TimeSpan.FromMinutes(10))
            return last;
        return null;
    }

    public static bool IsRunning(GameInfo game)
    {
        try
        {
            using var p = Process.GetProcessById(game.ProcessId);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True while the last detected game process is alive (even if alt-tabbed).</summary>
    public bool IsGameRunning => Current is not null || (LastSeen is not null && IsRunning(LastSeen));

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            _library.RescanIfOld();
            var hwnd = Win32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == _ownPid) return; // our own window: keep the previous state

            var path = Win32.GetProcessPath(pid);
            if (path is null) return;

            var game = Classify(hwnd, (int)pid, path);
            ForegroundDescription = game is null
                ? $"{Path.GetFileName(path)}: kein Spiel"
                : $"{Path.GetFileName(path)}: {game.DisplayName} (erkannt über {game.Source})";

            if (game is not null)
            {
                LastSeen = game;
                LastSeenUtc = DateTime.UtcNow;
            }

            var previous = Current;
            bool changed = game is null ? previous is not null : !game.IsSameGame(previous);
            if (changed)
            {
                Current = game;
                Log.Info(game is null ? "Kein Spiel mehr im Vordergrund" : $"Spiel erkannt: {game.DisplayName} ({game.Source})");
                CurrentChanged?.Invoke(game);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Spielerkennung: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private GameInfo? Classify(IntPtr hwnd, int pid, string path)
    {
        string exe = Path.GetFileName(path);
        string process = Path.GetFileNameWithoutExtension(path);

        if (_settings.ExcludedProcesses.Any(x => x.Equals(process, StringComparison.OrdinalIgnoreCase))) return null;
        if (NeverGames.Contains(process)) return null;

        if (_settings.CustomGameNames.TryGetValue(process, out var custom) && !string.IsNullOrWhiteSpace(custom))
            return new GameInfo(process, path, custom.Trim(), pid, "eigenen Namen");

        var known = _library.KnownExecutable(exe);
        if (known is not null) return new GameInfo(process, path, known, pid, "Spieleliste");

        bool forced = _settings.ExtraGameProcesses.Any(x => x.Equals(process, StringComparison.OrdinalIgnoreCase));
        var libraryName = _library.LookupByPath(path);
        if (libraryName is not null) return new GameInfo(process, path, libraryName, pid, "Spielebibliothek");

        if (forced) return new GameInfo(process, path, NiceName(path), pid, "eigene Liste");

        if (_settings.DetectFullscreenApps && !DesktopClasses.Contains(Win32.GetClassName(hwnd)) &&
            Win32.IsFullscreenWindow(hwnd))
            return new GameInfo(process, path, NiceName(path), pid, "Vollbild");

        return null;
    }

    /// <summary>Readable name from the exe's version info, or a cleaned-up file name.</summary>
    private string NiceName(string path)
    {
        lock (_nameCache)
        {
            if (_nameCache.TryGetValue(path, out var cached)) return cached;
        }

        string? name = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            foreach (var candidate in new[] { info.ProductName, info.FileDescription })
            {
                var c = candidate?.Trim();
                if (string.IsNullOrWhiteSpace(c) || c.Length > 60) continue;
                if (GenericProductNames.Any(g => c.Equals(g, StringComparison.OrdinalIgnoreCase))) continue;
                name = c;
                break;
            }
        }
        catch { /* no version info */ }

        if (name is null)
        {
            var stem = ExeSuffix().Replace(Path.GetFileNameWithoutExtension(path), "");
            name = stem.Replace('_', ' ').Replace('-', ' ').Trim();
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(path);
        }

        lock (_nameCache) _nameCache[path] = name;
        return name;
    }

    public void Dispose() => _timer?.Dispose();
}
