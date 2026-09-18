using Modbot.Core.Data;

namespace Modbot.VRChat.Sync;

/// <summary>
/// Why a profile is worth a request, highest priority first. The number is the tier.
/// </summary>
/// <remarks>
/// User profile sync design §3.1. The order is the maintainer's: somebody in an instance right
/// now beats somebody a moderator is looking at, which beats somebody who was merely active,
/// which beats a profile that is old, which beats a profile that was never fetched at all.
/// </remarks>
public enum RefreshReason
{
    /// <summary>A client reported them in an instance -- a join, a presence report, an avatar change.</summary>
    SeenInInstance = 1,

    /// <summary>Somebody opened their profile in Modbot and is looking at it now.</summary>
    OpenedInModbot = 2,

    /// <summary>They did something the fact log recorded -- joined the group, were banned, were kicked.</summary>
    SeenInFactLog = 3,

    /// <summary>
    /// Their profile is older than <see cref="UserProfileSyncOptions.StaleAfter"/>, and they are
    /// somebody the periodic refresh is for: a current group member, or anyone seen inside
    /// <see cref="UserProfileSyncOptions.RefreshNonMembersFor"/>.
    /// </summary>
    ProfileIsOld = 4,

    /// <summary>
    /// Their profile has never been fetched, and they are somebody the periodic refresh is for --
    /// the same two groups as <see cref="ProfileIsOld"/>.
    /// </summary>
    NeverRefreshed = 5,
}

/// <summary>
/// One pending refresh.
/// </summary>
/// <param name="UserId">Opaque, as always (spec 3.1.1).</param>
/// <param name="Reason">The tier.</param>
/// <param name="Key">
/// What orders entries within the tier: the time of the sighting or the request for the tiers
/// where the most recent goes first, and the time of the last refresh for the one tier where the
/// oldest does.
/// </param>
/// <param name="RequestedAt">
/// When the entry was made. A profile refreshed after this moment has already answered it, so
/// the entry is dropped rather than spent.
/// </param>
public sealed record RefreshRequest(
    string UserId,
    RefreshReason Reason,
    DateTimeOffset Key,
    DateTimeOffset RequestedAt)
{
    /// <summary>
    /// Whether a profile last fetched at <paramref name="lastRefreshedAt"/> already answers this
    /// request, so it can be dropped unspent.
    /// </summary>
    /// <remarks>
    /// Judged against the <see cref="Key"/>, not against when the entry was made: a sighting at
    /// 12:00 queued at 12:01 is answered by a refresh at 12:00:30, and the same sighting queued
    /// again by an overlap re-read a minute later must not earn a second fetch. For the "old
    /// profile" tier the key is the refresh being complained about, so any later one answers it;
    /// for "never refreshed", any refresh at all does.
    /// </remarks>
    public bool IsAnsweredBy(DateTimeOffset? lastRefreshedAt)
    {
        if (lastRefreshedAt is not { } refreshed)
            return false;

        return Reason switch
        {
            RefreshReason.ProfileIsOld => refreshed > Key,
            RefreshReason.NeverRefreshed => true,
            _ => refreshed >= Key,
        };
    }
}

/// <summary>What happened to a request that was offered to the queue.</summary>
public enum RefreshRequestOutcome
{
    /// <summary>Added.</summary>
    Queued,

    /// <summary>The user was already waiting at a lower tier and has been moved up.</summary>
    Promoted,

    /// <summary>The user was already waiting at this tier or a higher one. Nothing changed.</summary>
    AlreadyQueued,

    /// <summary>
    /// The profile was fetched recently enough that this kind of request does not need another
    /// fetch (<see cref="UserProfileSyncOptions.FreshEnoughFor"/>). Answered from what is stored.
    /// </summary>
    FreshEnough,

    /// <summary>
    /// The id is an instance's location, not a person's, and was not queued. An instance is never a profile
    /// to fetch, and one that slips in is answered 400 by VRChat and offered again on every
    /// housekeeping pass for as long as its row lasts.
    /// </summary>
    NotAPerson,
}

