using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.VRChat.Sync;

namespace Modbot.Api.Features.Analytics;

/// <summary>
/// The arithmetic behind the days a chart marks as having no data, and the day it marks as not
/// over yet. Pure, so the rules can be tested without a database.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A day with nothing recorded is not a day of nought.</strong> A daily chart fills a day
/// that has no row with zero, which is right for a day Modbot was watching and nothing happened,
/// and wrong for a day Modbot was not watching at all. The two used to look identical. Each list
/// here is a day the named source has nothing for, worked out from that source alone, so the
/// chart can draw it as missing instead.
/// </para>
/// <para>
/// <strong>Nothing here guesses.</strong> Where the server cannot tell whether a source was
/// running, the day is left out of the list and drawn as it always was. A gap is only ever
/// claimed from something recorded: the first fact a source wrote, the first reading a sync took,
/// an instance that was open with no count taken of it.
/// </para>
/// </remarks>
public static class MissingDays
{
    /// <summary>
    /// The window's last day, when that day is today by the deployment's clock and so not over
    /// yet; otherwise null. From the server's clock, never the browser's (spec 4.4).
    /// </summary>
    public static DateOnly? Today(DateOnly to, DateTimeOffset now) => to == AnalyticsSql.DayOf(now) ? to : null;

    /// <summary>Every day from <paramref name="from"/> to <paramref name="to"/>, both included.</summary>
    public static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to)
    {
        for (var day = from; day <= to; day = day.AddDays(1))
            yield return day;
    }

    /// <summary>
    /// The days of a window before a source's first day.
    /// </summary>
    /// <param name="start">
    /// The first day the source has anything for, or null when it has never recorded anything:
    /// then every day is missing, because nothing was recorded on any of them.
    /// </param>
    public static IReadOnlyList<DateOnly> Before(DateOnly from, DateOnly to, DateOnly? start)
        => Days(from, to).Where(d => start is null || d < start.Value).ToList();

    /// <summary>
    /// The days of the member count chart with nothing at all behind them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three stretches, in order. Before the first thing known (a group-info fact or a reading),
    /// nothing is known: missing. From the first fact to the first reading, the facts stand in, one
    /// observation per day carried forward: not missing, but carried, which the points say
    /// themselves. From the first reading on, the sync reads every five minutes, so a day with no
    /// reading is a day it was not running: missing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DateOnly> WithoutReadings(
        DateOnly from,
        DateOnly to,
        DateOnly? firstKnown,
        DateOnly? firstReading,
        IReadOnlySet<DateOnly> daysWithReadings)
        => Days(from, to)
            .Where(d =>
                firstKnown is null
                || d < firstKnown.Value
                || (firstReading is { } reading && d >= reading && !daysWithReadings.Contains(d)))
            .ToList();

    /// <summary>The earlier of two days, either of which may be unknown.</summary>
    public static DateOnly? Earliest(DateOnly? a, DateOnly? b) => (a, b) switch
    {
        (null, var y) => y,
        (var x, null) => x,
        (var x, var y) => x < y ? x : y,
    };
}

/// <summary>
/// Reads, per source, which days of a window it has nothing for (see <see cref="MissingDays"/>).
/// </summary>
/// <remarks>
/// Every read is bounded: a first day is one index lookup per fact type, and the per-day checks
/// walk the window's days or the instances open inside it, never the whole fact log.
/// </remarks>
public sealed class MissingDaysQuery(ModbotContext db)
{
    private readonly AnalyticsSql _sql = new(db);

    /// <summary>The daily total metrics counted only from fact types the audit log writes.</summary>
    private static readonly string[] AuditLogMetrics = DailyTotalMetrics.FactCounts
        .Where(m => m.Types.All(t => GroupAuditLogEvents.FactTypes.Contains(t)))
        .Select(m => m.Name)
        .ToArray();

