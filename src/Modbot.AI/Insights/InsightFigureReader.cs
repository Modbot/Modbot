using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.AI.Insights;

/// <summary>
/// Gathers the figures for one insight from Modbot's own tables (AI insights design §1).
/// </summary>
/// <remarks>
/// <para>
/// Mostly daily totals, which are kept forever and are already per UTC day, so an insight and the
/// analytics page beside it count the same things the same way. The rest comes from the few tables
/// that hold what daily totals do not: the headcount VRChat reported, instances, reviews and case files.
/// </para>
/// <para>
/// Every figure is a count over the whole group. A query here that selected a person's id or display
/// name would be the start of a report about a person, which insights never are (M8 §6).
/// </para>
/// </remarks>
public sealed class InsightFigureReader(ModbotContext db)
{
    /// <summary>How many entries a ranked list holds.</summary>
    public const int ListLength = 5;

    private static readonly (string Metric, string Name)[] ModeratorActions =
    [
        (DailyTotalMetrics.ModeratorInstanceKicks, "Kicks from an instance"),
        (DailyTotalMetrics.ModeratorWarns, "Warnings"),
        (DailyTotalMetrics.ModeratorBans, "Bans"),
        (DailyTotalMetrics.ModeratorUnbans, "Unbans"),
        (DailyTotalMetrics.ModeratorRemovals, "Removals from the group"),
        (DailyTotalMetrics.ModeratorInvites, "Invites sent by moderators"),
        (DailyTotalMetrics.ModeratorApprovals, "Join requests approved"),
        (DailyTotalMetrics.ModeratorRejections, "Join requests rejected"),
        (DailyTotalMetrics.ModeratorRoleChanges, "Role changes"),
    ];

