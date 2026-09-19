using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Analytics.Instances;

/// <summary>
/// How many people were in the group's instances, moment by moment, rebuilt from VRChat's own head
/// counts.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why head counts and not presence facts.</strong> Everything else about population on the
/// analytics pages comes from the companion's presence reports, and those exist only while a
/// moderator's client is standing in the instance — so a busy night nobody with the client attended
/// reads as empty. <c>instance_head_count</c> is the other source: <c>n_users</c> from each open
/// group instance's own page, read about every thirty seconds by <c>InstanceHeadCountSync</c>, with
/// the group list's number as the fallback. No moderator is involved, so it covers the instances
/// presence cannot see. It had never been read by analytics before this; the Live page and the
/// Discord card were its only readers.
/// </para>
/// <para>
/// <strong>A row per change, so the series is a staircase.</strong> <c>HeadCounts.Record</c> writes
/// a row only when the number actually moves, which is what keeps the table small and what means
/// the value between two rows is the earlier row's, all the way to the next one. Rebuilding the
/// total is therefore a sum of changes: each reading contributes its difference from that
/// instance's previous reading, an instance's first reading contributes the whole count, and an
/// instance's end contributes its last count back out again.
/// </para>
/// <para>
/// <strong>What the window costs.</strong> The heavy half is one index range over
/// <c>instance_head_count</c> on <c>counted_at</c> — every reading inside the window and nothing
/// before it. The seed (instances already open when the window began) is a separate, tiny read:
/// instances the group opened in the <see cref="VRChatInstance.CountsAsNewAfter"/> before the
/// window and still open at its start, each asked for its last reading through the
/// <c>(instance_id, counted_at)</c> index. That bound is the same rule that decides an instance is
/// finished, so the only thing it can miss is an instance open for longer than that whose count
/// never changed inside the whole window — which enters the series at its next reading instead of
/// at the window's first moment.
/// </para>
/// </remarks>
public static class InstanceActivitySql
{
    /// <summary>
    /// The moment-by-moment total, as a set of common table expressions ending in <c>running</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>running</c> has one row per moment the total changed, carrying <c>at</c> (a
    /// <c>timestamptz</c>), <c>at_utc</c> (the same moment as a bare UTC timestamp, for day and
    /// hour arithmetic), <c>id</c>, <c>people</c> and <c>instances</c>.
    /// </para>
    /// <para>
    /// The seed rows are folded into one row at the window's first moment rather than left one per
    /// instance. Several rows at the same instant are applied one at a time by the running sum, and
    /// a reader looking at the first of them would see a total that was never true; with one row
    /// there is nothing to see halfway through. Ends may still share an instant — the sweep closes
    /// several instances on one clock reading — but those only ever take the total down, and a
    /// momentary dip cannot invent a peak.
    /// </para>
    /// </remarks>
    public const string Running = """
        WITH open_before AS (
            SELECT i.id
            FROM vrchat_instance i
            WHERE i.group_id = @group
              AND i.opened_at >= @seedFrom AND i.opened_at < @from
              AND COALESCE(i.closed_at, i.last_seen_at) >= @from
        ),
        seed AS (
            SELECT o.id AS instance_id, r.head_count
            FROM open_before o
            CROSS JOIN LATERAL (
                SELECT h.head_count
                FROM instance_head_count h
                WHERE h.instance_id = o.id AND h.counted_at < @from
                ORDER BY h.counted_at DESC, h.id DESC
                LIMIT 1
            ) r
        ),
        steps AS (
            SELECT s.instance_id, @from AS at, 0::bigint AS id, s.head_count, true AS is_seed
            FROM seed s
            UNION ALL
            SELECT h.instance_id, h.counted_at, h.id, h.head_count, false
            FROM instance_head_count h
            JOIN vrchat_instance i ON i.id = h.instance_id AND i.group_id = @group
            WHERE h.counted_at >= @from AND h.counted_at < @to
        ),
        deltas AS (
            SELECT st.at, st.id, st.is_seed,
                   st.head_count - COALESCE(LAG(st.head_count) OVER w, 0) AS people_change,
                   CASE WHEN LAG(st.head_count) OVER w IS NULL THEN 1 ELSE 0 END AS instance_change
            FROM steps st
            WINDOW w AS (PARTITION BY st.instance_id ORDER BY st.at, st.id)
        ),
        seed_total AS (
            SELECT @from AS at, 0::bigint AS id,
                   COALESCE(SUM(s.head_count), 0)::bigint AS people_change,
                   COUNT(*)::bigint AS instance_change
            FROM seed s
            HAVING COUNT(*) > 0
        ),
        last_of AS (
            SELECT DISTINCT ON (st.instance_id) st.instance_id, st.at, st.head_count
            FROM steps st
            ORDER BY st.instance_id, st.at DESC, st.id DESC
        ),
        end_rows AS (
            SELECT GREATEST(COALESCE(v.closed_at, v.last_seen_at), l.at) AS at, l.head_count
            FROM last_of l
            JOIN vrchat_instance v ON v.id = l.instance_id
        ),
        stream AS (
            SELECT at, id, people_change::bigint AS people_change, instance_change::bigint AS instance_change
            FROM deltas WHERE NOT is_seed
            UNION ALL
            SELECT at, id, people_change, instance_change FROM seed_total
            UNION ALL
            SELECT at, 9223372036854775807::bigint, -head_count::bigint, -1::bigint
            FROM end_rows WHERE at >= @from AND at < @to
        ),
        running AS (
            SELECT at, (at AT TIME ZONE 'UTC') AS at_utc, id,
                   SUM(people_change) OVER w AS people,
                   SUM(instance_change) OVER w AS instances
            FROM stream
            WINDOW w AS (ORDER BY at, id ROWS UNBOUNDED PRECEDING)
        )
        """;

    /// <summary>
    /// How far before a window the seed looks for instances that were already open.
    /// </summary>
    /// <remarks>
    /// The same stretch after which an unseen instance counts as finished, so the seed reaches
    /// exactly as far back as an instance can plausibly still be running.
    /// </remarks>
    public static DateTimeOffset SeedFrom(DateTimeOffset from) => from - VRChatInstance.CountsAsNewAfter;
}
