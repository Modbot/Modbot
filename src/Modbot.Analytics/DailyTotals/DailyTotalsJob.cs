using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Npgsql;

namespace Modbot.Analytics.DailyTotals;

/// <summary>
/// Computes <c>modbot_daily_total</c> from the fact log.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The invariant this class exists to keep: daily totals are always recomputable from facts
/// (spec 5.2).</strong> <see cref="RebuildAsync"/> throws every computed row away and rebuilds it,
/// and that is the supported fix for any aggregation bug -- a wrong metric is a re-run, not lost
/// data, and a metric invented next year is filled in across all recorded history. Nothing here may
/// ever depend on state that only the incremental path produces, because then a rebuild would not
/// reproduce it. The property test in the test suite is what holds the line.
/// </para>
/// <para>
/// <strong>Imprecise facts.</strong> A fact carries <c>occurred_at</c> and a nullable
/// <c>occurred_before</c>; when the second is set, the fact happened somewhere inside that window
/// and not at its lower bound (spec 5.3). Daily totals therefore apportion each fact across the days
/// its window covers, weighted by how much of the window falls in each day. An exact fact is a
/// zero-length window and lands wholly on its own day; a five-minute sync-diff window at 14:00
/// does too, because it does not cross midnight; only a window that straddles a day boundary
/// splits, and it splits in proportion. This is why <c>value</c> is <c>numeric</c> rather than a
/// count.
/// </para>
/// <para>
/// The alternative the spec also allows -- excluding low-precision facts from fine-grained series
/// -- is the wrong trade at daily granularity: sync-diff windows are minutes wide, so excluding
/// them would drop nearly every membership change Modbot infers, to avoid a rounding error at one
/// boundary in 288. It becomes the right trade for an hourly series, where a five-minute window is
/// a meaningful fraction of the bucket; that decision belongs to whatever builds the hourly
/// series, not here.
/// </para>
/// <para>
/// What is <em>not</em> done is collapsing the window to <c>occurred_at</c>. That invents precision
/// Modbot does not have and shows up as fake spikes at the start of each polling interval.
/// </para>
/// <para>
/// All day arithmetic is in UTC, matching the fact log's UTC monthly partitions. One system with
/// two day boundaries would be a bug farm.
/// </para>
/// </remarks>
public sealed class DailyTotalsJob
{
    /// <summary>
    /// How far behind the watermark an incremental run re-scans.
    /// </summary>
    /// <remarks>
    /// The watermark is a high-water mark of <c>observed_at</c>, and <c>observed_at</c> is stamped
    /// when a fact is built rather than when its transaction commits -- so a slow transaction can
    /// commit a fact whose <c>observed_at</c> is already below the mark. Re-scanning a minute of
    /// history costs a recompute of days that are idempotent anyway. A fact that lands later than
    /// this is not lost, merely late: it is picked up by the next rebuild.
    /// </remarks>
    public static readonly TimeSpan WatermarkOverlap = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Serialises daily totals runs against each other. Two runs recomputing overlapping days would
    /// interleave a delete with the other's insert and leave a day short.
    /// </summary>
    private const long DailyTotalsLockKey = 0x4D4F44_524F4C; // "MOD" "ROL" -- the value predates the rename and stays

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;

    public DailyTotalsJob(ModbotContext db, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
    }

    /// <summary>
    /// Folds in everything learned since the last run.
    /// </summary>
    /// <remarks>
    /// Driven by <c>observed_at</c>, not <c>occurred_at</c>: facts arrive out of order, and a run
    /// that only looked at recent <em>occurrences</em> would silently skip the sync diff that
    /// dated a departure to yesterday.
    /// </remarks>
    public async Task<DailyTotalsRunResult> RunIncrementalAsync(CancellationToken ct = default)
    {
        var state = await StateAsync(ct);
        var since = state.ObservedThrough - WatermarkOverlap;
        var messagesSince = state.MessagesStoredThrough - WatermarkOverlap;

        var days = (await DaysTouchedSinceAsync(since, ct))
            .Concat(await MessageDaysStoredSinceAsync(messagesSince, ct))
            .Distinct()
            .ToList();

        var highWater = await MaxObservedAtAsync(ct);
        var messagesHighWater = await MaxStoredAtAsync(ct);

        if (days.Count == 0)
        {
            await SaveWatermarkAsync(state, highWater, messagesHighWater, ct);
            return new DailyTotalsRunResult(0, null, null);
        }

        var result = await RecomputeDaysAsync(days, ct);
        await SaveWatermarkAsync(state, highWater, messagesHighWater, ct);

        return result;
    }

