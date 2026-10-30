using ClipRing.Core;

namespace ClipRing.Highlights;

/// <param name="End">Local time the clip ends at (last kill plus post-roll).</param>
public sealed record HighlightClip(string Game, double Seconds, DateTime End, string Tag, int Kills);

/// <summary>
/// Turns kill events into clip requests. All kills of a round become one clip, however far apart they
/// are (a 3K is three kills in one round, not three kills within a few seconds). The game source ends the
/// round with <see cref="EndRound"/> (player died, round over); the clip then runs from a few seconds
/// before the first kill to a few seconds after the last one.
/// </summary>
public sealed class HighlightManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly object _gate = new();
    private readonly Timer _flushTimer;

    private string? _game;
    private DateTime _firstKillUtc;
    private DateTime _lastKillUtc;
    private int _kills;
    private int _headshots;
    private int _minKills = 1;
    private bool _ending;

    public HighlightManager(AppSettings settings)
    {
        _settings = settings;
        _flushTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<HighlightClip>? ClipRequested;

    public void ReportKills(string game, int kills, int headshots, int minKills)
    {
        if (kills <= 0) return;
        lock (_gate)
        {
            if (_game is not null && _game != game) FlushLocked();
            var now = DateTime.UtcNow;
            if (_game is null)
            {
                _game = game;
                _firstKillUtc = now;
                _kills = 0;
                _headshots = 0;
            }
            _kills += kills;
            _headshots += headshots;
            _lastKillUtc = now;
            _minKills = minKills;
            _ending = false;
            Log.Info($"Kill gezählt: {game}, {_kills} in dieser Runde");

            if (_kills >= 5)
            {
                EndRoundLocked(); // ace: nothing more to wait for
                return;
            }
            // The round normally ends first. If the end is never reported (GSI stops, game closed), save
            // before the first kill falls out of the replay buffer.
            double keep = _settings.BufferSeconds - _settings.HighlightPreRollSec - _settings.HighlightPostRollSec - 5;
            double left = Math.Max(1, keep - (now - _firstKillUtc).TotalSeconds);
            _flushTimer.Change(TimeSpan.FromSeconds(left), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// No more kills this round (died, round over, new round): save once the post-roll after the last
    /// kill is in the buffer. Kills reported before that still join the clip.
    /// </summary>
    public void EndRound()
    {
        lock (_gate)
        {
            if (_game is null || _ending) return;
            EndRoundLocked();
        }
    }

    private void EndRoundLocked()
    {
        _ending = true;
        var due = _lastKillUtc.AddSeconds(_settings.HighlightPostRollSec) - DateTime.UtcNow;
        _flushTimer.Change(due > TimeSpan.FromMilliseconds(200) ? due : TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        lock (_gate) FlushLocked();
    }

    private void FlushLocked()
    {
        _flushTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_game is null) return;

        string game = _game;
        int kills = _kills, headshots = _headshots;
        var endUtc = _lastKillUtc.AddSeconds(_settings.HighlightPostRollSec);
        if (endUtc > DateTime.UtcNow) endUtc = DateTime.UtcNow;
        double seconds = (endUtc - _firstKillUtc).TotalSeconds + _settings.HighlightPreRollSec;
        _game = null;
        _ending = false;

        if (kills < _minKills)
        {
            Log.Info($"Kein Highlight: {kills} Kill(s) in der Runde, eingestellt sind mindestens {_minKills}");
            return;
        }
        seconds = Math.Min(seconds, _settings.BufferSeconds);
        string tag = kills switch
        {
            1 => "Kill",
            5 => "Ace",
            _ => $"{kills}K",
        };
        if (headshots > 0 && headshots == kills) tag += "_HS";
        Log.Info($"Highlight erkannt: {game} {tag} ({seconds:F0}s)");
        var handler = ClipRequested;
        var clip = new HighlightClip(game, seconds, endUtc.ToLocalTime(), tag, kills);
        ThreadPool.QueueUserWorkItem(_ => handler?.Invoke(clip));
    }

    public void Dispose() => _flushTimer.Dispose();
}
