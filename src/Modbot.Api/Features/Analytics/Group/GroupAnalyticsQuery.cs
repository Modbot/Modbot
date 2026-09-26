using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Activity;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Analytics.Group;

/// <summary>
/// "Is the community growing or shrinking, and what changed?" (spec 10.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two sources, deliberately kept apart.</strong> Joins, leaves, invites and requests per
/// day come from the daily totals, which are kept forever. The headcount, the role changes, the
/// tenure buckets and the invite follow-up come from facts, which an operator may have configured
/// a window on. Each panel says which, and the response reports both ranges.
/// </para>
/// <para>
/// <strong>What this page cannot yet say.</strong> There is no member list: Modbot has not synced
/// one, so it does not know how many people hold each role, or when anybody who joined before
/// recording began became a member. The role panel therefore shows the roles VRChat lists and the
/// grants and revokes seen, and the tenure panel covers only members whose join was recorded.
/// Both say so in their labels rather than presenting a partial number as the whole.
/// </para>
/// </remarks>
public sealed class GroupAnalyticsQuery(ModbotContext db)
{
    /// <summary>How long after an invite a join still counts as having followed from it.</summary>
    public const int InviteFollowUpDays = 7;

    /// <summary>The tenure buckets, in order. Plain words; the days are inclusive at the low end.</summary>
    private static readonly (string Label, int MinDays, int? MaxDays)[] TenureBuckets =
    [
        ("Under a week", 0, 7),
        ("1 to 4 weeks", 7, 28),
        ("1 to 3 months", 28, 91),
        ("3 to 12 months", 91, 365),
        ("Over a year", 365, null),
    ];

    private readonly AnalyticsSql _sql = new(db);

    public async Task<GroupAnalytics> RunAsync(
        DateOnly from,
        DateOnly to,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var totals = await _sql.DailyTotalsAsync(from, to,
        [
            DailyTotalMetrics.MembersJoined,
            DailyTotalMetrics.MembersLeft,
            DailyTotalMetrics.MembersNet,
            DailyTotalMetrics.InvitesSent,
            DailyTotalMetrics.RequestsReceived,
            DailyTotalMetrics.ModeratorApprovals,
            DailyTotalMetrics.ModeratorRejections,
        ], ct);

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        var invites = new InviteFunnel(
            totals.Total(DailyTotalMetrics.InvitesSent),
            await JoinedAfterInviteAsync(from, to, ct),
            InviteFollowUpDays,
            totals.Total(DailyTotalMetrics.RequestsReceived),
            totals.Where(r => r.Metric == DailyTotalMetrics.ModeratorApprovals).Sum(r => r.Value),
            totals.Where(r => r.Metric == DailyTotalMetrics.ModeratorRejections).Sum(r => r.Value));

        var (tenure, withKnownTenure) = await TenureAsync(now, ct);

        return new GroupAnalytics(
            from,
            to,
            MissingDays.Today(to, now),
            await MemberCountAsync(from, to, ct),
            totals.Series(DailyTotalMetrics.MembersJoined),
            totals.Series(DailyTotalMetrics.MembersLeft),
            totals.Series(DailyTotalMetrics.MembersNet),
            totals.Series(DailyTotalMetrics.InvitesSent),
            totals.Series(DailyTotalMetrics.RequestsReceived),
            await RolesAsync(settings, from, to, ct),
            settings?.GroupInfoPolledAt,
            tenure,
            withKnownTenure,
            invites,
            await PeaksAsync(from, to, ct),
            await new MissingDaysQuery(db).AuditLogAsync(from, to, ct),
            await AnalyticsCoverageQuery.RunAsync(db, ct),
            now);
    }