    /// <summary>
    /// Throws away every computed daily total the fact log can still account for, and rebuilds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rows older than the oldest surviving fact are <strong>kept</strong>. Daily totals are never
    /// aged out, while facts can be where an operator has configured a window (spec 5.5), so for
    /// the days the facts have been pruned out of, the daily total is the only remaining record -- "charts keep their full
    /// history even after the underlying events age out". A rebuild that started from zero would
    /// destroy exactly the history the design promises to preserve.
    /// </para>
    /// <para>
    /// Counted-only rows (spec 5.2.1) are never touched either: nothing can recompute them.
    /// </para>
    /// </remarks>
    public async Task<DailyTotalsRunResult> RebuildAsync(CancellationToken ct = default)
    {
        var (factFirst, factLast) = await FactDayRangeAsync(ct);
        var (messageFirst, messageLast) = await MessageDayRangeAsync(ct);

        var first = Earliest(factFirst, messageFirst);
        var last = Latest(factLast, messageLast);

        if (first is null || last is null)
            return new DailyTotalsRunResult(0, null, null);

        var days = new List<DateOnly>();
        for (var day = first.Value; day <= last.Value; day = day.AddDays(1))
            days.Add(day);

        var result = await RecomputeDaysAsync(days, ct);

        var state = await StateAsync(ct);
        await SaveWatermarkAsync(state, await MaxObservedAtAsync(ct), await MaxStoredAtAsync(ct), ct);

        return result;
    }

    private static DateOnly? Earliest(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private static DateOnly? Latest(DateOnly? a, DateOnly? b) => a is null ? b : b is null ? a : a > b ? a : b;

    /// <summary>
    /// Recomputes exactly the days given -- every computed metric, from facts.
    /// </summary>
    /// <remarks>
    /// Delete-then-insert per day rather than upsert, so that a day whose facts have gone (a
    /// purge, spec 5.5) loses its daily total row instead of keeping a stale one.
    /// </remarks>
    public async Task<DailyTotalsRunResult> RecomputeDaysAsync(
        IReadOnlyCollection<DateOnly> days,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(days);

        if (days.Count == 0)
            return new DailyTotalsRunResult(0, null, null);

        var ordered = days.Distinct().Order().ToArray();
        var from = ordered[0];
        var to = ordered[^1];

        var ownsTransaction = _db.Database.CurrentTransaction is null;
        var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

        try
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({DailyTotalsLockKey})", ct);

            var rows = 0;

            // Message totals on days older than the oldest month of messages still kept are the only
            // record of those days once retention has dropped the messages; they are left alone.
            var messageFloor = await MessageFloorAsync(ct);

            await ExecuteAsync(
                """
                DELETE FROM modbot_daily_total
                WHERE origin = @origin AND day = ANY(@days)
                  AND NOT (metric = ANY(@messageMetrics) AND day < @messageFloor)
                """,
                ct,
                Param("origin", (short)DailyTotalOrigin.Computed),
                Param("days", ordered),
                Param("messageMetrics", DailyTotalMetrics.MessageCounts.Select(m => m.Name).ToArray()),
                Param("messageFloor", messageFloor));

            foreach (var metric in DailyTotalMetrics.FactCounts)
                rows += await ComputeFactCountAsync(metric, ordered, from, to, ct);

            var messageDays = ordered.Where(d => d >= messageFloor).ToArray();
            if (messageDays.Length > 0)
            {
                foreach (var metric in DailyTotalMetrics.MessageCounts)
                    rows += await ComputeMessageCountAsync(metric, messageDays, ct);
            }

            foreach (var metric in DailyTotalMetrics.VoiceMinutes)
                rows += await ComputeVoiceMinutesAsync(metric, ordered, from, to, ct);

            foreach (var metric in DailyTotalMetrics.Cumulative)
                rows += await ComputeCumulativeAsync(metric, from, ct);

            if (transaction is not null)
                await transaction.CommitAsync(ct);

            return new DailyTotalsRunResult(rows, from, to);
        }
        finally
        {
            if (transaction is not null)
                await transaction.DisposeAsync();
        }
    }

