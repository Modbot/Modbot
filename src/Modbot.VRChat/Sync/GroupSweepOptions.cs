namespace Modbot.VRChat.Sync;

/// <summary>
/// How the member list is swept: how fast the pages go by, and how long the sweep rests between
/// passes.
/// </summary>
/// <remarks>
/// <para>
/// A sweep is every page of <c>GET /groups/{id}/members</c> from offset 0 until an empty page
/// (audit-log research §7.1: the endpoint has no offset cap, so plain offset paging is correct).
/// Spec 4.2 paces <c>groups.members</c> at one request per 2 seconds, and the limiter enforces
/// that independently; <see cref="PacingFloor"/> is the same number, so a page delay configured
/// faster than it is raised rather than left to spin against a bucket it cannot drain.
/// </para>
/// <para>
/// Foundation §4.3.4's provisional table records the previous implementation at 1,500 ms between
/// member pages. That is faster than §4.2's cap, and §4.3.4 says §4.2 wins where they disagree,
/// so 2 seconds is the floor and the default. A typical 5,000-member group is fifty-odd pages,
/// about 100 seconds per pass; the rest between passes is what keeps the average well under the
/// class budget and leaves the room spec 4.2 reserves for a moderator's own requests.
/// </para>
/// <para>
/// Every value here can be raised (slower) and none can be lowered past the floor, which is spec
/// 4.2.1's rule that configuration may only make Modbot gentler. The rest is the one field that
/// rule does not literally cover -- a shorter rest means more passes -- and it is floored at the
/// pacing cap for the same honest reason spec 4.2.1.2 gives for the audit log's maximum interval.
/// </para>
/// </remarks>
public sealed record GroupMemberSyncOptions
{
    /// <summary>Spec 4.2's cap for <c>groups.members</c>: one request per 2 seconds.</summary>
    public static readonly TimeSpan PacingFloor = TimeSpan.FromSeconds(2);

    /// <summary>Time between one page and the next while a sweep is running.</summary>
    public TimeSpan PageDelay { get; init; } = PacingFloor;

