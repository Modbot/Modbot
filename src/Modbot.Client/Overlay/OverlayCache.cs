using Modbot.Core.Time;

namespace Modbot.Client.Overlay;

/// <summary>How much to trust what is on screen.</summary>
public enum Freshness
{
    /// <summary>Fetched moments ago.</summary>
    Fresh,

    /// <summary>Old enough that the moderator is told how old, in the card itself.</summary>
    Stale,

    /// <summary>Nothing has ever been fetched for this server. The only honest blank.</summary>
    Never,
}

/// <param name="Age">How long ago the value was fetched. Shown, not implied.</param>
public sealed record Cached<T>(T? Value, Freshness Freshness, TimeSpan Age)
{
    /// <summary>
    /// The line the overlay puts under a card. "Flagged — as of 20 minutes ago" is actionable;
    /// a spinner is not.
    /// </summary>
    public string Describe() => Freshness switch
    {
        Freshness.Fresh => "up to date",
        Freshness.Stale when Age < TimeSpan.FromMinutes(2) => $"as of {(int)Age.TotalSeconds} seconds ago",
        Freshness.Stale when Age < TimeSpan.FromHours(1) => $"as of {(int)Age.TotalMinutes} minutes ago",
        Freshness.Stale => $"as of {(int)Age.TotalHours} hours ago",
        _ => "not loaded",
    };
}

/// <summary>
/// Everything the overlay draws from, per paired server.
/// </summary>
/// <remarks>
/// <para><strong>The overlay renders from here and never from a live request.</strong> That is the
/// whole reason this type exists. The overlay's highest-value moment is "a flagged user just
/// joined", which is also the moment latency and network trouble hurt; an overlay that goes blank
/// in exactly that window is backwards. The client already buffers presence through
/// disconnections, and this is the same principle pointed the other way.</para>
/// <para><strong>Stale data is shown, and said to be stale.</strong> Freshness is displayed rather
/// than implied. A moderator seeing "flagged — as of 20 minutes ago" can act on it; one seeing a
/// spinner cannot.</para>
/// <para><strong>Nothing here is transmitted.</strong> This is a cache of what a server told this
/// client, held in memory only — it is not written to disk, because group context is the
/// operator's data rather than the moderator's, and it has no business outliving the session that
/// fetched it.</para>
/// <para><strong>One cache per server, never merged.</strong> With several groups paired, the
/// overlay shows context from whichever server manages the instance the moderator is actually in,
/// and says which group it is speaking for — a moderator seeing a flag needs to know whose flag
/// it is.</para>
/// </remarks>
public sealed class OverlayCache
{
    private readonly IModbotClock _clock;
    private readonly TimeSpan _staleAfter;
    private readonly Dictionary<string, Entry<InstanceContext>> _contexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entry<UserSummary>> _users = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// After this, a roster is described as stale rather than current. Short, because instance
    /// populations turn over in minutes.
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromSeconds(45);

    public OverlayCache(IModbotClock clock, TimeSpan? staleAfter = null)
    {
        _clock = clock;
        _staleAfter = staleAfter ?? DefaultStaleAfter;
    }

    /// <summary>Which server's data this holds, for the "whose flag is this?" label.</summary>
    public string? ServerId { get; private set; }

    /// <summary>Whether the last attempt to reach the server failed. Shown beside the age.</summary>
    public bool ServerUnreachable { get; private set; }

    public void RecordContext(string serverId, InstanceContext context)
    {
        lock (_gate)
        {
            ServerId = serverId;
            ServerUnreachable = false;
            _contexts[context.InstanceId] = new Entry<InstanceContext>(context, _clock.UtcNow);
        }
    }

    public void RecordUser(string serverId, UserSummary user)
    {
        lock (_gate)
        {
            ServerId = serverId;
            ServerUnreachable = false;
            _users[user.SubjectId] = new Entry<UserSummary>(user, _clock.UtcNow);
        }
    }

    /// <summary>
    /// Notes that the server could not be reached. Nothing is discarded: what was last known is
    /// still the best thing to show, and is now shown with its age.
    /// </summary>
    public void RecordUnreachable()
    {
        lock (_gate)
            ServerUnreachable = true;
    }

    public Cached<InstanceContext> Context(string instanceId)
    {
        lock (_gate)
            return Wrap(_contexts.GetValueOrDefault(instanceId));
    }

    public Cached<UserSummary> User(string subjectId)
    {
        lock (_gate)
            return Wrap(_users.GetValueOrDefault(subjectId));
    }

    /// <summary>Drops everything for this server. Used when a pairing is removed.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _contexts.Clear();
            _users.Clear();
            ServerUnreachable = false;
        }
    }

    private Cached<T> Wrap<T>(Entry<T>? entry) where T : class
    {
        if (entry is null)
            return new Cached<T>(null, Freshness.Never, TimeSpan.Zero);

        var age = _clock.UtcNow - entry.FetchedAt;
        return new Cached<T>(entry.Value, age <= _staleAfter ? Freshness.Fresh : Freshness.Stale, age);
    }

    private sealed record Entry<T>(T Value, DateTimeOffset FetchedAt);
}
