using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modbot.Analytics.Rollups;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Metrics;

/// <summary>
/// The charts of spec 5.6, read out of <c>modbot_rollup_daily</c> and the fact log.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Two sources, deliberately kept apart.</strong> The daily series come from the rollups,
/// which are kept forever; the per-type action breakdown and the observed member counts come from
/// facts, which an operator may have configured a window on. Mixing them into one series would
/// produce a chart whose left-hand end silently changes meaning, so each is reported with the
/// range it actually had (<see cref="MetricsCoverage"/>).
/// </para>
/// <para>
/// <strong><c>members.net</c> is not the group's member count.</strong> The rollup job computes
/// it as the running net of recorded joins and leaves, starting from zero on the fact log's first
/// day — a group that installs Modbot with 40,000 members watches the series start at zero and
/// climb. It is exposed here under a label that says so, and the real headcount is
/// <see cref="MetricsResponse.MemberCount"/>, which is what VRChat reported to the group-info
/// sync. Charting the rollup as "members" would be the exact failure this screen is supposed to
/// avoid: a real number that means something other than what it is labelled.
/// </para>
/// </remarks>
public sealed class MetricsQuery(ModbotContext db)
{
    public const int DefaultDays = 90;
    public const int MaxDays = 1830;

    /// <summary>How many moderators the per-moderator chart names before folding the rest away.</summary>
    public const int MaxModerators = 12;

    /// <summary>The action types the per-type breakdown counts, and the order it reports them in.</summary>
    public static readonly IReadOnlyList<string> ActionTypes =
    [
        FactType.MemberBanned,
        FactType.MemberUnbanned,
        FactType.MemberKicked,
        FactType.RoleGranted,
        FactType.RoleRevoked,
    ];

    public async Task<MetricsResponse> RunAsync(
        DateOnly from,
        DateOnly to,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        var rollups = await db.RollupDaily.AsNoTracking()
            .Where(r => r.Day >= from && r.Day <= to)
            .Select(r => new { r.Day, r.Metric, r.Dimension, r.Value })
            .ToListAsync(ct);

        List<DayValue> Undimensioned(string metric) => rollups
            .Where(r => r.Metric == metric && r.Dimension.Length == 0)
            .OrderBy(r => r.Day)
            .Select(r => new DayValue(r.Day, r.Value))
            .ToList();

        var series = new List<MetricSeries>
        {
            new(RollupMetrics.MembersJoined, "Members joined", null, Undimensioned(RollupMetrics.MembersJoined)),
            new(RollupMetrics.MembersLeft, "Members left", null, Undimensioned(RollupMetrics.MembersLeft)),
            new(RollupMetrics.BansAdded, "Bans recorded", null, Undimensioned(RollupMetrics.BansAdded)),
            new(
                RollupMetrics.MembersNet,
                "Net change since Modbot started recording",
                "Recorded joins minus recorded leaves, counted from zero on the first day of the "
                + "fact log. It is not the group's member count — that is the observed headcount "
                + "series, which comes from the group-info sync.",
                Undimensioned(RollupMetrics.MembersNet)),
        };

        var moderators = await ModeratorsAsync(rollups
            .Where(r => r.Metric == RollupMetrics.ModeratorActions && r.Dimension.Length > 0)
            .GroupBy(r => r.Dimension)
            .Select(g => (Dimension: g.Key, Actions: g.Sum(r => r.Value)))
            .OrderByDescending(g => g.Actions)
            .Take(MaxModerators)
            .ToList(), ct);

        return new MetricsResponse(
            from,
            to,
            series,
            await MemberCountAsync(from, to, ct),
            moderators,
            await ActionsByTypeAsync(from, to, ct),
            await CoverageAsync(ct),
            now);
    }

    /// <summary>
    /// Puts a name to each rollup dimension, where the fact log recorded one.
    /// </summary>
    /// <remarks>
    /// The dimension is <c>platform:id</c> (see <c>RollupDimensions</c>), and the id half is
    /// opaque — split on the first colon and never validate either side (spec 3.1.1).
    /// </remarks>
    private async Task<IReadOnlyList<ModeratorActivity>> ModeratorsAsync(
        IReadOnlyList<(string Dimension, decimal Actions)> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
            return [];

        var split = rows
            .Select(r =>
            {
                var colon = r.Dimension.IndexOf(':', StringComparison.Ordinal);

                return colon < 0
                    ? (r.Dimension, Platform: "unknown", ActorId: r.Dimension, r.Actions)
                    : (r.Dimension,
                       Platform: r.Dimension[..colon],
                       ActorId: r.Dimension[(colon + 1)..],
                       r.Actions);
            })
            .ToList();

        var ids = split.Select(s => s.ActorId).Distinct().ToList();

        // The most recent fact each actor caused, for the name recorded on it. One query for the
        // ids and one for the payloads, rather than a payload scan over the whole window.
        var latest = await db.Events.AsNoTracking()
            .Where(e => e.ActorId != null && ids.Contains(e.ActorId))
            .GroupBy(e => e.ActorId!)
            .Select(g => new { ActorId = g.Key, Id = g.Max(e => e.Id) })
            .ToListAsync(ct);

        var factIds = latest.Select(l => l.Id).ToList();

        var payloads = await db.Events.AsNoTracking()
            .Where(e => factIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Data })
            .ToListAsync(ct);

        var nameByFact = payloads.ToDictionary(
            p => p.Id,
            p => AuditJson.Text(AuditJson.Parse(p.Data), "actorDisplayName"));

        var nameByActor = latest
            .Where(l => nameByFact.TryGetValue(l.Id, out var n) && n is not null)
            .ToDictionary(l => l.ActorId, l => nameByFact[l.Id]);

