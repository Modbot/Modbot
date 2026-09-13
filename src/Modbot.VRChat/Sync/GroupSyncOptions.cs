namespace Modbot.VRChat.Sync;

/// <summary>
/// How often the audit log is polled, and how much of it is read at a time.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.2 paces <c>groups.auditlog</c> at one request per 8 seconds and spec 4.2.3 protects it
/// when the global ceiling binds, because it is cheap and it is the authoritative fact source.
/// That makes 8 seconds a <em>floor on the interval</em>, not a target: polling a silent group
/// every 8 seconds spends the whole budget discovering nothing.
/// </para>
/// <para>
/// So the poll rate is adaptive between <see cref="MinInterval"/> and <see cref="MaxInterval"/>,
/// and every value here can be raised (slower) but never lowered past the pacing floor, which is
/// spec 4.2.1's rule that configuration may only ever make Modbot gentler.
/// </para>
/// </remarks>
public sealed record AuditLogSyncOptions
{
    /// <summary>
    /// Spec 4.2's cap for <c>groups.auditlog</c>: one request per 8 seconds. The adaptive poll rate
    /// is clamped to this from below; the limiter enforces it independently and would simply make
    /// a faster loop wait, but a loop that spins against a bucket it cannot drain is a bug that
    /// looks like a performance problem.
    /// </summary>
    public static readonly TimeSpan PacingFloor = TimeSpan.FromSeconds(8);

    /// <summary>The fastest the producer will poll, when entries are arriving.</summary>
    public TimeSpan MinInterval { get; init; } = PacingFloor;

    /// <summary>The slowest it will poll, once the group has been quiet for a while.</summary>
    public TimeSpan MaxInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How much the interval grows per consecutive quiet poll. Geometric, so a group that goes
    /// quiet overnight reaches the maximum in a handful of polls rather than a hundred.
    /// </summary>
    public double QuietBackoff { get; init; } = 2.0;

    /// <summary>Per-tick jitter, as a fraction of the interval (spec 4.2.2).</summary>
    public double JitterFraction { get; init; } = 0.1;

    /// <summary>Entries per request. VRChat caps this; 60 is well inside it.</summary>
    public int PageSize { get; init; } = 60;

    /// <summary>
    /// How many requests one pass may spend catching up. A pass that hits this stops without
    /// advancing its cursor and resumes on the next tick, so a long outage drains over several
    /// polls instead of one burst -- the same reasoning as spec 4.2.3's "a large sync simply takes
    /// longer".
    /// </summary>
    public int MaxPagesPerRun { get; init; } = 5;

    /// <summary>
    /// How far behind the cursor each poll starts reading again.
    /// </summary>
    /// <remarks>
    /// Spec 5.9's audit log is the authoritative moderation source, so the asymmetry is stark:
    /// re-reading an entry costs a duplicate check, and missing one loses a ban permanently. The
    /// overlap is sized for an entry that surfaces late rather than for the common case.
    /// </remarks>
    public TimeSpan Overlap { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Whether to walk back through the audit log VRChat already holds when Modbot first starts.
    /// </summary>
    /// <remarks>
    /// On by default because spec 5.1's whole argument is that recorded history cannot be
    /// retrofitted, and this is the only history that exists before Modbot was installed. It is a
    /// switch rather than a constant because it is also the one thing a fresh install does that
    /// spends real budget for minutes on end.
    /// </remarks>
    public bool CatchUp { get; init; } = true;

    /// <summary>
    /// A stop on the catch-up walk, in pages, in case VRChat's <c>hasNext</c> never goes false.
    /// </summary>
    /// <remarks>
    /// Not a judgement about how much history is worth having -- VRChat's own retention ends the
    /// walk long before this. It exists so that a paging quirk cannot turn a one-off catch-up
    /// into a permanent background load nobody notices.
    /// </remarks>
    public int MaxCatchUpPages { get; init; } = 1000;

    /// <summary>Applies spec 4.2.1: configuration may only make the producer slower.</summary>
    /// <remarks>
    /// Written as statements rather than one <c>with</c> expression on purpose. Inside a
    /// <c>with</c> every right-hand side still reads the <em>original</em> record, so a clause
    /// that clamps <see cref="MaxInterval"/> against <see cref="MinInterval"/> would compare it
    /// against the un-clamped value and silently let a configured 100 ms through.
    /// </remarks>
    public AuditLogSyncOptions Clamped()
    {
        var min = MinInterval < PacingFloor ? PacingFloor : MinInterval;

        return this with
        {
            MinInterval = min,
            MaxInterval = MaxInterval < min ? min : MaxInterval,
            PageSize = Math.Clamp(PageSize, 1, 100),
            MaxPagesPerRun = Math.Max(1, MaxPagesPerRun),
            QuietBackoff = Math.Max(1.0, QuietBackoff),
            JitterFraction = Math.Clamp(JitterFraction, 0, 0.5),
            Overlap = Overlap < TimeSpan.Zero ? TimeSpan.Zero : Overlap,
        };
    }
}

/// <summary>
/// How often the group's own metadata is re-read.
/// </summary>
/// <remarks>
/// <para>
/// One request, not two: <c>GetGroup(includeRoles: true)</c> returns the counts, the name, the
/// description and every role definition together, so the role list costs nothing beyond the poll
/// that was happening anyway. Both would have landed in the same <c>groups.read</c> budget
/// (spec 4.2), and splitting them would have doubled its use to learn the same thing.
/// </para>
/// <para>
/// Five minutes rather than spec 4.2's 1-per-10-seconds pacing cap, because that cap is a
/// <em>rate limit</em> on the class and not a statement about how often group metadata is worth
/// re-reading. A group's name changes a few times a year.
/// </para>
/// </remarks>
public sealed record GroupInfoSyncOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Spec 4.2's <c>groups.read</c> pacing cap: one request per 10 seconds.</summary>
    public static readonly TimeSpan PacingFloor = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait after a failure before trying again.</summary>
    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long to wait while a bucket is cold-stopped. Long, because nothing will be sent until
    /// the stop lifts and a tighter loop would only produce log noise (spec 4.3.1).
    /// </summary>
    public TimeSpan RateLimitedInterval { get; init; } = TimeSpan.FromMinutes(15);

    public double JitterFraction { get; init; } = 0.1;

    public GroupInfoSyncOptions Clamped() => this with
    {
        Interval = Interval < PacingFloor ? PacingFloor : Interval,
        RetryInterval = RetryInterval < PacingFloor ? PacingFloor : RetryInterval,
        RateLimitedInterval = RateLimitedInterval < PacingFloor ? PacingFloor : RateLimitedInterval,
        JitterFraction = Math.Clamp(JitterFraction, 0, 0.5),
    };
}
