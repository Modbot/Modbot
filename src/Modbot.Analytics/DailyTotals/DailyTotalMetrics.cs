using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.DailyTotals;

/// <summary>How a metric's rows are broken down.</summary>
public enum DailyTotalDimensionKind
{
    /// <summary>One row per day, dimension <c>''</c>.</summary>
    None,

    /// <summary>
    /// One row per day per actor -- "what did each moderator do" (spec 5.8.5). Facts with no
    /// actor contribute nothing.
    /// </summary>
    Actor,

    /// <summary>
    /// One row per day per world -- "which of our worlds get used" (spec 10.1). The dimension is
    /// the world id exactly as the fact carries it, never parsed (spec 3.1.1). Facts with no
    /// world contribute nothing.
    /// </summary>
    World,
}

/// <summary>Which facts of a metric's types count. Declared here so the job stays free of metric-specific SQL.</summary>
public enum FactCondition
{
    /// <summary>Every fact of the listed types.</summary>
    Any,

    /// <summary>
    /// Only facts where somebody other than the subject caused it. VRChat records a moderator
    /// approving a join request as a <c>member.join</c> whose actor is the moderator ("User X
    /// has been added to the group by Y"), and a person joining under their own steam as one
    /// whose actor is themselves. The actor test is how the two are told apart.
    /// </summary>
    ActorIsNotSubject,
}

/// <summary>
/// A metric counted straight from facts: every fact of one of <paramref name="Types"/> contributes
/// its weight to the day it happened on.
/// </summary>
/// <param name="Name">Dotted metric name, stored verbatim in <c>modbot_daily_total.metric</c>.</param>
/// <param name="CountDistinctSubjects">
/// Count each person once per day and dimension rather than summing facts. For presence: a
/// moderator walking into a room writes one "seen here" fact per occupant, and a second
/// moderator arriving an hour later writes them all again, so summing those facts inflates
/// with the number of moderators watching. Distinct people per day does not.
/// </param>
public sealed record FactCountMetric(
    string Name,
    DailyTotalDimensionKind Dimension,
    IReadOnlyList<string> Types,
    FactCondition Condition = FactCondition.Any,
    bool CountDistinctSubjects = false);

/// <summary>
/// A running total: yesterday's value plus today's <paramref name="Plus"/> minus today's
/// <paramref name="Minus"/>.
/// </summary>
/// <param name="Plus">Metric whose daily value increases the total.</param>
/// <param name="Minus">Metric whose daily value decreases it.</param>
public sealed record CumulativeMetric(string Name, string Plus, string Minus);

/// <summary>
/// The metrics <see cref="DailyTotalsJob"/> knows how to compute, and the names reserved for the
/// counted-only path.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.4 keeps the table generic -- metric and dimension are text -- so that adding a metric is
/// an entry in this file and a rebuild, never a migration. Everything about a metric that the SQL
/// needs is declared here; <see cref="DailyTotalsJob"/> contains no metric-specific code.
/// </para>
/// </remarks>
public static class DailyTotalMetrics
{
    /// <summary>
    /// The running net of recorded joins minus leaves. <strong>Not a headcount.</strong>
    /// </summary>
    /// <remarks>
    /// Named for what it is. It was <c>members.total</c>, which claimed to be the group's member
    /// count and was not: the series starts at zero on the fact log's first day, so a group that
    /// installs Modbot with 40,000 members sees it begin at zero and climb. A number that
    /// plausibly reads as a headcount and is not one is worse than no number, because nothing
    /// about it looks wrong.
    /// </remarks>
    public const string MembersNet = "members.net";
    public const string MembersJoined = "members.joined";
    public const string MembersLeft = "members.left";
    public const string BansAdded = "bans.added";

    /// <summary>Invites sent, whoever sent them. The "My Group" page pairs it with the joins that followed.</summary>
    public const string InvitesSent = "invites.sent";

    /// <summary>People who asked to join. With <see cref="InvitesSent"/> and <see cref="MembersJoined"/>, the way in.</summary>
    public const string RequestsReceived = "requests.received";

    public const string InstancesOpened = "instances.opened";
    public const string InstancesClosed = "instances.closed";

    /// <summary>Instances opened per world -- "which of our worlds do we actually run".</summary>
    public const string WorldInstances = "worlds.instances";

    /// <summary>
    /// Distinct people seen in each world per day, from the desktop client's presence reports.
    /// </summary>
    /// <remarks>
    /// Presence facts are the retention class an operator may age out (spec 5.5); this row is
    /// what keeps "which world was popular last spring" answerable after they are gone. It counts
    /// people seen, not people present: a world nobody with the client visited that day reads as
    /// zero however full it was.
    /// </remarks>
    public const string WorldVisitors = "worlds.visitors";

    // ── What each moderator did, one metric per kind of action ─────────────────────────────
    //
    // A family rather than one `moderator.actions` total, because the "My Team" page has to say
    // *what* each person did and a single total cannot be broken down after the fact. The total
    // is the sum of the family; a metric that was only ever a sum is not worth a row.

    public const string ModeratorBans = "moderator.bans";
    public const string ModeratorUnbans = "moderator.unbans";

    /// <summary>Removed somebody from the group -- VRChat's own word for a kick from the group.</summary>
    public const string ModeratorRemovals = "moderator.removals";

    /// <summary>Kicked somebody out of a group instance. Separate from a removal; folding them blends two actions.</summary>
    public const string ModeratorInstanceKicks = "moderator.instance-kicks";
    public const string ModeratorWarns = "moderator.warns";
    public const string ModeratorInvites = "moderator.invites";

