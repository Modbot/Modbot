namespace Modbot.Api.Features.WorldLists;

/// <summary>
/// At most one VRChat world search every <see cref="Interval"/>, across the whole deployment (world
/// lists design §2).
/// </summary>
/// <remarks>
/// The <c>worlds.search</c> bucket paces the same searches, but a bucket makes a caller wait for its
/// turn; this answers at once, so a moderator who presses too soon is told how long to wait and the
/// press costs VRChat nothing. Held in memory: a restart forgets it, and the bucket still holds the
/// pace.
/// </remarks>
public sealed class WorldSearchWait
{
    /// <summary>Matches the <c>worlds.search</c> bucket's one request per ten seconds.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private DateTimeOffset? _lastAt;

    /// <summary>
    /// Counts one search at <paramref name="now"/> and returns zero, or returns how long to wait
    /// before the next one is allowed.
    /// </summary>
    public TimeSpan TryStart(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_lastAt is { } last && now - last < Interval)
                return Interval - (now - last);

            _lastAt = now;
            return TimeSpan.Zero;
        }
    }
}
