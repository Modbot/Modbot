namespace Modbot.VRChat.Sync;

/// <summary>
/// How fast profiles are refreshed, and what counts as "recent", "old" and "fresh enough" when
/// deciding who is next.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.2.5: <c>users.read</c> runs in its own lane, exempt from the group endpoints' 2 req/s
/// ceiling because VRChat is observed to govern it separately and far more permissively. The
/// foundation spec wrote that lane at 1 req/s; the maintainer raised it to <strong>3.5 req/s</strong>
/// on 2026-09-13 (user profile sync design §5), and that is <see cref="PacingFloor"/>. Everything
/// here may be made slower and nothing may be made faster than it (spec 4.2.1).
/// </para>
/// <para>
/// The windows shape the queue (user profile sync design §3). <see cref="RecentWindow"/> says how
/// long a sighting keeps somebody near the front; <see cref="StaleAfter"/> says how old a profile
/// has to be before it is worth a request just for being old; the two "fresh enough" gaps say how
/// recently a profile must have been fetched for a new request to be answered without a request.
/// None of these is a rate, so none is clamped downward-only: a shorter window makes Modbot no
/// faster, only choosier.
/// </para>
/// </remarks>
public sealed record UserProfileSyncOptions
{
    /// <summary>
    /// The <c>users.read</c> pacing cap: 3.5 requests per second, so one request every ~286 ms.
    /// Kept in step with <c>VRChatRateLimits</c>; the limiter enforces the same number
    /// independently, and a loop that spun faster than its bucket would only wait on it.
    /// </summary>
    public static readonly TimeSpan PacingFloor = TimeSpan.FromSeconds(1 / RequestsPerSecondCap);

    /// <summary>Spec 4.2.5 as revised 2026-09-13: 3.5 req/s on the users lane.</summary>
    public const double RequestsPerSecondCap = 3.5;

    /// <summary>Time between refreshes while there is somebody to refresh.</summary>
    public TimeSpan Interval { get; init; } = PacingFloor;

    /// <summary>
    /// Time between passes while nobody is due. Each pass still looks for newly seen people, so
    /// this bounds how long a fresh join waits to be discovered when the queue is otherwise empty.
    /// </summary>
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait after a pass that failed outright -- the database, the network.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long to wait while the lane is cold-stopped. Long, because nothing will be sent until
    /// the stop lifts and a tighter loop would only produce refusals in the log (spec 4.3.1).
    /// </summary>
    public TimeSpan RateLimitedInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Per-tick jitter, as a fraction of the interval (spec 4.2.2).</summary>
    public double JitterFraction { get; init; } = 0.1;