    /// <summary>
    /// Days before Modbot began reading the group's audit log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The start is the first fact the audit log wrote. The catch-up walks back through the history
    /// VRChat still holds before it reads anything live, so that first fact is as far back as the
    /// record goes, and a day before it was never read.
    /// </para>
    /// <para>
    /// Where an operator has set a retention window the oldest facts may have been deleted while
    /// the daily totals made from them are kept forever (spec 5.5). Then the first daily total
    /// counted from audit-log facts reaches further back, and it is used where it does.
    /// </para>
    /// <para>
    /// A stretch inside the record that the sync could not read is not marked: the sync keeps its
    /// place and re-reads from it after any failure, and nothing records a stretch it missed, so
    /// there is nothing to mark it from.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<DateOnly>> AuditLogAsync(DateOnly from, DateOnly to, CancellationToken ct)
        => MissingDays.Before(from, to, await AuditLogStartAsync(ct));

    public async Task<DateOnly?> AuditLogStartAsync(CancellationToken ct)
    {
        const string Sql = """
            SELECT MIN(f.first_at)
            FROM unnest(@types) AS t(type)
            CROSS JOIN LATERAL (
                SELECT MIN(e.occurred_at) AS first_at
                FROM modbot_event e
                WHERE e.type = t.type AND e.source = @source
            ) f
            """;

        var rows = await _sql.ReadAsync(
            Sql,
            r => AnalyticsSql.InstantOrNull(r, 0),
            ct,
            ("types", GroupAuditLogEvents.FactTypes.ToArray()),
            ("source", (short)FactSource.AuditLog));

        var firstFact = rows.Count > 0 && rows[0] is { } at ? AnalyticsSql.DayOf(at) : (DateOnly?)null;

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);
        var pruned = (settings?.ModerationFactRetentionDays ?? 0) > 0 || (settings?.PresenceFactRetentionDays ?? 0) > 0;

        if (!pruned)
            return firstFact;

        var firstTotal = await db.DailyTotals.AsNoTracking()
            .Where(r => AuditLogMetrics.Contains(r.Metric))
            .MinAsync(r => (DateOnly?)r.Day, ct);