    /// <summary>
    /// The days a fact's window touches, for every fact observed after <paramref name="since"/>.
    /// </summary>
    private async Task<IReadOnlyList<DateOnly>> DaysTouchedSinceAsync(
        DateTimeOffset? since,
        CancellationToken ct)
    {
        var parameters = new List<NpgsqlParameter> { Param("types", TypeValues(DailyTotalMetrics.ComputedTypes)) };
        if (since is not null)
            parameters.Add(Param("since", since.Value));

        return await QueryAsync<DateOnly>(
            DaysTouchedSql(WindowedFacts("@types", observedSince: since is not null)),
            parameters,
            ct);
    }

    /// <summary>
    /// The days every fact about one subject touches -- what a purge has to recompute once those
    /// facts are gone (spec 5.5).
    /// </summary>
    /// <remarks>
    /// It lives here rather than in the purger because the day a fact belongs to is this class's
    /// decision: an imprecise fact belongs to every day its window covers, and two places
    /// deciding that differently is how a purge leaves a stale row behind.
    /// </remarks>
    public async Task<IReadOnlyList<DateOnly>> DaysTouchedBySubjectAsync(
        FactPlatform platform,
        string subjectId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(subjectId);

        var facts = WindowedFacts(
            "@types",
            observedSince: false,
            extraFilter: "AND e.subject_platform = @platform AND e.subject_id = @subject");

        return await QueryAsync<DateOnly>(
            DaysTouchedSql(facts),
            [
                Param("types", TypeValues(DailyTotalMetrics.ComputedTypes)),
                Param("platform", (short)platform),
                Param("subject", subjectId),
            ],
            ct);
    }

    private static string DaysTouchedSql(string windowedFacts) => $"""
        SELECT DISTINCT (gs)::date AS "Value"
        FROM (
            {windowedFacts}
        ) w
        CROSS JOIN LATERAL generate_series(
            date_trunc('day', w.lo), date_trunc('day', w.hi), interval '1 day') AS gs
        """;

    private async Task<int> ComputeFactCountAsync(
        FactCountMetric metric,
        DateOnly[] days,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var dimension = metric.Dimension switch
        {
            DailyTotalDimensionKind.Actor => ActorDimensionSql,
            DailyTotalDimensionKind.World => "s.world_id",
            _ => "''",
        };

        // A fact with nothing to break it down by contributes nothing to a broken-down metric:
        // a ban VRChat did not attribute counts as a ban, not as anybody's action.
        var dimensionFilter = metric.Dimension switch
        {
            DailyTotalDimensionKind.Actor => "AND s.actor_id IS NOT NULL",
            DailyTotalDimensionKind.World => "AND s.world_id IS NOT NULL",
            _ => string.Empty,
        };

        // PostgreSQL rejects a bare string constant in GROUP BY, so the undimensioned metrics
        // group by the day alone -- which is the same grouping, since their dimension is ''.
        var grouping = metric.Dimension is DailyTotalDimensionKind.None
            ? "s.day"
            : $"s.day, {dimension}";

        var conditionFilter = metric.Condition switch
        {
            FactCondition.ActorIsNotSubject => "AND e.actor_id IS NOT NULL AND e.actor_id <> e.subject_id",
            _ => string.Empty,
        };

        // Distinct people are counted, not weighed: a person seen on a day was there, however
        // many facts say so. The weight still gates which day an imprecise fact lands on.
        var aggregate = metric.CountDistinctSubjects
            ? "COUNT(DISTINCT s.subject_id)::numeric"
            : "SUM(s.weight)";

        // The weight of one fact on one day: the fraction of its [occurred_at, occurred_before]
        // window that falls inside that day. Exact facts (hi <= lo) weigh 1 on their own day.
        //
        // Rounded to nine places so the value survives the numeric -> decimal round trip intact;
        // unrounded numeric division can produce more significant digits than a decimal holds.
        // The rounding is applied identically on every path, so a rebuild still reproduces an
        // incremental run to the digit.
        var sql = $"""
            WITH windowed AS (
                {WindowedFacts("@types", observedSince: false, boundedDays: true, extraFilter: conditionFilter)}
            ),
            spread AS (
                SELECT w.actor_platform,
                       w.actor_id,
                       w.subject_id,
                       w.world_id,
                       (gs)::date AS day,
                       CASE
                           WHEN w.hi <= w.lo THEN 1::numeric
                           ELSE round(
                               EXTRACT(EPOCH FROM (LEAST(w.hi, gs + interval '1 day') - GREATEST(w.lo, gs)))::numeric
                               / EXTRACT(EPOCH FROM (w.hi - w.lo))::numeric, 9)
                       END AS weight
                FROM windowed w
                CROSS JOIN LATERAL generate_series(
                    date_trunc('day', w.lo), date_trunc('day', w.hi), interval '1 day') AS gs
            )
            INSERT INTO modbot_daily_total (day, metric, dimension, value, origin)
            SELECT s.day, @metric, {dimension}, {aggregate}, @origin
            FROM spread s
            WHERE s.day = ANY(@days) AND s.weight > 0 {dimensionFilter}
            GROUP BY {grouping}
            HAVING {aggregate} <> 0
            """;

        return await ExecuteAsync(
            sql,
            ct,
            Param("types", TypeValues(metric.Types)),
            Param("days", days),
            Param("metric", metric.Name),
            Param("origin", (short)DailyTotalOrigin.Computed),
            Param("from", DayStart(from)),
            Param("to", DayStart(to.AddDays(1))));
    }