    /// <summary>
    /// The highest member count and the highest online count inside the window, each with the
    /// moment it was read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// From <c>group_member_count</c>, the readings the member count chart draws — not from the
    /// daily totals, which hold the last reading of a day and would put a peak at midnight. A tie
    /// goes to the earliest reading, so the same window always names the same moment.
    /// </para>
    /// <para>
    /// Days with a reading come back beside the peaks. The sync reads every five minutes, so a
    /// window Modbot was running through has every day; a window it was not — the deployment is
    /// younger than the range, or an operator's presence retention window has deleted the older
    /// readings — has gaps, and a peak drawn across them is the highest Modbot saw rather than the
    /// highest there was.
    /// </para>
    /// </remarks>
    private async Task<MemberCountPeaks> PeaksAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string Sql = """
            SELECT COUNT(*)::int AS readings,
                   COUNT(DISTINCT (c.counted_at AT TIME ZONE 'UTC')::date)::int AS days,
                   MAX(c.member_count)::int,
                   (array_agg(c.counted_at ORDER BY c.member_count DESC, c.counted_at))[1],
                   MAX(c.online_member_count)::int,
                   (array_agg(c.counted_at ORDER BY c.online_member_count DESC, c.counted_at))[1]
            FROM group_member_count c
            WHERE c.counted_at >= @from AND c.counted_at < @to
            """;

        var windowDays = to.DayNumber - from.DayNumber + 1;

        var rows = await _sql.ReadAsync(
            Sql,
            r => r.GetInt32(0) == 0
                ? MemberCountPeaks.Empty(windowDays)
                : new MemberCountPeaks(
                    Peak(r, 2, 3),
                    Peak(r, 4, 5),
                    new MemberCountCoverage(windowDays, r.GetInt32(1), r.GetInt32(0))),
            ct,
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        return rows.Count > 0 ? rows[0] : MemberCountPeaks.Empty(windowDays);
    }

    /// <summary>A peak and its moment, or null where the column is null or the count never rose above nought.</summary>
    private static PeakCount? Peak(System.Data.Common.DbDataReader reader, int value, int at)
        => reader.IsDBNull(value) || reader.IsDBNull(at) || reader.GetInt32(value) <= 0
            ? null
            : new PeakCount(reader.GetInt32(value), AnalyticsSql.InstantOf(reader, at));

