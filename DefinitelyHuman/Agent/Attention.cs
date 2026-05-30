namespace DefinitelyHuman.Agent;

/// <summary>
/// Models a human's wandering attention to an IRC channel as a single "focus" value in
/// [0, 1] that decays over wall-clock time (computed lazily on read — no background loop).
///
/// A direct mention snaps focus to full; sending a reply keeps it high; ambient chatter the
/// bot ignores simply lets focus fade. Focus then governs whether the bot even "notices" a
/// non-mention message and how long it takes to glance over before replying.
/// </summary>
public sealed class Attention
{
    private readonly double _halfLifeMinutes;
    private readonly double _glanceFloor;
    private readonly double _engagedFocus;
    private readonly Random _rng;
    private readonly object _gate = new();

    private double _focus;
    private DateTime _updatedAt = DateTime.UtcNow;

    /// <param name="halfLifeMinutes">Time for focus to halve. Lower = more easily distracted.</param>
    /// <param name="glanceFloor">Below this focus, ambient messages are never noticed.</param>
    /// <param name="engagedFocus">Focus restored when the bot actually replies.</param>
    public Attention(double halfLifeMinutes = 4.0, double glanceFloor = 0.15,
                     double engagedFocus = 0.9, Random? rng = null)
    {
        _halfLifeMinutes = halfLifeMinutes;
        _glanceFloor = glanceFloor;
        _engagedFocus = engagedFocus;
        _rng = rng ?? new Random();
    }

    /// <summary>Current focus, decayed from its last value to now.</summary>
    public double Current()
    {
        lock (_gate)
        {
            double minutes = (DateTime.UtcNow - _updatedAt).TotalMinutes;
            return _focus * Math.Pow(0.5, minutes / _halfLifeMinutes);
        }
    }

    /// <summary>A direct mention/ping: attention snaps to full regardless of prior state.</summary>
    public void Notice() => Set(1.0);

    /// <summary>The bot engaged (sent a reply), so it keeps watching for a while.</summary>
    public void Engaged() => Set(Math.Max(Current(), _engagedFocus));

    private void Set(double value)
    {
        lock (_gate)
        {
            _focus = Math.Clamp(value, 0.0, 1.0);
            _updatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Did the human happen to glance at this ambient (non-mention) message? Scales with focus:
    /// fully focused notices ~everything, below the floor notices nothing.
    /// </summary>
    public bool NoticesAmbient(double focus) => focus >= _glanceFloor && _rng.NextDouble() < focus;

    /// <summary>
    /// Padding after a direct mention — the beep grabs you, but you still context-switch and
    /// read before typing. Short, hard-capped at a minute.
    /// </summary>
    public TimeSpan MentionNoticeDelay() => TimeSpan.FromSeconds(4 + _rng.NextDouble() * 36);

    /// <summary>
    /// How long until you glance over at an ambient message: quick when focused, up to ~2
    /// minutes when barely paying attention.
    /// </summary>
    public TimeSpan AmbientNoticeDelay(double focus)
    {
        double seconds = 5 + (1 - focus) * 115;
        seconds += (_rng.NextDouble() - 0.5) * 10; // +/-5s jitter
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 120));
    }
}