    /// <summary>Counts stored Discord messages people sent, per day, for the days given.</summary>
    private async Task<int> ComputeMessageCountAsync(MessageCountMetric metric, DateOnly[] days, CancellationToken ct)
    {
        var dimension = metric.Dimension switch
        {
            MessageDimensionKind.Channel => "m.channel_id",
            MessageDimensionKind.Author => $"'{DailyTotalDimensions.Label(FactPlatform.Discord)}:' || m.author_id",
            MessageDimensionKind.Hour => "to_char(m.sent_at AT TIME ZONE 'UTC', 'HH24')",
            _ => "''",
        };

        // Grouped by position: the day, and the dimension when there is one.
        var grouping = metric.Dimension is MessageDimensionKind.None ? "1" : "1, 3";

        // Bounded by the days' own range first, so the planner keeps to their months' partitions.
        var sql = $"""
            INSERT INTO modbot_daily_total (day, metric, dimension, value, origin)
            SELECT (m.sent_at AT TIME ZONE 'UTC')::date, @metric, {dimension}, COUNT(*)::numeric, @origin
            FROM discord_message m
            WHERE m.sent_at >= @from AND m.sent_at < @to
              AND NOT m.author_is_bot
              AND (m.sent_at AT TIME ZONE 'UTC')::date = ANY(@days)
            GROUP BY {grouping}
            """;

        return await ExecuteAsync(
            sql,
            ct,
            Param("metric", metric.Name),
            Param("origin", (short)DailyTotalOrigin.Computed),
            Param("days", days),
            Param("from", DayStart(days[0])),
            Param("to", DayStart(days[^1].AddDays(1))));
    }

    /// <summary>
    /// Minutes in voice for the days given: each stretch from a join or a move to the next voice
    /// fact for the same person, counted on the day it ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On the day it ended, not split across midnight, because the day a stretch ends is the day
    /// whose facts arrive with it: an incremental run recomputes that day, and a stretch split onto
    /// an earlier day would never be recomputed there. A rebuild counts it the same way.
    /// </para>
    /// <para>
    /// A stretch is at most a day. A leave nobody saw is closed when the bot next signs in, but a
    /// join followed by another join with nothing between must not become a week in voice. Bots
    /// are left out, as they are from message counts.
    /// </para>
    /// </remarks>
    private async Task<int> ComputeVoiceMinutesAsync(
        VoiceMinutesMetric metric,
        DateOnly[] days,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var dimension = metric.PerPerson
            ? $"'{DailyTotalDimensions.Label(FactPlatform.Discord)}:' || s.subject_id"
            : "''";

        var grouping = metric.PerPerson ? "s.day, s.subject_id" : "s.day";

        var sql = $"""
            WITH v AS (
                SELECT e.subject_id,
                       e.occurred_at,
                       LAG(e.type) OVER w AS previous_type,
                       LAG(e.occurred_at) OVER w AS previous_at
                FROM modbot_event e
                WHERE e.subject_platform = @discord
                  AND e.type = ANY(@types)
                  AND e.occurred_at >= @lookback AND e.occurred_at < @to
                WINDOW w AS (PARTITION BY e.subject_id ORDER BY e.occurred_at, e.id)
            ),
            s AS (
                SELECT v.subject_id,
                       (v.occurred_at AT TIME ZONE 'UTC')::date AS day,
                       LEAST(EXTRACT(EPOCH FROM (v.occurred_at - v.previous_at)) / 60.0, 1440) AS minutes
                FROM v
                WHERE v.previous_type = ANY(@opens)
                  AND v.occurred_at >= @from
                  AND NOT EXISTS (
                      SELECT 1 FROM discord_member b WHERE b.user_id = v.subject_id AND b.is_bot)
            )
            INSERT INTO modbot_daily_total (day, metric, dimension, value, origin)
            SELECT s.day, @metric, {dimension}, round(SUM(s.minutes)::numeric, 3), @origin
            FROM s
            WHERE s.day = ANY(@days)
            GROUP BY {grouping}
            HAVING SUM(s.minutes) > 0
            """;

        return await ExecuteAsync(
            sql,
            ct,
            Param("discord", (short)FactPlatform.Discord),
            Param("types", TypeValues(DailyTotalMetrics.VoiceTypes)),
            Param("opens", new[] { FactType.DiscordVoiceJoined, FactType.DiscordVoiceMoved }),
            Param("days", days),
            Param("metric", metric.Name),
            Param("origin", (short)DailyTotalOrigin.Computed),
            Param("lookback", DayStart(from.AddDays(-2))),
            Param("from", DayStart(from)),
            Param("to", DayStart(to.AddDays(1))));
    }

