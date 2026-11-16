using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ClipRing.Core;
using ClipRing.Games;

namespace ClipRing.Highlights;

/// <summary>
/// Receives Counter-Strike 2 Game State Integration (GSI) updates. CS2 POSTs JSON to a local
/// HTTP endpoint configured in a gamestate_integration_*.cfg file. This is Valve's official
/// interface, so it is anti-cheat safe. A minimal HTTP/1.1 server on 127.0.0.1 is enough and
/// avoids HTTP.sys URL reservations.
/// </summary>
public sealed class Cs2GameStateServer : IDisposable
{
    public const string GameName = "Counter-Strike 2";

    private readonly int _port;
    private readonly string _token;
    private readonly bool _ignoreWarmup;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    private readonly object _stateGate = new();
    private int? _roundKills;
    private int? _roundHeadshots;

    public Cs2GameStateServer(int port, string token, bool ignoreWarmup)
    {
        _port = port;
        _token = token;
        _ignoreWarmup = ignoreWarmup;
    }

    /// <summary>kills, headshots, game mode (GSI map.mode, e.g. "competitive", "deathmatch")</summary>
    public event Action<int, int, string?>? KillsDetected;

    /// <summary>No more regular kills this round: the player died or the round is over.</summary>
    public event Action? RoundEnded;

    /// <summary>The next round started or the match ended: kills from now on belong to a new round.</summary>
    public event Action? RoundFinished;

    public DateTime LastMessageUtc { get; private set; } = DateTime.MinValue;
    public string? LastMap { get; private set; }
    public string? LastMode { get; private set; }

    /// <summary>Display name of a GSI game mode in the UI language.</summary>
    public static string ModeName(string? mode) => mode switch
    {
        "competitive" or "premier" or "scrimcomp2v2" or "casual" or "deathmatch" or "gungameprogressive"
            or "gungametrbomb" or "custom" => Localization.Loc.T("Cs2Mode_" + mode),
        null or "" => Localization.Loc.T("Cs2Mode_unknown"),
        _ => mode,
    };

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        Log.Info($"CS2-GSI-Empfänger lauscht auf 127.0.0.1:{_port}");
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new List<byte>(16 * 1024);
                var chunk = new byte[16 * 1024];
                while (!ct.IsCancellationRequested)
                {
                    // Read headers.
                    int headerEnd;
                    while ((headerEnd = FindHeaderEnd(buffer)) < 0)
                    {
                        int n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                        if (n <= 0) return;
                        buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                        if (buffer.Count > 1024 * 1024) return;
                    }
                    string headers = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray());
                    int contentLength = 0;
                    bool close = false;
                    foreach (var line in headers.Split("\r\n"))
                    {
                        int colon = line.IndexOf(':');
                        if (colon <= 0) continue;
                        var name = line[..colon].Trim();
                        var value = line[(colon + 1)..].Trim();
                        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out contentLength);
                        if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) && value.Equals("close", StringComparison.OrdinalIgnoreCase)) close = true;
                    }
                    if (contentLength < 0 || contentLength > 4 * 1024 * 1024) return;

                    int bodyStart = headerEnd + 4;
                    while (buffer.Count < bodyStart + contentLength)
                    {
                        int n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                        if (n <= 0) return;
                        buffer.AddRange(chunk.AsSpan(0, n).ToArray());
                    }
                    var body = buffer.GetRange(bodyStart, contentLength).ToArray();
                    buffer.RemoveRange(0, bodyStart + contentLength);

                    var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 0\r\n" +
                                                           (close ? "Connection: close\r\n" : "Connection: keep-alive\r\n") + "\r\n");
                    await stream.WriteAsync(response, ct).ConfigureAwait(false);

                    try { Process(body); }
                    catch (Exception ex) { Log.Warn("CS2-GSI-Daten nicht verarbeitet: " + ex.Message); }

                    if (close) return;
                }
            }
            catch
            {
                // client disconnected
            }
        }
    }

    private static int FindHeaderEnd(List<byte> data)
    {
        for (int i = 0; i + 3 < data.Count; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') return i;
        }
        return -1;
    }

    /// <summary>Handles one GSI update. Connections run in parallel, so updates are handled one at a time.</summary>
    internal void Process(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        lock (_stateGate) Process(doc.RootElement);
    }

    private void Process(JsonElement root)
    {
        if (!root.TryGetProperty("auth", out var auth) || !auth.TryGetProperty("token", out var token) ||
            token.GetString() != _token)
            return; // not ours

        LastMessageUtc = DateTime.UtcNow;

        string? provider = root.TryGetProperty("provider", out var prov) && prov.TryGetProperty("steamid", out var ps) ? ps.GetString() : null;
        if (root.TryGetProperty("map", out var map))
        {
            if (map.TryGetProperty("name", out var mn)) LastMap = mn.GetString();
            if (map.TryGetProperty("mode", out var md)) LastMode = md.GetString();
        }
        string? phase = root.TryGetProperty("map", out var m2) && m2.TryGetProperty("phase", out var ph) ? ph.GetString() : null;
        string? roundPhase = root.TryGetProperty("round", out var round) && round.TryGetProperty("phase", out var rp) ? rp.GetString() : null;

        // Checked after the kills: the kill that ends a round (or a trade) comes in the same update.
        var state = ProcessPlayer(root, provider, phase);
        if (state == PlayerRound.NewRound || phase == "gameover" || roundPhase == "freezetime") RoundFinished?.Invoke();
        else if (state == PlayerRound.Dead || roundPhase == "over") RoundEnded?.Invoke();
    }

    private enum PlayerRound { Playing, Dead, NewRound }

    private PlayerRound ProcessPlayer(JsonElement root, string? provider, string? phase)
    {
        if (provider is null || !root.TryGetProperty("player", out var player)) return PlayerRound.Playing;
        string? playerId = player.TryGetProperty("steamid", out var pid) ? pid.GetString() : null;

        // While dead and spectating, "player" is somebody else: keep our baseline untouched.
        if (playerId != provider) return PlayerRound.Dead;
        if (!player.TryGetProperty("state", out var state)) return PlayerRound.Playing;

        int roundKills = state.TryGetProperty("round_kills", out var rk) && rk.TryGetInt32(out var rkv) ? rkv : 0;
        int roundHs = state.TryGetProperty("round_killhs", out var rh) && rh.TryGetInt32(out var rhv) ? rhv : 0;

        int? previousKills = _roundKills;
        int previousHs = _roundHeadshots ?? 0;
        _roundKills = roundKills;
        _roundHeadshots = roundHs;

        // First message: baseline only. Fewer kills than before: a new round started.
        if (previousKills is not null && roundKills > previousKills && !(_ignoreWarmup && phase == "warmup"))
        {
            int kills = roundKills - previousKills.Value;
            int headshots = Math.Max(0, roundHs - previousHs);
            KillsDetected?.Invoke(kills, Math.Min(headshots, kills), LastMode);
        }

        if (previousKills is not null && roundKills < previousKills) return PlayerRound.NewRound;
        int health = state.TryGetProperty("health", out var hp) && hp.TryGetInt32(out var hpv) ? hpv : 100;
        return health <= 0 ? PlayerRound.Dead : PlayerRound.Playing;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}