    /// <summary>Time between the end of one full sweep and the start of the next.</summary>
    public TimeSpan RestBetweenSweeps { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>How long to wait after a page that failed -- VRChat, the network, the database.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long to wait while the bucket is cold-stopped. Long, because nothing will be sent until
    /// the stop lifts and a tighter loop would only produce refusals in the log (spec 4.3.1).
    /// </summary>
    public TimeSpan RateLimitedInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Per-tick jitter, as a fraction of the delay (spec 4.2.2).</summary>
    public double JitterFraction { get; init; } = 0.1;

    /// <summary>Members per request. VRChat caps this at 100; the research walked the live list at exactly that.</summary>
    public int PageSize { get; init; } = 100;

    /// <summary>
    /// How many entries each page re-reads from the end of the previous one.
    /// </summary>
    /// <remarks>
    /// Offset paging over a live list is not stable: a member leaving while a sweep is between
    /// two pages shifts everyone after them one slot earlier, and the person who was first on the
    /// next page is now last on the page already read -- never listed, and wrongly marked as
    /// gone. Stepping the offset by <c>PageSize - PageOverlap</c> means up to this many
    /// departures between two pages cost nothing. Duplicates are upserted and cost nothing either.
    /// </remarks>
    public int PageOverlap { get; init; } = 5;

    /// <summary>
    /// How long after a change was noticed the audit log must have polled before the sweep
    /// records the change itself.
    /// </summary>
    /// <remarks>
    /// The audit log is authoritative and exact; the sweep is an inference with a window (spec
    /// 5.3). If the sweep wrote first, the audit log would write the same event again a poll
    /// later -- it deduplicates on VRChat's entry id, which an inferred fact does not have -- and
    /// every join would count twice in the daily totals. So the sweep waits until the audit log
    /// has polled at least this long after the moment the change was noticed, then records only
    /// what the audit log did not (member and ban sync design §4).
    /// </remarks>
    public TimeSpan WaitForAuditLog { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// If the audit log has not polled for this long, stop waiting for it and record inferred
    /// changes straight away. An audit log that is switched off, or whose bucket has given up,
    /// must not hold the sweep's facts forever.
    /// </summary>
    public TimeSpan AuditLogSilentAfter { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Applies spec 4.2.1: configuration may only make the producer slower.</summary>
    public GroupMemberSyncOptions Clamped() => this with
    {
        PageDelay = PageDelay < PacingFloor ? PacingFloor : PageDelay,
        RestBetweenSweeps = RestBetweenSweeps < PacingFloor ? PacingFloor : RestBetweenSweeps,
        RetryInterval = RetryInterval < PacingFloor ? PacingFloor : RetryInterval,
        RateLimitedInterval = RateLimitedInterval < PacingFloor ? PacingFloor : RateLimitedInterval,
        JitterFraction = Math.Clamp(JitterFraction, 0, 0.5),
        PageSize = Math.Clamp(PageSize, 1, 100),
        PageOverlap = Math.Clamp(PageOverlap, 0, Math.Max(0, Math.Clamp(PageSize, 1, 100) - 1)),
        WaitForAuditLog = WaitForAuditLog < TimeSpan.Zero ? TimeSpan.Zero : WaitForAuditLog,
        AuditLogSilentAfter = AuditLogSilentAfter < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : AuditLogSilentAfter,
    };
}

/// <summary>
/// How the ban list is swept. The same shape as <see cref="GroupMemberSyncOptions"/> with slower
/// defaults, because a ban list changes far less often than a member list.
/// </summary>
/// <remarks>
/// Spec 4.2 caps <c>groups.bans</c> at one request per 2 seconds too, so that is the floor. The
/// default page delay is the 3,500 ms the previous implementation ran in production (foundation
/// §4.3.4), which is gentler than the cap and is what the standing rule asks for on an endpoint
/// with little history behind it. Half an hour between passes: a ban appearing on the list is
/// almost always in the audit log within a poll, so the sweep's job here is the bans issued
/// before Modbot was installed and the ones the audit log missed.
/// </remarks>
public sealed record GroupBanSyncOptions
{
    /// <summary>Spec 4.2's cap for <c>groups.bans</c>: one request per 2 seconds.</summary>
    public static readonly TimeSpan PacingFloor = TimeSpan.FromSeconds(2);

    public TimeSpan PageDelay { get; init; } = TimeSpan.FromMilliseconds(3500);

    public TimeSpan RestBetweenSweeps { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan RateLimitedInterval { get; init; } = TimeSpan.FromMinutes(15);

    public double JitterFraction { get; init; } = 0.1;

    public int PageSize { get; init; } = 100;

    /// <inheritdoc cref="GroupMemberSyncOptions.PageOverlap"/>
    public int PageOverlap { get; init; } = 5;

    /// <inheritdoc cref="GroupMemberSyncOptions.WaitForAuditLog"/>
    public TimeSpan WaitForAuditLog { get; init; } = TimeSpan.FromMinutes(2);

    /// <inheritdoc cref="GroupMemberSyncOptions.AuditLogSilentAfter"/>
    public TimeSpan AuditLogSilentAfter { get; init; } = TimeSpan.FromHours(1);

    public GroupBanSyncOptions Clamped() => this with
    {
        PageDelay = PageDelay < PacingFloor ? PacingFloor : PageDelay,
        RestBetweenSweeps = RestBetweenSweeps < PacingFloor ? PacingFloor : RestBetweenSweeps,
        RetryInterval = RetryInterval < PacingFloor ? PacingFloor : RetryInterval,
        RateLimitedInterval = RateLimitedInterval < PacingFloor ? PacingFloor : RateLimitedInterval,
        JitterFraction = Math.Clamp(JitterFraction, 0, 0.5),
        PageSize = Math.Clamp(PageSize, 1, 100),
        PageOverlap = Math.Clamp(PageOverlap, 0, Math.Max(0, Math.Clamp(PageSize, 1, 100) - 1)),
        WaitForAuditLog = WaitForAuditLog < TimeSpan.Zero ? TimeSpan.Zero : WaitForAuditLog,
        AuditLogSilentAfter = AuditLogSilentAfter < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : AuditLogSilentAfter,
    };
}
