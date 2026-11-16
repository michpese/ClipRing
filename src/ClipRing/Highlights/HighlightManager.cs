using ClipRing.Core;

namespace ClipRing.Highlights;

/// <param name="End">Local time the clip ends at (last kill plus post-roll).</param>
public sealed record HighlightClip(string Game, double Seconds, DateTime End, string Tag, int Kills);

/// <summary>
/// Turns kill events into clip requests. All kills of a round become one clip, however far apart they
/// are (a 3K is three kills in one round, not three kills within a few seconds). The clip runs from a few
/// seconds before the first kill to a few seconds after the last one.
/// <para>
/// The game source reports two kinds of round end: <see cref="EndRound"/> when the player can't get more
/// kills (died, round over), after which late kills still join (a grenade after death, exit frags), and
/// <see cref="FinishRound"/> when the next round starts, after which kills belong to a new clip.
/// </para>
/// </summary>
public sealed class HighlightManager : IDisposable
{
    /// <summary>
    /// Safety net only, if the round end never arrives (game closed, GSI stopped). A CS2 round runs at most
    /// 1:55 plus 40 s of bomb timer, so this never cuts a round that is still being played.
    /// </summary>
    public static readonly TimeSpan MaxWaitForRoundEnd = TimeSpan.FromSeconds(180);

    private readonly AppSettings _settings;
    private readonly TimeProvider _time;
    private readonly Action<Action> _dispatch;
    private readonly object _gate = new();
    private readonly ITimer _flushTimer;

    private string? _game;
    private DateTime _firstKillUtc;
    private DateTime _lastKillUtc;
    private int _kills;
    private int _headshots;
    private int _minKills = 1;
    private bool _ending;   // no more regular kills expected; saved once the post-roll has passed
    private bool _finished; // the next round started: further kills start a new clip

    /// <param name="time">Clock and timers; tests pass a manual clock.</param>
    /// <param name="dispatch">How <see cref="ClipRequested"/> is raised; by default on the thread pool.</param>
    public HighlightManager(AppSettings settings, TimeProvider? time = null, Action<Action>? dispatch = null)
    {
        _settings = settings;
        _time = time ?? TimeProvider.System;
        _dispatch = dispatch ?? (action => ThreadPool.QueueUserWorkItem(_ => action()));
        _flushTimer = _time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event Action<HighlightClip>? ClipRequested;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Start of the round being collected (first kill minus pre-roll), or null. Clearing the replay buffer
    /// must keep everything from here on, or the highlight would lose its first kills.
    /// </summary>
    public DateTime? ProtectedSinceUtc
    {
        get
        {
            lock (_gate) return _game is null ? null : _firstKillUtc.AddSeconds(-_settings.HighlightPreRollSec);
        }
    }

    public void ReportKills(string game, int kills, int headshots, int minKills)
    {
        if (kills <= 0) return;
        lock (_gate)
        {
            if (_game is not null && (_game != game || _finished)) FlushLocked();
            var now = Now;
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
            Log.Info($"Kill gezählt: {game}, {_kills} in dieser Runde");

            if (_kills >= 5 || _ending)
            {
                // Ace: nothing more to wait for. A late kill after the round end: wait for its post-roll.
                EndRoundLocked();
                return;
            }
            _flushTimer.Change(MaxWaitForRoundEnd, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// No more regular kills this round (died, round over): save once the post-roll after the last kill is
    /// in the buffer. Kills reported before that still join the clip.
    /// </summary>
    public void EndRound()
    {
        lock (_gate)
        {
            if (_game is null || _ending) return;
            EndRoundLocked();
        }
    }

    /// <summary>The next round started (or the match ended): the collected round is complete for good.</summary>
    public void FinishRound()
    {
        lock (_gate)
        {
            if (_game is null || _finished) return;
            _finished = true;
            if (!_ending) EndRoundLocked();
        }
    }

    private void EndRoundLocked()
    {
        _ending = true;
        var due = _lastKillUtc.AddSeconds(_settings.HighlightPostRollSec) - Now;
        _flushTimer.Change(due > TimeSpan.FromMilliseconds(200) ? due : TimeSpan.FromMilliseconds(200), Timeout.InfiniteTimeSpan);
    }

    private void Flush()
    {
        lock (_gate) FlushLocked();
    }

    private void FlushLocked()
    {
        _flushTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_game is null) return;

        string game = _game;
        int kills = _kills, headshots = _headshots;
        var now = Now;
        var endUtc = _lastKillUtc.AddSeconds(_settings.HighlightPostRollSec);
        if (endUtc > now) endUtc = now;
        double seconds = (endUtc - _firstKillUtc).TotalSeconds + _settings.HighlightPreRollSec;
        _game = null;
        _ending = false;
        _finished = false;

        if (kills < _minKills)
        {
            Log.Info($"Kein Highlight: {kills} Kill(s) in der Runde, eingestellt sind mindestens {_minKills}");
            return;
        }
        if (seconds > _settings.BufferSeconds)
        {
            // A long round: the start is no longer in the replay buffer, the kill count still is right.
            Log.Info($"Highlight länger als der Puffer ({seconds:F0}s), Anfang fehlt");
            seconds = _settings.BufferSeconds;
        }
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
        if (handler is not null) _dispatch(() => handler(clip));
    }

    public void Dispose() => _flushTimer.Dispose();
}