/// <summary>
/// The one order every refresh is decided by (user profile sync design §3.2).
/// </summary>
/// <remarks>
/// <para>
/// Tier first. Within a tier, most recent first -- except <see cref="RefreshReason.ProfileIsOld"/>,
/// whose whole point is that the oldest goes first. The user id is the final tie-break so the
/// order is total and two entries never compare equal, which is what lets a sorted set hold them.
/// </para>
/// <para>
/// This is a comparison and nothing else. It does not know about the database, the fresh-enough
/// gaps or the lane; it is the one thing that has to be right for the queue to mean what the
/// design says, and it is unit-tested on its own.
/// </para>
/// </remarks>
public sealed class RefreshOrder : IComparer<RefreshRequest>
{
    public static RefreshOrder Instance { get; } = new();

    public int Compare(RefreshRequest? x, RefreshRequest? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return 1;
        if (y is null) return -1;

        var byTier = ((int)x.Reason).CompareTo((int)y.Reason);
        if (byTier != 0) return byTier;

        var byKey = x.Reason == RefreshReason.ProfileIsOld
            ? x.Key.CompareTo(y.Key)      // oldest profile first
            : y.Key.CompareTo(x.Key);     // most recent sighting or request first

        if (byKey != 0) return byKey;

        return string.CompareOrdinal(x.UserId, y.UserId);
    }
}

/// <summary>
/// Who is waiting for a profile refresh, in the order they will get one.
/// </summary>
/// <remarks>
/// <para>
/// One queue, fed from three directions: the producer's own discovery of new facts (instance
/// sightings and audit-log activity), the web app when somebody opens a profile, and a periodic
/// top-up from the database for the people who are merely old or never fetched. All of them
/// land here and <see cref="RefreshOrder"/> decides between them; there are no separate loops
/// per source, because separate loops would each need their own share of the lane and the
/// design says the lane has one order.
/// </para>
/// <para>
/// <strong>One entry per user.</strong> A request for somebody already waiting either promotes
/// them (higher tier) or is absorbed (same or lower). A moderator clicking the same profile ten
/// times produces one fetch, and a person whose join, avatar change and presence report arrive
/// in the same second produces one fetch.
/// </para>
/// <para>
/// In memory, on purpose. Entries are requests, not history; a restart loses the pending ones
/// and the next top-up re-derives the durable tiers from the rows' own timestamps. The two
/// transient tiers -- somebody in an instance, somebody being looked at -- are re-created by the
/// next presence report or the next click, both of which happen within seconds if they still
/// matter.
/// </para>
/// </remarks>
public sealed class UserRefreshQueue
{
    private readonly Lock _gate = new();
    private readonly SortedSet<RefreshRequest> _order = new(RefreshOrder.Instance);
    private readonly Dictionary<string, RefreshRequest> _byUser = new(StringComparer.Ordinal);

    /// <summary>The refresh in flight right now, if any, so a screen can say "refreshing…" after it left the queue.</summary>
    public RefreshRequest? InProgress
    {
        get { lock (_gate) return _inProgress; }
    }

    private RefreshRequest? _inProgress;

    public int Count
    {
        get { lock (_gate) return _order.Count; }
    }

    /// <summary>
    /// Whether a profile fetched at <paramref name="lastRefreshedAt"/> is recent enough that a
    /// request of this kind can be answered without a fetch.
    /// </summary>
    /// <remarks>
    /// Pure, and separate from <see cref="Offer"/>, so the API can answer "fresh enough" with the
    /// same rule the discovery uses -- one rule, not one per caller.
    /// </remarks>
    public static bool IsFreshEnough(
        DateTimeOffset? lastRefreshedAt,
        RefreshReason reason,
        DateTimeOffset now,
        UserProfileSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (lastRefreshedAt is not { } refreshed || options.FreshEnoughFor(reason) is not { } gap)
            return false;

        return now - refreshed < gap;
    }