        return split
            .Select(s => new ModeratorActivity(
                s.Dimension,
                s.Platform,
                s.ActorId,
                nameByActor.TryGetValue(s.ActorId, out var name) ? name : null,
                s.Actions))
            .ToList();
    }

    /// <summary>
    /// The group's headcount as VRChat reported it, one point per day it was observed changing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read out of <c>GroupInfoChanged</c> facts rather than from a rollup because there is no
    /// rollup for it: the group-info producer writes a baseline fact carrying the whole snapshot
    /// and thereafter writes only what changed, and neither shape is something the generic
    /// fact-count rollup can sum.
    /// </para>
    /// <para>
    /// <c>DISTINCT ON</c> takes the last observation of each UTC day. A day on which nothing
    /// changed produces no row at all — the producer does not write a fact when the poll saw what
    /// it left behind — so the series is sparse by design and the chart carries the last value
    /// forward rather than the table storing one row per calendar day forever.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<DayValue>> MemberCountAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
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

        return await ReadAsync(
            Sql,
            reader => new DayValue(
                DateOnly.FromDateTime(reader.GetDateTime(0)),
                reader.GetDecimal(1)),
            ct,
            ("type", FactType.GroupInfoChanged),
            ("from", DayStart(from)),
            ("to", DayStart(to.AddDays(1))));
    }

    /// <summary>
    /// Moderation actions per day per type, counted from facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// From facts and not from rollups because no rollup carries it: the registry has
    /// <c>bans.added</c> and an actor-dimensioned <c>moderator.actions</c>, and neither is a
    /// per-type daily breakdown. That makes this the one series on the screen bounded by the fact
    /// log rather than by the rollups, which is why the response reports both ranges.
    /// </para>
    /// <para>
    /// Counted, not apportioned. Every type here comes from VRChat's audit log, which states when
    /// the thing happened, so these facts carry no window to spread across days (spec 5.3).
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ActionTypeSeries>> ActionsByTypeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        // Bound as a text[] parameter rather than interpolated. The list is this class's own
        // constants, so interpolation was safe when they were numbers -- but they are strings now,
        // and a string that is safe only because nobody has typed a quote into it yet is not the
        // kind of safe this file should depend on.
        var types = ActionTypes.ToArray();

        var sql = $"""
            SELECT (e.occurred_at AT TIME ZONE 'UTC')::date AS day, e.type, COUNT(*)::numeric
            FROM modbot_event e
            WHERE e.type = ANY(@types) AND e.occurred_at >= @from AND e.occurred_at < @to
            GROUP BY 1, 2
            ORDER BY 1
            """;

        var rows = await ReadAsync(
            sql,
            reader => (
                Day: DateOnly.FromDateTime(reader.GetDateTime(0)),
                Type: reader.GetString(1),
                Value: reader.GetDecimal(2)),
            ct,
            ("types", types),
            ("from", DayStart(from)),
            ("to", DayStart(to.AddDays(1))));

        return ActionTypes
            .Select(type =>
            {
                var points = rows
                    .Where(r => r.Type == type)
                    .OrderBy(r => r.Day)
                    .Select(r => new DayValue(r.Day, r.Value))
                    .ToList();

                return new ActionTypeSeries(
                    type.ToString(),
                    FactLabels.For(type),
                    points.Sum(p => p.Value),
                    points);
            })
            .ToList();
    }

    private async Task<MetricsCoverage> CoverageAsync(CancellationToken ct)
    {
        var rollupFirst = await db.RollupDaily.AsNoTracking().MinAsync(r => (DateOnly?)r.Day, ct);
        var rollupLast = await db.RollupDaily.AsNoTracking().MaxAsync(r => (DateOnly?)r.Day, ct);

        var factFirst = await db.Events.AsNoTracking().MinAsync(e => (DateTimeOffset?)e.OccurredAt, ct);
        var factLast = await db.Events.AsNoTracking().MaxAsync(e => (DateTimeOffset?)e.OccurredAt, ct);

        var settings = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, ct);

        var moderation = settings?.ModerationFactRetentionDays ?? 0;
        var presence = settings?.PresenceFactRetentionDays ?? 0;

        return new MetricsCoverage(
            rollupFirst,
            rollupLast,
            factFirst is null ? null : DateOnly.FromDateTime(factFirst.Value.UtcDateTime),
            factLast is null ? null : DateOnly.FromDateTime(factLast.Value.UtcDateTime),
            moderation > 0 || presence > 0,
            moderation,
            presence);
    }

    /// <summary>
    /// Runs one statement through the context's own connection.
    /// </summary>
    /// <remarks>
    /// ADO rather than <c>SqlQueryRaw</c> because both statements project several columns, and
    /// the EF helper's single-column <c>Value</c> shape does not fit. Every value travels as a
    /// parameter; the only interpolated fragment is a list of enum members from this file.
    /// </remarks>
    private async Task<IReadOnlyList<T>> ReadAsync<T>(
        string sql,
        Func<DbDataReader, T> read,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;

        if (opened)
            await connection.OpenAsync(ct);

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            if (db.Database.CurrentTransaction is { } transaction)
                command.Transaction = transaction.GetDbTransaction();

            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            await using var reader = await command.ExecuteReaderAsync(ct);

            var rows = new List<T>();
            while (await reader.ReadAsync(ct))
                rows.Add(read(reader));

            return rows;
        }
        finally
        {
            if (opened)
                await connection.CloseAsync();
        }
    }

    /// <summary>UTC midnight beginning <paramref name="day"/>, as a <c>timestamptz</c> bound.</summary>
    private static DateTimeOffset DayStart(DateOnly day)
        => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