    /// <summary>The UTC days of messages stored after <paramref name="since"/>, or of every message when null.</summary>
    private async Task<IReadOnlyList<DateOnly>> MessageDaysStoredSinceAsync(DateTimeOffset? since, CancellationToken ct)
    {
        var parameters = new List<NpgsqlParameter>();
        var filter = string.Empty;

        if (since is not null)
        {
            filter = "WHERE m.stored_at > @since";
            parameters.Add(Param("since", since.Value));
        }

        return await QueryAsync<DateOnly>(
            $"SELECT DISTINCT (m.sent_at AT TIME ZONE 'UTC')::date AS \"Value\" FROM discord_message m {filter}",
            parameters,
            ct);
    }

    /// <summary>
    /// The UTC days one Discord user's messages fall on -- what a purge recomputes once they are gone.
    /// </summary>
    public async Task<IReadOnlyList<DateOnly>> MessageDaysByAuthorAsync(string authorId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(authorId);

        return await QueryAsync<DateOnly>(
            "SELECT DISTINCT (m.sent_at AT TIME ZONE 'UTC')::date AS \"Value\" FROM discord_message m WHERE m.author_id = @author",
            [Param("author", authorId)],
            ct);
    }

    private async Task<(DateOnly? First, DateOnly? Last)> MessageDayRangeAsync(CancellationToken ct)
    {
        var first = await QueryAsync<DateOnly?>(
            "SELECT MIN(m.sent_at AT TIME ZONE 'UTC')::date AS \"Value\" FROM discord_message m", [], ct);
        var last = await QueryAsync<DateOnly?>(
            "SELECT MAX(m.sent_at AT TIME ZONE 'UTC')::date AS \"Value\" FROM discord_message m", [], ct);

        return (first.Count > 0 ? first[0] : null, last.Count > 0 ? last[0] : null);
    }