        return MissingDays.Earliest(firstFact, firstTotal);
    }

    /// <summary>
    /// Days a group instance was open and no companion reported anything at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Presence reports exist only while a moderator's companion is in an instance, so a day
    /// without any is either a day nobody with the companion went in, or a day nothing was open.
    /// Only the first is missing. Calling every quiet day "no data" would mark a small group's
    /// figures unreliable for being small (peaks spec 3.1), so a day counts only when something
    /// says an instance was open: the instance table, or the audit log's openings.
    /// </para>
    /// <para>
    /// A day whose presence facts a retention window has deleted still has its visitors daily
    /// total, which is kept forever, and is not missing.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<DateOnly>> PresenceReportsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var sql = $"""
            WITH {Lives},
            open_days AS (
                SELECT d::date AS day
                FROM lives l
                CROSS JOIN LATERAL generate_series(
                    date_trunc('day', l.s AT TIME ZONE 'UTC'),
                    date_trunc('day', (l.e AT TIME ZONE 'UTC') - interval '1 microsecond'),
                    interval '1 day') AS d
                WHERE l.e > l.s
                UNION
                SELECT t.day
                FROM modbot_daily_total t
                WHERE t.metric = @opened AND t.dimension = ''
                  AND t.day >= @firstDay AND t.day <= @lastDay AND t.value > 0
            )
            SELECT o.day
            FROM open_days o
            WHERE NOT EXISTS (
                    SELECT 1 FROM modbot_event e
                    WHERE e.type = ANY(@presence)
                      AND e.occurred_at >= o.day::timestamp AT TIME ZONE 'UTC'
                      AND e.occurred_at < (o.day + 1)::timestamp AT TIME ZONE 'UTC')
              AND NOT EXISTS (
                    SELECT 1 FROM modbot_daily_total t
                    WHERE t.metric = @visitors AND t.day = o.day)
            ORDER BY o.day
            """;

        return await _sql.ReadAsync(
            sql,
            r => AnalyticsSql.DayOf(r, 0),
            ct,
            [
                .. await WindowParametersAsync(from, to, ct),
                ("opened", DailyTotalMetrics.InstancesOpened),
                ("visitors", DailyTotalMetrics.WorldVisitors),
                ("presence", AnalyticsSql.PresenceTypes),
            ]);
    }

    /// <summary>
    /// Days a group instance was open and Modbot had no head count for any of that time.
    /// </summary>
    /// <remarks>
    /// The same rule the instance coverage uses (peaks spec 3.1): an instance counts from its first
    /// head count onwards, and the measure is open time, never the calendar. A day nothing was open
    /// is a quiet day and not missing; a day an instance was open and never counted is.
    /// </remarks>
    public async Task<IReadOnlyList<DateOnly>> HeadCountsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var sql = $"""
            WITH {Lives},
            spans AS (
                SELECT (l.s AT TIME ZONE 'UTC') AS s,
                       (l.e AT TIME ZONE 'UTC') AS e,
                       (CASE WHEN f.first_at IS NULL THEN NULL ELSE GREATEST(l.s, f.first_at) END) AT TIME ZONE 'UTC' AS counted_from
                FROM lives l
                LEFT JOIN LATERAL (
                    SELECT MIN(h.counted_at) AS first_at
                    FROM instance_head_count h
                    WHERE h.instance_id = l.id
                ) f ON true
                WHERE l.e > l.s
            )
            SELECT d::date AS day
            FROM spans sp
            CROSS JOIN LATERAL generate_series(
                date_trunc('day', sp.s),
                date_trunc('day', sp.e - interval '1 microsecond'),
                interval '1 day') AS d
            GROUP BY d
            HAVING NOT bool_or(
                sp.counted_from IS NOT NULL
                AND GREATEST(sp.counted_from, d) < LEAST(sp.e, d + interval '1 day'))
            ORDER BY d
            """;

        return await _sql.ReadAsync(sql, r => AnalyticsSql.DayOf(r, 0), ct, await WindowParametersAsync(from, to, ct));
    }

    /// <summary>
    /// The first day the Discord bot was reading the server: when it first read the member list,
    /// or the first member count it kept, whichever is earlier. Null when it never has.
    /// </summary>
    /// <remarks>
    /// Nothing records the stretches the bot was disconnected, so only the start is known. Joins
    /// and leaves across a disconnect are caught up when it returns; voice time is not, and those
    /// days read as quiet rather than missing.
    /// </remarks>
    public async Task<DateOnly?> DiscordBotStartAsync(string? guildId, CancellationToken ct)
    {
        var listed = await db.DiscordServers.AsNoTracking()
            .Where(s => guildId == null || s.GuildId == guildId)
            .MinAsync(s => s.MembersListedAt, ct);

        var firstCount = await db.DailyTotals.AsNoTracking()
            .Where(r => r.Metric == DailyTotalMetrics.DiscordMembersCount)
            .MinAsync(r => (DateOnly?)r.Day, ct);

        return MissingDays.Earliest(listed is { } at ? AnalyticsSql.DayOf(at) : null, firstCount);
    }

    /// <summary>
    /// The first day with a stored Discord message. The bot reads history back when it signs in,
    /// so messages can reach further back than the bot itself.
    /// </summary>
    public async Task<DateOnly?> DiscordMessagesStartAsync(CancellationToken ct)
        => await db.DailyTotals.AsNoTracking()
            .Where(r => r.Metric == DailyTotalMetrics.DiscordMessages)
            .MinAsync(r => (DateOnly?)r.Day, ct);

    /// <summary>
    /// The group's instances open at some point in the window, each clipped to it. Bounded the way
    /// the instance coverage is: instances opened no earlier than an instance can still be running.
    /// </summary>
    private const string Lives = """
        lives AS (
            SELECT i.id,
                   GREATEST(i.opened_at, @from) AS s,
                   LEAST(COALESCE(i.closed_at, i.last_seen_at), @to) AS e
            FROM vrchat_instance i
            WHERE i.group_id = @group
              AND i.opened_at >= @seedFrom AND i.opened_at < @to
              AND COALESCE(i.closed_at, i.last_seen_at) > @from
        )
        """;

    private async Task<(string Name, object? Value)[]> WindowParametersAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var group = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ManagedGroupId)
            .FirstOrDefaultAsync(ct);

        return
        [
            // No group set up: an empty id matches no instance, which is the truth.
            ("group", string.IsNullOrWhiteSpace(group) ? string.Empty : group),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)),
            ("seedFrom", Instances.InstanceActivitySql.SeedFrom(AnalyticsSql.DayStart(from))),
            ("firstDay", from),
            ("lastDay", to),
        ];
    }
}