    /// <summary>
    /// The group's headcount as VRChat reported it, one point per day it was observed changing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read out of <c>GroupInfoChanged</c> facts rather than from a daily total because there is no
    /// daily total for it: the group-info producer writes a baseline fact carrying the whole snapshot
    /// and thereafter writes only what changed, and neither shape is something the generic
    /// fact-count daily total can sum.
    /// </para>
    /// <para>
    /// <c>DISTINCT ON</c> takes the last observation of each UTC day. A day on which nothing
    /// changed produces no row at all — the producer does not write a fact when the poll saw what
    /// it left behind — so the series is sparse by design and the chart carries the last value
    /// forward.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<DayValue>> MemberCountAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string Sql = """
            SELECT d.day, d.value
            FROM (
                SELECT DISTINCT ON ((e.occurred_at AT TIME ZONE 'UTC')::date)
                       (e.occurred_at AT TIME ZONE 'UTC')::date AS day,
                       COALESCE(
                           e.data->'changed'->'MemberCount'->>'new',
                           e.data->'baseline'->>'MemberCount')::numeric AS value
                FROM modbot_event e
                WHERE e.type = @type
                  AND e.occurred_at >= @from
                  AND e.occurred_at < @to
                  AND COALESCE(
                        e.data->'changed'->'MemberCount'->>'new',
                        e.data->'baseline'->>'MemberCount') IS NOT NULL
                ORDER BY (e.occurred_at AT TIME ZONE 'UTC')::date, e.occurred_at DESC, e.id DESC
            ) d
            ORDER BY d.day
            """;

        return await _sql.ReadAsync(
            Sql,
            r => new DayValue(AnalyticsSql.DayOf(r, 0), r.GetDecimal(1)),
            ct,
            ("type", FactType.GroupInfoChanged),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));
    }

    /// <summary>
    /// The roles VRChat lists for the group, with the grants and revokes recorded in the window.
    /// </summary>
    /// <remarks>
    /// The list comes from the group-info sync's last snapshot and the counts from role facts.
    /// A role that was granted in the window but is no longer in the snapshot (deleted since) is
    /// still reported, under the name the fact recorded, so the count is not silently dropped.
    /// </remarks>
    private async Task<IReadOnlyList<RoleSummary>> RolesAsync(
        Core.Data.Entities.Settings? settings,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        const string Sql = """
            SELECT e.data->>'roleId' AS role_id,
                   MAX(e.data->>'roleName') AS role_name,
                   COUNT(*) FILTER (WHERE e.type = @granted)::numeric AS granted,
                   COUNT(*) FILTER (WHERE e.type = @revoked)::numeric AS revoked
            FROM modbot_event e
            WHERE e.type IN (@granted, @revoked)
              AND e.occurred_at >= @from AND e.occurred_at < @to
              AND e.data->>'roleId' IS NOT NULL
            GROUP BY 1
            """;

        var changes = await _sql.ReadAsync(
            Sql,
            r => (RoleId: r.GetString(0), Name: r.IsDBNull(1) ? null : r.GetString(1), Granted: r.GetDecimal(2), Revoked: r.GetDecimal(3)),
            ct,
            ("granted", FactType.RoleGranted),
            ("revoked", FactType.RoleRevoked),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        var byId = changes.ToDictionary(c => c.RoleId, StringComparer.Ordinal);
        var snapshot = GroupInfoSnapshot.Parse(settings?.GroupInfoSnapshot);

        var roles = new List<RoleSummary>();

        foreach (var role in (snapshot?.Roles ?? []).OrderBy(r => r.Order))
        {
            byId.Remove(role.Id, out var change);
            roles.Add(new RoleSummary(
                role.Id,
                role.Name,
                ModerationRoles.IsModerationRole(role),
                role.IsAddedOnJoin,
                role.IsSelfAssignable,
                change.Granted,
                change.Revoked));
        }

        foreach (var orphan in byId.Values.OrderBy(c => c.RoleId, StringComparer.Ordinal))
            roles.Add(new RoleSummary(orphan.RoleId, orphan.Name, false, false, false, orphan.Granted, orphan.Revoked));

        return roles;
    }

    /// <summary>
    /// How long current members have been members — for the members whose join is on record.
    /// </summary>
    /// <remarks>
    /// A current member is somebody whose most recent join is later than any leave, removal or
    /// ban recorded for them. Over the whole log rather than the window, because tenure is a
    /// "now" question: the window says how far back to chart, not who counts as a member today.
    /// </remarks>
    private async Task<(IReadOnlyList<TenureBucket> Buckets, int Members)> TenureAsync(
        DateTimeOffset now,
        CancellationToken ct)
    {
        const string Sql = """
            SELECT m.joined
            FROM (
                SELECT e.subject_id,
                       MAX(e.occurred_at) FILTER (WHERE e.type = @join) AS joined,
                       MAX(e.occurred_at) FILTER (WHERE e.type <> @join) AS gone
                FROM modbot_event e
                WHERE e.type = ANY(@types) AND e.subject_platform = @vrchat
                GROUP BY e.subject_id
            ) m
            WHERE m.joined IS NOT NULL AND (m.gone IS NULL OR m.gone < m.joined)
            """;

        var joins = await _sql.ReadAsync(
            Sql,
            r => AnalyticsSql.InstantOf(r, 0),
            ct,
            ("join", FactType.MemberJoined),
            ("types", new[] { FactType.MemberJoined, FactType.MemberLeft, FactType.MemberKicked, FactType.MemberBanned }),
            ("vrchat", (short)FactPlatform.VRChat));

        var buckets = TenureBuckets
            .Select(b => new TenureBucket(
                b.Label,
                b.MinDays,
                b.MaxDays,
                joins.Count(j =>
                {
                    var days = (now - j).TotalDays;
                    return days >= b.MinDays && (b.MaxDays is null || days < b.MaxDays);
                })))
            .ToList();

        return (buckets, joins.Count);
    }

    /// <summary>
    /// Invites sent in the window that the invited person followed by joining within a week.
    /// </summary>
    private async Task<int> JoinedAfterInviteAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string Sql = """
            SELECT COUNT(*)::int
            FROM modbot_event i
            WHERE i.type = @invite
              AND i.occurred_at >= @from AND i.occurred_at < @to
              AND EXISTS (
                  SELECT 1 FROM modbot_event j
                  WHERE j.type = @join
                    AND j.subject_platform = i.subject_platform
                    AND j.subject_id = i.subject_id
                    AND j.occurred_at >= i.occurred_at
                    AND j.occurred_at < i.occurred_at + @followUp)
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => r.GetInt32(0),
            ct,
            ("invite", FactType.InviteCreated),
            ("join", FactType.MemberJoined),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)),
            ("followUp", TimeSpan.FromDays(InviteFollowUpDays)));

        return rows.Count > 0 ? rows[0] : 0;
    }
}
