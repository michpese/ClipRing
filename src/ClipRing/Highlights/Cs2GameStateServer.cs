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

    private int? _roundKills;
    private int? _roundHeadshots;

    public Cs2GameStateServer(int port, string token, bool ignoreWarmup)
    {
        _port = port;
        _token = token;
        _ignoreWarmup = ignoreWarmup;
    }

    /// <summary>kills, headshots</summary>
    public event Action<int, int>? KillsDetected;

    public DateTime LastMessageUtc { get; private set; } = DateTime.MinValue;
    public string? LastMap { get; private set; }

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
                    catch (Exception ex) { Log.Warn("CS2-GSI-Daten nicht lesbar: " + ex.Message); }

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

    private void Process(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("auth", out var auth) || !auth.TryGetProperty("token", out var token) ||
            token.GetString() != _token)
            return; // not ours

        LastMessageUtc = DateTime.UtcNow;

        string? provider = root.TryGetProperty("provider", out var prov) && prov.TryGetProperty("steamid", out var ps) ? ps.GetString() : null;
        if (root.TryGetProperty("map", out var map))
        {
            if (map.TryGetProperty("name", out var mn)) LastMap = mn.GetString();
        }
        string? phase = root.TryGetProperty("map", out var m2) && m2.TryGetProperty("phase", out var ph) ? ph.GetString() : null;

        if (!root.TryGetProperty("player", out var player)) return;
        string? playerId = player.TryGetProperty("steamid", out var pid) ? pid.GetString() : null;

        // While dead and spectating, "player" is somebody else: keep our baseline untouched.
        if (provider is null || playerId != provider) return;
        if (!player.TryGetProperty("state", out var state)) return;

        int roundKills = state.TryGetProperty("round_kills", out var rk) && rk.TryGetInt32(out var rkv) ? rkv : 0;
        int roundHs = state.TryGetProperty("round_killhs", out var rh) && rh.TryGetInt32(out var rhv) ? rhv : 0;

        int? previousKills = _roundKills;
        int previousHs = _roundHeadshots ?? 0;
        _roundKills = roundKills;
        _roundHeadshots = roundHs;

        if (previousKills is null) return;                 // first message: baseline only
        if (roundKills <= previousKills) return;           // new round (reset) or nothing happened
        if (_ignoreWarmup && phase == "warmup") return;

        int kills = roundKills - previousKills.Value;
        int headshots = Math.Max(0, roundHs - previousHs);
        KillsDetected?.Invoke(kills, Math.Min(headshots, kills));
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}

public static class Cs2GsiInstaller
{
    public const string FileName = "gamestate_integration_cliprings.cfg";

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
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path);
        return text.Contains($":{port}/", StringComparison.Ordinal) && text.Contains(token, StringComparison.Ordinal);
    }

    /// <summary>Writes the GSI config into the CS2 cfg folder. Returns the file path.</summary>
    public static string Install(GameLibrary library, int port, string token)
    {
        library.Rescan();
        var dir = FindCfgDirectory(library)
            ?? throw new DirectoryNotFoundException("Counter-Strike 2 wurde in keiner Steam-Bibliothek gefunden.");

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
        Log.Info("CS2-GSI-Konfiguration geschrieben: " + path);
        return path;
    }
}
