using ClipRing.Core;
using ClipRing.Highlights;
using Xunit;

namespace ClipRing.Tests;

public class HighlightManagerTests
{
    private const string Game = "Counter-Strike 2";

    private readonly ManualTimeProvider _clock = new();
    private readonly AppSettings _settings = new() { BufferSeconds = 120, HighlightPreRollSec = 8, HighlightPostRollSec = 4 };
    private readonly List<HighlightClip> _clips = new();
    private readonly HighlightManager _manager;

    public HighlightManagerTests()
    {
        _manager = new HighlightManager(_settings, _clock, action => action());
        _manager.ClipRequested += _clips.Add;
    }

    [Fact]
    public void KillsFarApartInOneRoundMakeOneClip()
    {
        _manager.ReportKills(Game, 1, 0, minKills: 3);
        _clock.AdvanceSeconds(25);
        _manager.ReportKills(Game, 1, 0, minKills: 3);
        _clock.AdvanceSeconds(30);
        _manager.ReportKills(Game, 1, 1, minKills: 3);
        _clock.AdvanceSeconds(10);
        Assert.Empty(_clips); // round still running

        _manager.EndRound();
        _clock.AdvanceSeconds(1);

        var clip = Assert.Single(_clips);
        Assert.Equal("3K", clip.Tag);
        Assert.Equal(3, clip.Kills);
        Assert.Equal(55 + 4 + 8, clip.Seconds, 3); // first to last kill + post-roll + pre-roll
    }

    [Fact]
    public void LongBombRoundKeepsTheFullKillCount()
    {
        // Entry kill at 0:10, retake kills more than two minutes later (bomb planted late).
        _manager.ReportKills(Game, 1, 0, minKills: 2);
        _clock.AdvanceSeconds(130);
        _manager.ReportKills(Game, 2, 0, minKills: 2);
        _clock.AdvanceSeconds(5);
        _manager.EndRound();
        _clock.AdvanceSeconds(1);

        var clip = Assert.Single(_clips);
        Assert.Equal("3K", clip.Tag);
        Assert.Equal(_settings.BufferSeconds, clip.Seconds, 3); // longer than the buffer: capped, count intact
    }

    [Fact]
    public void KillsAfterTheNextRoundStartedStartANewClip()
    {
        _manager.ReportKills(Game, 2, 0, minKills: 2);
        _manager.EndRound();
        _manager.FinishRound(); // freezetime of the next round
        _manager.ReportKills(Game, 1, 0, minKills: 2);
        _clock.AdvanceSeconds(10);

        var clip = Assert.Single(_clips);
        Assert.Equal("2K", clip.Tag);
    }

    [Fact]
    public void ExitFragAfterRoundOverJoinsTheRound()
    {
        _manager.ReportKills(Game, 2, 0, minKills: 3);
        _clock.AdvanceSeconds(3);
        _manager.EndRound(); // round over, but the post-roll is still running
        _clock.AdvanceSeconds(0.5);
        _manager.ReportKills(Game, 1, 0, minKills: 3);
        _clock.AdvanceSeconds(10);

        var clip = Assert.Single(_clips);
        Assert.Equal("3K", clip.Tag);
    }

    [Fact]
    public void RoundBelowMinimumIsDropped()
    {
        _manager.ReportKills(Game, 1, 0, minKills: 2);
        _manager.EndRound();
        _clock.AdvanceSeconds(10);
        Assert.Empty(_clips);
        Assert.Null(_manager.ProtectedSinceUtc);
    }

    [Fact]
    public void AceIsSavedWithoutWaitingForTheRoundEnd()
    {
        _manager.ReportKills(Game, 4, 0, minKills: 2);
        _clock.AdvanceSeconds(2);
        _manager.ReportKills(Game, 1, 0, minKills: 2);
        _clock.AdvanceSeconds(5);

        Assert.Equal("Ace", Assert.Single(_clips).Tag);
    }

    [Fact]
    public void AllHeadshotsAreTagged()
    {
        _manager.ReportKills(Game, 2, 2, minKills: 2);
        _manager.EndRound();
        _clock.AdvanceSeconds(5);
        Assert.Equal("2K_HS", Assert.Single(_clips).Tag);
    }

    [Fact]
    public void SafetyNetSavesWhenTheRoundEndNeverComes()
    {
        _manager.ReportKills(Game, 2, 0, minKills: 2);
        _clock.Advance(HighlightManager.MaxWaitForRoundEnd - TimeSpan.FromSeconds(1));
        Assert.Empty(_clips);
        _clock.AdvanceSeconds(2);
        Assert.Single(_clips);
    }

    [Fact]
    public void ProtectedSinceCoversTheRoundsPreRoll()
    {
        var firstKill = _clock.GetUtcNow().UtcDateTime;
        _manager.ReportKills(Game, 1, 0, minKills: 2);
        _clock.AdvanceSeconds(20);
        Assert.Equal(firstKill.AddSeconds(-_settings.HighlightPreRollSec), _manager.ProtectedSinceUtc);
    }
}
