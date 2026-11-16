using System.Text;
using System.Text.Json;
using ClipRing.Highlights;
using Xunit;

namespace ClipRing.Tests;

public class Cs2GameStateServerTests
{
    private const string Token = "secret";
    private const string Me = "76561190000000001";
    private const string Teammate = "76561190000000002";

    private readonly Cs2GameStateServer _server = new(55000, Token, ignoreWarmup: true);
    private readonly List<(int Kills, int Headshots, string? Mode)> _kills = new();
    private int _ended, _finished;

    public Cs2GameStateServerTests()
    {
        _server.KillsDetected += (k, h, m) => _kills.Add((k, h, m));
        _server.RoundEnded += () => _ended++;
        _server.RoundFinished += () => _finished++;
    }

    private void Send(int roundKills, int headshots = 0, int health = 100, string mapPhase = "live",
                      string roundPhase = "live", string player = Me, string token = Token)
    {
        var payload = new
        {
            auth = new { token },
            provider = new { steamid = Me },
            map = new { name = "de_mirage", mode = "competitive", phase = mapPhase },
            round = new { phase = roundPhase },
            player = new { steamid = player, state = new { health, round_kills = roundKills, round_killhs = headshots } },
        };
        _server.Process(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
    }

    [Fact]
    public void ReportsNewKillsAsDifference()
    {
        Send(0);
        Send(1, headshots: 1);
        Send(1, headshots: 1); // same state again (heartbeat)
        Send(3, headshots: 1);

        Assert.Equal(new[] { (1, 1, (string?)"competitive"), (2, 0, (string?)"competitive") }, _kills);
    }

    [Fact]
    public void FirstMessageIsOnlyTheBaseline()
    {
        Send(2);
        Assert.Empty(_kills);
    }

    [Fact]
    public void DeathAndSpectatingEndTheRoundSoftly()
    {
        Send(0);
        Send(1, health: 0);
        Assert.Single(_kills); // the trade kill in the same update still counts
        Assert.Equal(1, _ended);

        Send(5, player: Teammate); // spectating a teammate: their kills are not ours
        Assert.Single(_kills);
        Assert.Equal(2, _ended);
        Assert.Equal(0, _finished);
    }

    [Fact]
    public void FreezetimeAndKillResetFinishTheRound()
    {
        Send(0);
        Send(2);
        Send(2, roundPhase: "over");
        Assert.Equal(1, _ended);

        Send(0, roundPhase: "freezetime");
        Assert.True(_finished >= 1);
    }

    [Fact]
    public void WarmupKillsAreIgnored()
    {
        Send(0, mapPhase: "warmup");
        Send(1, mapPhase: "warmup");
        Assert.Empty(_kills);
    }

    [Fact]
    public void ForeignTokenIsIgnored()
    {
        Send(0, token: "other");
        Send(1, token: "other");
        Assert.Empty(_kills);
        Assert.Equal(DateTime.MinValue, _server.LastMessageUtc);
    }
}