public static class Cs2GsiInstaller
{
    public const string FileName = "gamestate_integration_clipring.cfg";

    /// <summary>The file's name up to 0.1.3; replaced when setting up again.</summary>
    public const string LegacyFileName = "gamestate_integration_cliprings.cfg";

    public static string? FindCfgDirectory(GameLibrary library)
    {
        foreach (var lib in library.SteamLibraries)
        {
            var dir = Path.Combine(lib, "steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo", "cfg");
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }

    public static bool IsInstalled(GameLibrary library, int port, string token)
    {
        var dir = FindCfgDirectory(library);
        if (dir is null) return false;
        foreach (var name in new[] { FileName, LegacyFileName })
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            if (text.Contains($":{port}/", StringComparison.Ordinal) && text.Contains(token, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Writes the GSI config into the CS2 cfg folder. Returns the file path.</summary>
    public static string Install(GameLibrary library, int port, string token)
    {
        library.Rescan();
        var dir = FindCfgDirectory(library)
            ?? throw new DirectoryNotFoundException(Localization.Loc.T("Cs2_NotFound"));

        var cfg = $$"""
            "ClipRing"
            {
                "uri"       "http://127.0.0.1:{{port}}/"
                "timeout"   "1.0"
                "buffer"    "0.1"
                "throttle"  "0.1"
                "heartbeat" "15.0"
                "auth"
                {
                    "token" "{{token}}"
                }
                "data"
                {
                    "provider"           "1"
                    "map"                "1"
                    "round"              "1"
                    "player_id"          "1"
                    "player_state"       "1"
                    "player_match_stats" "1"
                }
            }
            """;
        var path = Path.Combine(dir, FileName);
        // CS2 ignores cfg files with a UTF-8 BOM.
        File.WriteAllText(path, cfg.Replace("\r\n", "\n"), new UTF8Encoding(false));
        // Two files with the same address would make CS2 send every update twice.
        var legacy = Path.Combine(dir, LegacyFileName);
        try { if (File.Exists(legacy)) File.Delete(legacy); }
        catch (Exception ex) { Log.Warn("Alte CS2-Konfiguration nicht entfernt: " + ex.Message); }
        Log.Info("CS2-GSI-Konfiguration geschrieben: " + path);
        return path;
    }
}