    /// <summary>
    /// Adds a request, promotes an existing one, or declines because the profile is fresh enough.
    /// </summary>
    /// <param name="lastRefreshedAt">
    /// When the profile was last fetched, for the fresh-enough check. Pass null to skip the check
    /// -- the durable tiers are never turned away on freshness, because their whole reason is
    /// that the profile is not fresh.
    /// </param>
    public RefreshRequestOutcome Offer(
        RefreshRequest request,
        DateTimeOffset? lastRefreshedAt,
        DateTimeOffset now,
        UserProfileSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        // Every way into the queue -- a sighting in the fact log, a screen asking, a row the
        // housekeeping found never refreshed -- comes through here, so this is the one place a
        // instance's location can be turned away before it costs a users.profile call. It is a
        // delimiter check on a location's grammar, not a shape check on a person's id (spec
        // 3.1.1): see InstanceLocationParts.LooksLikeALocation.
        if (InstanceLocationParts.LooksLikeALocation(request.UserId))
            return RefreshRequestOutcome.NotAPerson;

        if (IsFreshEnough(lastRefreshedAt, request.Reason, now, options))
            return RefreshRequestOutcome.FreshEnough;

        lock (_gate)
        {
            if (_byUser.TryGetValue(request.UserId, out var existing))
            {
                // A higher tier wins; the same tier keeps the entry it has. Re-keying an entry
                // on every repeat sighting would let one very active person hold the front of
                // their tier by staying active, which is not what "most recent first" is for.
                if ((int)request.Reason >= (int)existing.Reason)
                    return RefreshRequestOutcome.AlreadyQueued;

                _order.Remove(existing);
                _order.Add(request);
                _byUser[request.UserId] = request;

                return RefreshRequestOutcome.Promoted;
            }

            _order.Add(request);
            _byUser[request.UserId] = request;

            return RefreshRequestOutcome.Queued;
        }
    }

    /// <summary>Takes the next request, marking it as in progress until <see cref="Finish"/>.</summary>
    public RefreshRequest? TakeNext()
    {
        lock (_gate)
        {
            if (_order.Count == 0)
                return null;

            var next = _order.Min!;
            _order.Remove(next);
            _byUser.Remove(next.UserId);
            _inProgress = next;

            return next;
        }
    }

    /// <summary>The refresh that was in flight is over, however it ended.</summary>
    public void Finish(RefreshRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (ReferenceEquals(_inProgress, request))
                _inProgress = null;
        }
    }

    /// <summary>
    /// Puts a request back at the front of its tier -- for a lane that turned out to be
    /// cold-stopped, where the request was not spent and should not be lost.
    /// </summary>
    public void PutBack(RefreshRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_gate)
        {
            if (ReferenceEquals(_inProgress, request))
                _inProgress = null;

            if (_byUser.ContainsKey(request.UserId))
                return;

            _order.Add(request);
            _byUser[request.UserId] = request;
        }
    }

    /// <summary>The pending request for a user, or the one in flight, or null.</summary>
    public RefreshRequest? PendingFor(string userId)
    {
        ArgumentNullException.ThrowIfNull(userId);

        lock (_gate)
        {
            if (_byUser.TryGetValue(userId, out var pending))
                return pending;

            return _inProgress is { } current && string.Equals(current.UserId, userId, StringComparison.Ordinal)
                ? current
                : null;
        }
    }

    /// <summary>Whether anything at this tier or a higher one is waiting.</summary>
    public bool HasAnyAtOrAbove(RefreshReason reason)
    {
        lock (_gate)
            return _order.Count > 0 && (int)_order.Min!.Reason <= (int)reason;
    }

    /// <summary>How many are waiting at each tier, for the health screen.</summary>
    public IReadOnlyDictionary<RefreshReason, int> CountByReason()
    {
        lock (_gate)
        {
            var counts = new Dictionary<RefreshReason, int>();
            foreach (var entry in _order)
                counts[entry.Reason] = counts.GetValueOrDefault(entry.Reason) + 1;
            return counts;
        }
    }

    /// <summary>Everything waiting, in order. For tests and diagnostics; not for deciding anything.</summary>
    public IReadOnlyList<RefreshRequest> Snapshot()
    {
        lock (_gate)
            return [.. _order];
    }
}
