using ClipRing.Core;

namespace ClipRing.Highlights;

public sealed record HighlightClip(string Game, double Seconds, string Tag, int Kills);

/// <summary>
/// Turns kill events into clip requests. Kills close together become one clip
/// (multi-kill); the clip is saved a few seconds after the last kill so the
/// aftermath is included, and starts a few seconds before the first one.
/// </summary>
public sealed class HighlightManager : IDisposable
{
    private readonly AppSettings _settings;
    private readonly object _gate = new();
    private readonly Timer _flushTimer;

    private string? _game;
    private DateTime _firstKillUtc;
    private int _kills;
    private int _headshots;
    private int _minKills = 1;

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
            if (_game is null)
            {
                _game = game;
                _firstKillUtc = DateTime.UtcNow;
                _kills = 0;
                _headshots = 0;
            }
            _kills += kills;
            _headshots += headshots;
            _minKills = minKills;

            // Very long sprees are cut so the clip stays inside the buffer.
            if ((DateTime.UtcNow - _firstKillUtc).TotalSeconds > Math.Max(20, _settings.BufferSeconds - 20))
            {
                FlushLocked();
                return;
            }
            _flushTimer.Change(TimeSpan.FromSeconds(_settings.HighlightPostRollSec), Timeout.InfiniteTimeSpan);
        }
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
        double seconds = (DateTime.UtcNow - _firstKillUtc).TotalSeconds + _settings.HighlightPreRollSec;
        _game = null;

        if (kills < _minKills) return;
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
        ThreadPool.QueueUserWorkItem(_ => handler?.Invoke(new HighlightClip(game, seconds, tag, kills)));
    }

    public void Dispose() => _flushTimer.Dispose();
}
