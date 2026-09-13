using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Rollups;

/// <summary>How a metric's rows are broken down.</summary>
public enum RollupDimensionKind
{
    /// <summary>One row per day, dimension <c>''</c>.</summary>
    None,

    /// <summary>
    /// One row per day per actor -- "what did each moderator do" (spec 5.8.5). Facts with no
    /// actor contribute nothing.
    /// </summary>
    Actor,
}

/// <summary>
/// A metric counted straight from facts: every fact of one of <paramref name="Types"/> contributes
/// its weight to the day it happened on.
/// </summary>
/// <param name="Name">Dotted metric name, stored verbatim in <c>modbot_rollup_daily.metric</c>.</param>
public sealed record FactCountMetric(
    string Name,
    RollupDimensionKind Dimension,
    IReadOnlyList<FactType> Types);

/// <summary>
/// A running total: yesterday's value plus today's <paramref name="Plus"/> minus today's
/// <paramref name="Minus"/>.
/// </summary>
/// <param name="Plus">Metric whose daily value increases the total.</param>
/// <param name="Minus">Metric whose daily value decreases it.</param>
public sealed record CumulativeMetric(string Name, string Plus, string Minus);

/// <summary>
/// The metrics <see cref="RollupJob"/> knows how to compute, and the names reserved for the
/// counted-only path.
/// </summary>
/// <remarks>
/// <para>
/// Spec 5.4 keeps the table generic -- metric and dimension are text -- so that adding a metric is
/// an entry in this file and a rebuild, never a migration. Everything about a metric that the SQL
/// needs is declared here; <see cref="RollupJob"/> contains no metric-specific code.
/// </para>
/// </remarks>
public static class RollupMetrics
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
    public const string ModeratorActions = "moderator.actions";

    /// <summary>
    /// Discord message volume -- the metric the counted-only path exists for (spec 5.2.1).
    /// Incremented through <see cref="IRollupCounter"/>; no fact is ever written for a message.
    /// </summary>
    public const string DiscordMessages = "discord.messages";

    /// <summary>
    /// Metrics summed directly from facts.
    /// </summary>
    /// <remarks>
    /// <c>members.left</c> counts <see cref="FactType.MemberLeft"/> only. A kick or a ban that
    /// removes someone produces its own departure fact in the audit log, so counting those types
    /// here as well would remove the same member twice.
    /// </remarks>
    public static IReadOnlyList<FactCountMetric> FactCounts { get; } =
    [
        new(MembersJoined, RollupDimensionKind.None, [FactType.MemberJoined]),
        new(MembersLeft, RollupDimensionKind.None, [FactType.MemberLeft]),
        new(BansAdded, RollupDimensionKind.None, [FactType.MemberBanned]),

        // Everything a moderator did, attributed to whoever did it. VRChat's audit log attributes
        // Modbot's own actions to Modbot's single account (spec 5.9.1), so for those the actor
        // that matters is on Modbot's own fact, not VRChat's -- both are counted here, and the
        // deduplication of the pair is spec 5.9.1's enrichment problem, not this job's.
        new(ModeratorActions, RollupDimensionKind.Actor,
        [
            FactType.MemberBanned,
            FactType.MemberUnbanned,
            FactType.MemberKicked,
            FactType.RoleGranted,
            FactType.RoleRevoked,
        ]),
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
    /// obvious. Backfill walks the audit log backwards from whatever VRChat still retains, so
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

    /// <summary>Every metric name the rollup job owns and will delete and rewrite at will.</summary>
    public static IReadOnlySet<string> Computed { get; } =
        FactCounts.Select(m => m.Name).Concat(Cumulative.Select(m => m.Name)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every fact type that feeds a computed metric.</summary>
    public static IReadOnlyList<FactType> ComputedTypes { get; } =
        FactCounts.SelectMany(m => m.Types).Distinct().Order().ToList();
}