    /// <summary>Approved a join request -- see <see cref="FactCondition.ActorIsNotSubject"/> for how that is recognised.</summary>
    public const string ModeratorApprovals = "moderator.approvals";
    public const string ModeratorRejections = "moderator.rejections";
    public const string ModeratorRoleChanges = "moderator.role-changes";

    /// <summary>
    /// Discord message volume -- the metric the counted-only path exists for (spec 5.2.1).
    /// Incremented through <see cref="IDailyTotalCounter"/>; no fact is ever written for a message.
    /// </summary>
    public const string DiscordMessages = "discord.messages";

    /// <summary>
    /// The per-moderator metrics, in the order the "My Team" page lists them. Every one is
    /// broken down by actor; their sum is a moderator's total.
    /// </summary>
    public static IReadOnlyList<string> ModeratorActionKinds { get; } =
    [
        ModeratorInstanceKicks,
        ModeratorWarns,
        ModeratorBans,
        ModeratorUnbans,
        ModeratorRemovals,
        ModeratorInvites,
        ModeratorApprovals,
        ModeratorRejections,
        ModeratorRoleChanges,
    ];

    /// <summary>
    /// Metrics summed directly from facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>members.left</c> counts <see cref="FactType.MemberLeft"/> only. A kick or a ban that
    /// removes someone produces its own departure fact in the audit log, so counting those types
    /// here as well would remove the same member twice.
    /// </para>
    /// <para>
    /// VRChat's audit log attributes everything Modbot itself does to Modbot's single account
    /// (spec 5.9.1), so once Modbot performs actions the per-moderator rows will name that
    /// account rather than the person behind it. The actor that matters is then on Modbot's own
    /// fact, and lining the pair up is spec 5.9.1's enrichment problem, not this job's.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<FactCountMetric> FactCounts { get; } =
    [
        new(MembersJoined, DailyTotalDimensionKind.None, [FactType.MemberJoined]),
        new(MembersLeft, DailyTotalDimensionKind.None, [FactType.MemberLeft]),
        new(BansAdded, DailyTotalDimensionKind.None, [FactType.MemberBanned]),
        new(InvitesSent, DailyTotalDimensionKind.None, [FactType.InviteCreated]),
        new(RequestsReceived, DailyTotalDimensionKind.None, [FactType.JoinRequestCreated]),
        new(InstancesOpened, DailyTotalDimensionKind.None, [FactType.GroupInstanceCreated]),
        new(InstancesClosed, DailyTotalDimensionKind.None, [FactType.GroupInstanceClosed]),

        new(WorldInstances, DailyTotalDimensionKind.World, [FactType.GroupInstanceCreated]),
        new(WorldVisitors, DailyTotalDimensionKind.World,
            [FactType.InstanceJoined, FactType.InstancePresenceObserved],
            CountDistinctSubjects: true),

        new(ModeratorBans, DailyTotalDimensionKind.Actor, [FactType.MemberBanned]),
        new(ModeratorUnbans, DailyTotalDimensionKind.Actor, [FactType.MemberUnbanned]),
        new(ModeratorRemovals, DailyTotalDimensionKind.Actor, [FactType.MemberKicked]),
        new(ModeratorInstanceKicks, DailyTotalDimensionKind.Actor, [FactType.GroupInstanceKick]),
        new(ModeratorWarns, DailyTotalDimensionKind.Actor, [FactType.GroupInstanceWarn]),
        new(ModeratorInvites, DailyTotalDimensionKind.Actor, [FactType.InviteCreated]),
        new(ModeratorApprovals, DailyTotalDimensionKind.Actor, [FactType.MemberJoined],
            FactCondition.ActorIsNotSubject),
        new(ModeratorRejections, DailyTotalDimensionKind.Actor,
            [FactType.JoinRequestRejected, FactType.JoinRequestBlocked]),
        new(ModeratorRoleChanges, DailyTotalDimensionKind.Actor,
            [FactType.RoleGranted, FactType.RoleRevoked]),
    ];

    /// <summary>
    /// Running totals, computed from <see cref="FactCounts"/> rows rather than from facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>members.net</c> is the net of joins and leaves recorded so far, carried forward day by
    /// day. It is a count of what the fact log has seen, not an independently observed headcount:
    /// the log's first day starts from zero, and a group that installs Modbot with 40,000 members
    /// sees the series start at zero and climb.
    /// </para>
    /// <para>
    /// The group-info producer now writes an observed member count (<c>GroupInfoChanged</c>), so
    /// an anchored headcount is finally possible — but it is not this rename, and it is not
    /// obvious. The catch-up walks the audit log backwards from whatever VRChat still retains, so
    /// joins can arrive that predate the first observation; anchoring naively would count those
    /// twice and produce a number that is wrong in a way nothing about it looks wrong. Until that
    /// is worked through, the observed count is read straight from the facts where a headcount is
    /// wanted, and this series is named for what it actually measures.
    /// </para>
    /// <para>
    /// A row is written only on days something happened. The value on a day with no membership
    /// facts is the previous row's -- charts carry the last value forward rather than the table
    /// storing one row per calendar day forever.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<CumulativeMetric> Cumulative { get; } =
    [
        new(MembersNet, Plus: MembersJoined, Minus: MembersLeft),
    ];

    /// <summary>Every metric name the daily totals job owns and will delete and rewrite at will.</summary>
    public static IReadOnlySet<string> Computed { get; } =
        FactCounts.Select(m => m.Name).Concat(Cumulative.Select(m => m.Name)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every fact type that feeds a computed metric.</summary>
    public static IReadOnlyList<string> ComputedTypes { get; } =
        FactCounts.SelectMany(m => m.Types).Distinct().Order().ToList();
}