    /// <summary>
    /// How long after somebody was last seen doing something they count as a fresh sighting and
    /// go near the front of the queue.
    /// </summary>
    public TimeSpan RecentWindow { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How old a profile has to be before it is refreshed just for being old. Six hours lets a
    /// typical group (spec 4.2.5: ~2.2 hours per full pass) refresh everyone several times a day
    /// while leaving most of the lane for people who are actually active.
    /// </summary>
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How long after Modbot last saw somebody who is <em>not</em> a current group member it goes
    /// on refreshing them on a schedule. Thirty days.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The periodic tiers -- <see cref="RefreshReason.ProfileIsOld"/> and
    /// <see cref="RefreshReason.NeverRefreshed"/> -- used to select from every row in
    /// <c>vrchat_user</c>, which is a row for everyone Modbot has ever seen anywhere. A stranger
    /// who walked through one instance a year ago was queued for a refresh forever, competing for
    /// the budget spec 4.2 accounts for. This window is what keeps them out: a current member is
    /// always refreshed, and anybody else only while their last sighting is inside it.
    /// </para>
    /// <para>
    /// Nobody becomes unreachable. The two on-demand tiers are untouched -- a sighting in an
    /// instance and a moderator opening the person in Modbot both still queue a fetch at once --
    /// so the cost of a long-gone stranger is a profile that is as old as the last time anyone
    /// cared, not a profile nobody can get.
    /// </para>
    /// </remarks>
    public TimeSpan RefreshNonMembersFor { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// A profile fetched more recently than this is "fresh enough" when somebody opens it in
    /// Modbot: the request is answered from what is stored and nothing is queued. This is what
    /// keeps a moderator clicking through a list from spending the whole lane on one person.
    /// </summary>
    public TimeSpan FreshEnoughWhenOpened { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The same gap for a sighting in an instance. Shorter, because a presence report is the
    /// strongest signal that somebody is here right now -- but not zero, because one person's
    /// join, avatar change and presence report can arrive within a second of each other.
    /// </summary>
    public TimeSpan FreshEnoughWhenSeenInInstance { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long between reads of the full user object for one person. Seven days.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The public profile is the main read and runs on the schedule above. The user object is
    /// read once when a person is first met and then about once a week, because of what it
    /// carries that the public profile does not: the join date, which never changes after the
    /// account is made; the tag list, including the trust rank, which moves on a scale of weeks;
    /// the status line, which is a line of self-description nothing acts on; and the avatar
    /// pictures and platform, which change constantly and which nothing decides anything from.
    /// Age verification -- the one field that would have forced a frequent user read -- is on
    /// both calls (research: <c>vrchat-public-profile-findings.md</c> §5).
    /// </para>
    /// <para>
    /// The cost is small enough not to matter: a 10,000-person group is about 1,430 user reads a
    /// day, roughly 0.017 req/s against the lane's 3.5.
    /// </para>
    /// </remarks>
    public TimeSpan ReadUserEvery { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How long a user whose last refresh failed is left alone before being asked about again.</summary>
    public TimeSpan RetryFailedUserAfter { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long a user VRChat answered 404 for is left alone. A week: a deleted account does not
    /// come back, and a request a day to confirm that would be the "aggressive retry" spec 4.3
    /// warns against, spent on the least likely profile to have changed.
    /// </summary>
    public TimeSpan RetryNotFoundAfter { get; init; } = TimeSpan.FromDays(7);

    /// <summary>How many new facts one pass reads while looking for people it has not seen.</summary>
    public int DiscoveryBatchSize { get; init; } = 2_000;

    /// <summary>
    /// How many fact ids behind the cursor each pass re-reads. Ids are handed out at insert and
    /// committed slightly later, so a fact can land below a cursor that has already passed it.
    /// </summary>
    public int DiscoveryOverlap { get; init; } = 500;

    /// <summary>
    /// How many people the queue is topped up with from the database, per tier, when it runs low.
    /// The queue is where the ordering lives; the database is where the people who are old or
    /// never refreshed are found.
    /// </summary>
    public int TopUpBatchSize { get; init; } = 100;

    /// <summary>How often the queue is topped up even when it is not empty, so a new arrival in the database is noticed.</summary>
    public TimeSpan TopUpInterval { get; init; } = TimeSpan.FromMinutes(1);

    /// <summary>Applies spec 4.2.1: configuration may only make the producer slower.</summary>
    public UserProfileSyncOptions Clamped()
    {
        var interval = Interval < PacingFloor ? PacingFloor : Interval;

        return this with
        {
            Interval = interval,
            IdleInterval = IdleInterval < interval ? interval : IdleInterval,
            RetryInterval = RetryInterval < PacingFloor ? PacingFloor : RetryInterval,
            RateLimitedInterval = RateLimitedInterval < PacingFloor ? PacingFloor : RateLimitedInterval,
            JitterFraction = Math.Clamp(JitterFraction, 0, 0.5),
            RecentWindow = RecentWindow < TimeSpan.Zero ? TimeSpan.Zero : RecentWindow,
            StaleAfter = StaleAfter < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : StaleAfter,
            RefreshNonMembersFor = RefreshNonMembersFor < TimeSpan.Zero ? TimeSpan.Zero : RefreshNonMembersFor,
            FreshEnoughWhenOpened = FreshEnoughWhenOpened < TimeSpan.Zero ? TimeSpan.Zero : FreshEnoughWhenOpened,
            FreshEnoughWhenSeenInInstance =
                FreshEnoughWhenSeenInInstance < TimeSpan.Zero ? TimeSpan.Zero : FreshEnoughWhenSeenInInstance,
            RetryFailedUserAfter = RetryFailedUserAfter < interval ? interval : RetryFailedUserAfter,
            ReadUserEvery = ReadUserEvery < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : ReadUserEvery,
            RetryNotFoundAfter = RetryNotFoundAfter < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : RetryNotFoundAfter,
            DiscoveryBatchSize = Math.Clamp(DiscoveryBatchSize, 1, 10_000),
            DiscoveryOverlap = Math.Max(0, DiscoveryOverlap),
            TopUpBatchSize = Math.Clamp(TopUpBatchSize, 1, 1_000),
            TopUpInterval = TopUpInterval < interval ? interval : TopUpInterval,
        };
    }

    /// <summary>
    /// How recently a profile must have been fetched for a request of this kind to be answered
    /// "fresh enough" instead of queued. Null when the kind is never turned away on freshness.
    /// </summary>
    public TimeSpan? FreshEnoughFor(RefreshReason reason) => reason switch
    {
        RefreshReason.SeenInInstance => FreshEnoughWhenSeenInInstance,
        RefreshReason.OpenedInModbot => FreshEnoughWhenOpened,
        _ => null,
    };
}