    public async Task<InsightFigures> ReadAsync(string kind, InsightPeriod period, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(period);

        var (figures, lists) = kind switch
        {
            InsightKinds.Group => await GroupAsync(period, ct),
            InsightKinds.Team => await TeamAsync(period, ct),
            InsightKinds.Instances => await InstancesAsync(period, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of insight."),
        };

        return new InsightFigures(
            kind, period.FirstDay, period.LastDay, period.BeforeFirstDay, period.BeforeLastDay, figures, lists);
    }

    private async Task<(List<InsightFigure>, List<InsightList>)> GroupAsync(InsightPeriod period, CancellationToken ct)
    {
        string[] metrics =
        [
            DailyTotalMetrics.MembersJoined,
            DailyTotalMetrics.MembersLeft,
            DailyTotalMetrics.InvitesSent,
            DailyTotalMetrics.RequestsReceived,
            DailyTotalMetrics.ModeratorApprovals,
            DailyTotalMetrics.ModeratorRejections,
            DailyTotalMetrics.BansAdded,
            DailyTotalMetrics.InstancesOpened,
            DailyTotalMetrics.DiscordMessages,
        ];

        var now = await TotalsAsync(period.FirstDay, period.LastDay, metrics, ct);
        var before = await TotalsAsync(period.BeforeFirstDay, period.BeforeLastDay, metrics, ct);

        // Zero messages and "Discord is not counted here" would read the same as a number. Only a
        // deployment that has ever counted a message gets the figure at all.
        var countsDiscord = await db.DailyTotals.AnyAsync(r => r.Metric == DailyTotalMetrics.DiscordMessages, ct);

        var figures = new List<InsightFigure>
        {
            new("Members at the end, as VRChat reported",
                await MembersAtAsync(period.LastDay, ct),
                await MembersAtAsync(period.BeforeLastDay, ct)),
            Pair("Joined", DailyTotalMetrics.MembersJoined, now, before),
            Pair("Left", DailyTotalMetrics.MembersLeft, now, before),
            Pair("Invites sent", DailyTotalMetrics.InvitesSent, now, before),
            Pair("Join requests", DailyTotalMetrics.RequestsReceived, now, before),
            Pair("Join requests approved", DailyTotalMetrics.ModeratorApprovals, now, before),
            Pair("Join requests rejected", DailyTotalMetrics.ModeratorRejections, now, before),
            Pair("Bans", DailyTotalMetrics.BansAdded, now, before),
            Pair("Instances opened", DailyTotalMetrics.InstancesOpened, now, before),
        };

        if (countsDiscord)
            figures.Add(Pair("Discord messages", DailyTotalMetrics.DiscordMessages, now, before));

        List<InsightList> lists =
        [
            new("Busiest worlds, by visitors (each person counted once a day)",
                await WorldsAsync(DailyTotalMetrics.WorldVisitors, period, ct)),
        ];

        return (figures, lists);
    }

    private async Task<(List<InsightFigure>, List<InsightList>)> TeamAsync(InsightPeriod period, CancellationToken ct)
    {
        var metrics = ModeratorActions.Select(a => a.Metric).ToArray();

        var now = await TotalsAsync(period.FirstDay, period.LastDay, metrics, ct);
        var before = await TotalsAsync(period.BeforeFirstDay, period.BeforeLastDay, metrics, ct);

        var figures = new List<InsightFigure>
        {
            new("All moderator actions", now.Values.Sum(), before.Values.Sum()),
            new("Moderators who took any action",
                await ModeratorsActiveAsync(period.FirstDay, period.LastDay, metrics, ct),
                await ModeratorsActiveAsync(period.BeforeFirstDay, period.BeforeLastDay, metrics, ct)),
        };

        figures.AddRange(ModeratorActions.Select(a => Pair(a.Name, a.Metric, now, before)));

        var (from, to) = (Start(period.FirstDay), End(period.LastDay));
        var (beforeFrom, beforeTo) = (Start(period.BeforeFirstDay), End(period.BeforeLastDay));

        figures.Add(new("Reviews opened",
            await db.Reviews.CountAsync(r => r.OpenedAt >= from && r.OpenedAt < to, ct),
            await db.Reviews.CountAsync(r => r.OpenedAt >= beforeFrom && r.OpenedAt < beforeTo, ct)));

        figures.Add(new("Ban write-ups",
            await db.CaseFiles.CountAsync(c => c.CreatedAt >= from && c.CreatedAt < to, ct),
            await db.CaseFiles.CountAsync(c => c.CreatedAt >= beforeFrom && c.CreatedAt < beforeTo, ct)));

        return (figures, []);
    }

    private async Task<(List<InsightFigure>, List<InsightList>)> InstancesAsync(InsightPeriod period, CancellationToken ct)
    {
        string[] metrics = [DailyTotalMetrics.InstancesOpened];

        var now = await TotalsAsync(period.FirstDay, period.LastDay, metrics, ct);
        var before = await TotalsAsync(period.BeforeFirstDay, period.BeforeLastDay, metrics, ct);

        var instances = await InstancesInAsync(period.FirstDay, period.LastDay, ct);
        var instancesBefore = await InstancesInAsync(period.BeforeFirstDay, period.BeforeLastDay, ct);

        var figures = new List<InsightFigure>
        {
            Pair("Instances opened", DailyTotalMetrics.InstancesOpened, now, before),
            new("Typical minutes an instance stayed open", MedianMinutes(instances), MedianMinutes(instancesBefore)),
            new("Most people in one instance", MostPeople(instances), MostPeople(instancesBefore)),
        };

        var names = await WorldNamesAsync(instances.Select(r => r.WorldId), ct);

        var busiest = instances
            .Where(r => r.PeakUserCount is > 0)
            .OrderByDescending(r => r.PeakUserCount)
            .ThenBy(r => r.OpenedAt)
            .Take(ListLength)
            .Select(r => new InsightListItem(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{names.GetValueOrDefault(r.WorldId) ?? r.WorldId}, opened {r.OpenedAt.UtcDateTime:dddd d MMMM HH:mm} UTC"),
                r.PeakUserCount!.Value))
            .ToList();

        List<InsightList> lists =
        [
            new("Busiest instances, by most people at once", busiest),
            new("Worlds, by instances opened", await WorldsAsync(DailyTotalMetrics.WorldInstances, period, ct)),
        ];

        return (figures, lists);
    }

    private static InsightFigure Pair(
        string name, string metric, IReadOnlyDictionary<string, decimal> now, IReadOnlyDictionary<string, decimal> before)
        => new(name, now.GetValueOrDefault(metric), before.GetValueOrDefault(metric));

    /// <summary>Each metric summed over the days and over every dimension.</summary>
    private async Task<IReadOnlyDictionary<string, decimal>> TotalsAsync(
        DateOnly first, DateOnly last, string[] metrics, CancellationToken ct)
    {
        var rows = await db.DailyTotals.AsNoTracking()
            .Where(r => r.Day >= first && r.Day <= last && metrics.Contains(r.Metric))
            .GroupBy(r => r.Metric)
            .Select(g => new { Metric = g.Key, Value = g.Sum(r => r.Value) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Metric, r => r.Value, StringComparer.Ordinal);
    }

    /// <summary>How many different people the moderator action metrics name. A count, never who.</summary>
    private async Task<decimal> ModeratorsActiveAsync(DateOnly first, DateOnly last, string[] metrics, CancellationToken ct)
        => await db.DailyTotals.AsNoTracking()
            .Where(r => r.Day >= first && r.Day <= last && metrics.Contains(r.Metric) && r.Value > 0 && r.Dimension != "")
            .Select(r => r.Dimension)
            .Distinct()
            .CountAsync(ct);

    /// <summary>
    /// The last headcount VRChat reported on or before the end of <paramref name="day"/>, from the
    /// group-info facts -- the same source as the My Group page's member count.
    /// </summary>
    private async Task<decimal?> MembersAtAsync(DateOnly day, CancellationToken ct)
    {
        var end = End(day);
        var type = FactType.GroupInfoChanged;

        var values = await db.Database.SqlQuery<decimal?>($"""
            SELECT COALESCE(
                       e.data->'changed'->'MemberCount'->>'new',
                       e.data->'baseline'->>'MemberCount')::numeric AS "Value"
            FROM modbot_event e
            WHERE e.type = {type}
              AND e.occurred_at < {end}
              AND COALESCE(
                    e.data->'changed'->'MemberCount'->>'new',
                    e.data->'baseline'->>'MemberCount') IS NOT NULL
            ORDER BY e.occurred_at DESC, e.id DESC
            LIMIT 1
            """).ToListAsync(ct);

        return values.Count > 0 ? values[0] : null;
    }

    private async Task<List<InsightListItem>> WorldsAsync(string metric, InsightPeriod period, CancellationToken ct)
    {
        var top = await db.DailyTotals.AsNoTracking()
            .Where(r => r.Day >= period.FirstDay && r.Day <= period.LastDay && r.Metric == metric && r.Dimension != "")
            .GroupBy(r => r.Dimension)
            .Select(g => new { WorldId = g.Key, Value = g.Sum(r => r.Value) })
            .Where(g => g.Value > 0)
            .OrderByDescending(g => g.Value)
            .ThenBy(g => g.WorldId)
            .Take(ListLength)
            .ToListAsync(ct);

        var names = await WorldNamesAsync(top.Select(t => t.WorldId), ct);

        return top.Select(t => new InsightListItem(names.GetValueOrDefault(t.WorldId) ?? t.WorldId, t.Value)).ToList();
    }

    private async Task<Dictionary<string, string?>> WorldNamesAsync(IEnumerable<string> worldIds, CancellationToken ct)
    {
        var ids = worldIds.Distinct(StringComparer.Ordinal).ToList();

        return await db.VRChatWorlds.AsNoTracking()
            .Where(w => ids.Contains(w.WorldId))
            .ToDictionaryAsync(w => w.WorldId, w => w.Name, StringComparer.Ordinal, ct);
    }

    private sealed record InstanceRecord(string WorldId, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt, int? PeakUserCount);

    private async Task<List<InstanceRecord>> InstancesInAsync(DateOnly first, DateOnly last, CancellationToken ct)
    {
        var (from, to) = (Start(first), End(last));

        return await db.VRChatInstances.AsNoTracking()
            .Where(i => i.OpenedAt >= from && i.OpenedAt < to)
            .Select(i => new InstanceRecord(i.WorldId, i.OpenedAt, i.ClosedAt, i.PeakUserCount))
            .ToListAsync(ct);
    }

    /// <summary>The middle open time of the instances that have closed, in whole minutes. Null with none closed.</summary>
    private static decimal? MedianMinutes(List<InstanceRecord> instances)
    {
        var minutes = instances
            .Where(r => r.ClosedAt is not null)
            .Select(r => (decimal)(r.ClosedAt!.Value - r.OpenedAt).TotalMinutes)
            .Order()
            .ToList();

        if (minutes.Count == 0)
            return null;

        var middle = minutes.Count / 2;
        var median = minutes.Count % 2 == 1 ? minutes[middle] : (minutes[middle - 1] + minutes[middle]) / 2;
        return Math.Round(median, 0);
    }

    private static decimal? MostPeople(List<InstanceRecord> instances) => instances.Max(r => r.PeakUserCount);

    private static DateTimeOffset Start(DateOnly day) => InsightPeriod.DayStart(day);

    private static DateTimeOffset End(DateOnly day) => InsightPeriod.DayStart(day.AddDays(1));
}
