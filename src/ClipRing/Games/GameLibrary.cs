using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClipRing.Core;
using Microsoft.Win32;

namespace ClipRing.Games;

/// <summary>
/// Knows where launchers install games, so an executable path can be mapped to a proper
/// game name: Steam app manifests, Epic manifests, GOG registry entries and a built-in
/// list of common executables.
/// </summary>
public sealed partial class GameLibrary
{
    private readonly object _gate = new();
    private Dictionary<string, string> _knownExecutables = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _installDirs = new(StringComparer.OrdinalIgnoreCase); // full dir → name
    private List<string> _steamLibraries = new();
    private DateTime _lastScan = DateTime.MinValue;

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex VdfPath();

    [GeneratedRegex("\"(name|installdir)\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AcfField();

    public GameLibrary()
    {
        LoadKnownExecutables();
        Rescan();
    }

    public IReadOnlyList<string> SteamLibraries
    {
        get { lock (_gate) return _steamLibraries.ToList(); }
    }

    private void LoadKnownExecutables()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClipRing.known_games.json");
            if (stream is null) return;
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
            if (map is not null) _knownExecutables = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Warn("Spieleliste konnte nicht geladen werden: " + ex.Message);
        }
    }

    public string? KnownExecutable(string exeFileName)
        => _knownExecutables.TryGetValue(exeFileName, out var name) ? name : null;

    public void RescanIfOld()
    {
        if (DateTime.UtcNow - _lastScan > TimeSpan.FromMinutes(10)) Rescan();
    }

    public void Rescan()
    {
        var dirs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var libraries = FindSteamLibraries();

        foreach (var library in libraries)
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;
            foreach (var manifest in SafeEnumerate(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    string? name = null, installDir = null;
                    foreach (Match m in AcfField().Matches(File.ReadAllText(manifest)))
                    {
                        if (m.Groups[1].Value.Equals("name", StringComparison.OrdinalIgnoreCase)) name ??= m.Groups[2].Value;
                        else installDir ??= m.Groups[2].Value;
                    }
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(installDir))
                        dirs[Path.Combine(steamapps, "common", installDir)] = name;
                }
                catch { /* unreadable manifest */ }
            }
        }

        // Epic Games Launcher
        var epicManifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                         "Epic", "EpicGamesLauncher", "Data", "Manifests");
        foreach (var item in SafeEnumerate(epicManifests, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(item));
                var root = doc.RootElement;
                if (root.TryGetProperty("DisplayName", out var dn) && root.TryGetProperty("InstallLocation", out var il))
                {
                    var name = dn.GetString();
                    var location = il.GetString();
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(location))
                        dirs[location.TrimEnd('\\', '/')] = name;
                }
            }
            catch { /* ignore */ }
        }

        // GOG Galaxy
        try
        {
            using var gog = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
            if (gog is not null)
            {
                foreach (var sub in gog.GetSubKeyNames())
                {
                    using var key = gog.OpenSubKey(sub);
                    if (key?.GetValue("gameName") is string name && key.GetValue("path") is string path &&
                        !string.IsNullOrWhiteSpace(path))
                        dirs[path.TrimEnd('\\', '/')] = name;
                }
            }
        }
        catch { /* no GOG */ }

        lock (_gate)
        {
            _installDirs = dirs;
            _steamLibraries = libraries;
            _lastScan = DateTime.UtcNow;
        }
        Log.Info($"Spielebibliotheken gescannt: {dirs.Count} Installationen, {libraries.Count} Steam-Bibliotheken");
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static string? SteamRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string path && Directory.Exists(path))
                return Path.GetFullPath(path.Replace('/', '\\'));
        }
        catch { }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(fallback) ? fallback : null;
    }

    private static List<string> FindSteamLibraries()
    {
        var result = new List<string>();
        var root = SteamRoot();
        if (root is null) return result;
        result.Add(root);

        var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                foreach (Match m in VdfPath().Matches(File.ReadAllText(vdf)))
                {
                    var path = m.Groups[1].Value.Replace(@"\\", @"\");
                    if (Directory.Exists(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase))
                        result.Add(path);
                }
            }
        }
        catch { /* ignore */ }
        return result;
    }

    /// <summary>Game name for an executable path based on launcher install folders, or null.</summary>
    public string? LookupByPath(string exePath)
    {
        Dictionary<string, string> dirs;
        lock (_gate) dirs = _installDirs;

        // Longest matching install directory wins.
        string? best = null;
        int bestLength = 0;
        foreach (var (dir, name) in dirs)
        {
            if (dir.Length > bestLength &&
                exePath.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                best = name;
                bestLength = dir.Length;
            }
        }
        if (best is not null) return best;

        // Folder conventions of launchers without a readable manifest.
        foreach (var marker in new[]
                 {
                     @"\steamapps\common\", @"\Epic Games\", @"\Riot Games\", @"\GOG Galaxy\Games\",
                     @"\Ubisoft Game Launcher\games\", @"\EA Games\", @"\XboxGames\", @"\Battle.net\",
                 })
        {
            int idx = exePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var rest = exePath[(idx + marker.Length)..];
            var folder = rest.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(folder) && rest.Contains('\\')) return folder;
        }
        return null;
    }

    /// <summary>True if the path lies inside a known launcher library.</summary>
    public bool IsInGameLibrary(string exePath) => LookupByPath(exePath) is not null;
}