    /// <summary>
    /// The first day of the oldest month of Discord messages still kept, found from the partitions
    /// retention drops by name. When there are none, every message total is kept.
    /// </summary>
    private async Task<DateOnly> MessageFloorAsync(CancellationToken ct)
    {
        var names = await QueryAsync<string>(
            """
            SELECT child.relname AS "Value"
            FROM pg_inherits i
            JOIN pg_class child ON child.oid = i.inhrelid
            JOIN pg_class parent ON parent.oid = i.inhparent
            WHERE parent.relname = 'discord_message'
            """,
            [],
            ct);

        var months = names
            .Select(n => n.Length >= 7
                && int.TryParse(n.AsSpan(n.Length - 7, 4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var year)
                && int.TryParse(n.AsSpan(n.Length - 2, 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var month)
                && month is >= 1 and <= 12
                    ? new DateOnly(year, month, 1)
                    : (DateOnly?)null)
            .OfType<DateOnly>()
            .ToList();

        return months.Count > 0 ? months.Min() : DateOnly.MaxValue;
    }

    private async Task<DateTimeOffset?> MaxStoredAtAsync(CancellationToken ct)
    {
        var max = await QueryAsync<DateTimeOffset?>(
            "SELECT MAX(stored_at) AS \"Value\" FROM discord_message",
            [],
            ct);

        return max.Count > 0 ? max[0] : null;
    }

    /// <summary>
    /// Rewrites a running total from <paramref name="from"/> forward, carrying the value stored
    /// for the last day before it.
    /// </summary>
    /// <remarks>
    /// Computed from the daily rows rather than from facts a second time, so the total and its
    /// components can never disagree. Forward, because a fact that lands on an old day moves every
    /// later total with it -- which is exactly what an incremental run has to get right and what
    /// the property test checks.
    /// </remarks>
    private async Task<int> ComputeCumulativeAsync(
        CumulativeMetric metric,
        DateOnly from,
        CancellationToken ct)
    {
        // The value carried in from before the recomputed range. Read before the delete below,
        // and from a day the delete does not touch.
        var carried = await QueryAsync<decimal>(
            """
            SELECT value AS "Value"
            FROM modbot_daily_total
            WHERE metric = @metric AND dimension = '' AND day < @from
            ORDER BY day DESC
            LIMIT 1
            """,
            [Param("metric", metric.Name), Param("from", from)],
            ct);

        // Days that no longer have any movement lose their row; leaving one behind would show a
        // step in the chart that no fact accounts for.
        await ExecuteAsync(
            "DELETE FROM modbot_daily_total WHERE metric = @metric AND day >= @from AND origin = @origin",
            ct,
            Param("metric", metric.Name),
            Param("from", from),
            Param("origin", (short)DailyTotalOrigin.Computed));

        return await ExecuteAsync(
            """
            INSERT INTO modbot_daily_total (day, metric, dimension, value, origin)
            SELECT d.day, @metric, '', @carried + SUM(d.delta) OVER (ORDER BY d.day), @origin
            FROM (
                SELECT day, SUM(CASE WHEN metric = @plus THEN value ELSE -value END) AS delta
                FROM modbot_daily_total
                WHERE origin = @origin AND day >= @from AND metric IN (@plus, @minus)
                GROUP BY day
            ) d
            """,
            ct,
            Param("metric", metric.Name),
            Param("plus", metric.Plus),
            Param("minus", metric.Minus),
            Param("carried", carried.Count > 0 ? carried[0] : 0m),
            Param("origin", (short)DailyTotalOrigin.Computed),
            Param("from", from));
    }

    private async Task<(DateOnly? First, DateOnly? Last)> FactDayRangeAsync(CancellationToken ct)
    {
        var first = await FactDayBoundAsync("MIN(date_trunc('day', w.lo))", ct);
        var last = await FactDayBoundAsync("MAX(date_trunc('day', w.hi))", ct);

        return (first, last);
    }

    private async Task<DateOnly?> FactDayBoundAsync(string aggregate, CancellationToken ct)
    {
        var bound = await QueryAsync<DateOnly?>(
            $"""
            SELECT {aggregate}::date AS "Value"
            FROM ({WindowedFacts("@types", observedSince: false)}) w
            """,
            [Param("types", TypeValues(DailyTotalMetrics.ComputedTypes))],
            ct);

        return bound.Count > 0 ? bound[0] : null;
    }

    private async Task<DateTimeOffset?> MaxObservedAtAsync(CancellationToken ct)
    {
        var max = await QueryAsync<DateTimeOffset?>(
            "SELECT MAX(observed_at) AS \"Value\" FROM modbot_event",
            [],
            ct);

        return max.Count > 0 ? max[0] : null;
    }

    private async Task<DailyTotalsState> StateAsync(CancellationToken ct)
    {
        var state = await _db.DailyTotalsState.FirstOrDefaultAsync(s => s.Id == 1, ct);
        if (state is not null)
            return state;

        state = new DailyTotalsState { Id = 1 };
        _db.DailyTotalsState.Add(state);

        return state;
    }

    private async Task SaveWatermarkAsync(
        DailyTotalsState state,
        DateTimeOffset? highWater,
        DateTimeOffset? messagesHighWater,
        CancellationToken ct)
    {
        // Never move the mark backwards: an empty fact log must not undo what a previous run
        // already folded in.
        if (highWater is not null && (state.ObservedThrough is null || highWater > state.ObservedThrough))
            state.ObservedThrough = highWater;

        if (messagesHighWater is not null && (state.MessagesStoredThrough is null || messagesHighWater > state.MessagesStoredThrough))
            state.MessagesStoredThrough = messagesHighWater;

        state.UpdatedAt = _clock.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The fact window, normalised: <c>lo</c> and <c>hi</c> as UTC wall-clock timestamps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Converted out of <c>timestamptz</c> deliberately. Day truncation and <c>+ interval '1
    /// day'</c> on a <c>timestamptz</c> are evaluated in the session's time zone, so the same
    /// query would bucket facts differently depending on a connection setting. In plain
    /// <c>timestamp</c> space, after one explicit <c>AT TIME ZONE 'UTC'</c>, a day is always
    /// twenty-four hours and always starts at UTC midnight.
    /// </para>
    /// <para>
    /// <c>hi</c> is clamped upwards to <c>lo</c>: an <c>occurred_before</c> earlier than
    /// <c>occurred_at</c> is nonsense, and a negative window would produce negative weights.
    /// </para>
    /// </remarks>
    private static string WindowedFacts(
        string types,
        bool observedSince,
        bool boundedDays = false,
        string extraFilter = "")
    {
        var observed = observedSince ? "AND e.observed_at > @since" : string.Empty;

        // Narrows the scan to the days being recomputed. The spread below still decides which day
        // each fact lands on; this only keeps the query off the rest of the partition set.
        var bounded = boundedDays
            ? "AND e.occurred_at < @to AND GREATEST(COALESCE(e.occurred_before, e.occurred_at), e.occurred_at) >= @from"
            : string.Empty;

        return $"""
            SELECT e.actor_platform,
                   e.actor_id,
                   e.subject_id,
                   e.world_id,
                   (e.occurred_at AT TIME ZONE 'UTC') AS lo,
                   (GREATEST(COALESCE(e.occurred_before, e.occurred_at), e.occurred_at) AT TIME ZONE 'UTC') AS hi
            FROM modbot_event e
            WHERE e.type = ANY({types}) {observed} {bounded} {extraFilter}
            """;
    }

    /// <summary>
    /// The dimension for actor-broken-down metrics, as SQL.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="DailyTotalDimensions.Label"/> so that the string this query writes and
    /// the string C# looks rows up by cannot drift apart -- a purge searching for
    /// <c>vrchat:usr_...</c> while the job wrote something else would silently find nothing.
    /// </remarks>
    private static readonly string ActorDimensionSql = BuildActorDimensionSql();

    private static string BuildActorDimensionSql()
    {
        var cases = string.Join(
            " ",
            Enum.GetValues<FactPlatform>().Select(p =>
                $"WHEN {(short)p} THEN '{DailyTotalDimensions.Label(p)}'"));

        return $"(CASE s.actor_platform {cases} ELSE 'unknown' END || ':' || s.actor_id)";
    }

    private static string[] TypeValues(IEnumerable<string> types) => types.ToArray();

    /// <summary>
    /// UTC midnight beginning <paramref name="day"/>, as a <c>timestamptz</c> bound.
    /// </summary>
    /// <remarks>
    /// A <c>DateTimeOffset</c> and not a bare <c>DateTime</c>: compared against a
    /// <c>timestamptz</c> column, a timestamp without a zone is interpreted in the session's time
    /// zone, and the scan bound would then move with a connection setting.
    /// </remarks>
    private static DateTimeOffset DayStart(DateOnly day)
        => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static NpgsqlParameter Param(string name, object? value)
        => new(name, value ?? DBNull.Value);

    // EF1002 is knowingly suppressed on the raw-SQL helpers below. The statements are assembled
    // from constants and from the metric registry in this assembly -- table names, a dimension
    // expression, a fixed CTE -- and every value that comes from data travels as an NpgsqlParameter.
    // They cannot be expressed through ExecuteSqlInterpolated because the varying part is SQL, not
    // a value.

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params NpgsqlParameter[] parameters)
    {
#pragma warning disable EF1002
        return await _db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
#pragma warning restore EF1002
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        string sql,
        IReadOnlyList<NpgsqlParameter> parameters,
        CancellationToken ct)
    {
#pragma warning disable EF1002
        return await _db.Database.SqlQueryRaw<T>(sql, parameters.ToArray()).ToListAsync(ct);
#pragma warning restore EF1002
    }
}

/// <param name="RowsWritten">Daily total rows written, for logging. Not a metric.</param>
/// <param name="From">First day recomputed, or null when there was nothing to do.</param>
/// <param name="To">Last day recomputed.</param>
public readonly record struct DailyTotalsRunResult(int RowsWritten, DateOnly? From, DateOnly? To);
