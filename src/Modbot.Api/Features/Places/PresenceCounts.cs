using System.Data.Common;
using Modbot.Api.Features.Analytics;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Places;

/// <summary>
/// How long people were seen somewhere, how many of them there were, and when.
/// </summary>
/// <remarks>
/// <para>
/// One copy of the session sum, shared by the Worlds page, the world popup, the instance popup
/// and one person's own figures. It used to live inside <c>WorldsAnalyticsQuery</c> as a private
/// method; the popups need the same arithmetic for one world, one room and one person, and a
/// second copy of it would be a second answer to "how long was this person here" that could
/// disagree with the first.
/// </para>
/// <para>
/// <strong>A person's presence in a room is the last thing said about them there.</strong> An
/// arrival makes them present, a leave makes them absent, and a repeat of either changes nothing
/// — so each report that changes the state opens a session and the next state change closes it. A
/// session nobody saw the end of closes at the last report from that room, which is the last
/// moment anything is known. Time nobody was watching is never counted: presence facts exist only
/// while a moderator's companion is in the room.
/// </para>
/// <para>
/// The world and room filters are written into the statement rather than passed as optional
/// parameters, because they are part of the key the sessions are grouped by and narrowing on them
/// cannot change any session's shape. The person filter is applied at the end instead: narrowing
/// to one person first would make "the last report from that room" mean "the last report about
/// that person", and every session they were not seen to leave would close early.
/// </para>
/// </remarks>
public sealed class PresenceCounts(ModbotContext db)
{
    private readonly AnalyticsSql _sql = new(db);

    /// <summary>
    /// Sessions, from the presence reports, narrowed to a world or a room where asked.
    /// </summary>
    /// <remarks>
    /// The window bounds are always real instants — all of recorded history is expressed as the
    /// widest pair rather than as nulls, so no parameter here is ever typeless.
    /// </remarks>
    private static string Sessions(bool byWorld, bool byRoom) => $"""
        WITH p AS (
            SELECT e.world_id, e.instance_id, e.subject_id, e.occurred_at, e.id,
                   CASE WHEN e.type = @leave THEN 0 ELSE 1 END AS here
            FROM modbot_event e
            WHERE e.type = ANY(@presence)
              AND e.occurred_at >= @from AND e.occurred_at < @to
              AND e.world_id IS NOT NULL AND e.instance_id IS NOT NULL
              {(byWorld ? "AND e.world_id = @world" : "")}
              {(byRoom ? "AND e.instance_id = @room" : "")}
        ),
        changes AS (
            SELECT p.*,
                   p.here - COALESCE(LAG(p.here) OVER (
                       PARTITION BY p.world_id, p.instance_id, p.subject_id
                       ORDER BY p.occurred_at, p.id), 0) AS change,
                   MAX(p.occurred_at) OVER (PARTITION BY p.world_id, p.instance_id) AS last_report
            FROM p
        ),
        sessions AS (
            SELECT world_id, instance_id, subject_id, change,
                   occurred_at AS started,
                   COALESCE(LEAD(occurred_at) OVER (
                       PARTITION BY world_id, instance_id, subject_id
                       ORDER BY occurred_at, id), last_report) AS ended
            FROM changes
            WHERE change <> 0
        )
        """;

    private const string Totals = """
        SELECT (SUM(EXTRACT(EPOCH FROM (ended - started))) / 60.0)::numeric AS minutes,
               COUNT(DISTINCT subject_id)::int AS visitors,
               COUNT(*)::int AS arrivals,
               MAX(ended) AS last_seen_at
        FROM sessions
        WHERE change = 1
        """;

    /// <summary>Minutes seen, distinct people and arrivals for every world in the window.</summary>
    public async Task<IReadOnlyDictionary<string, PlaceCounts>> PerWorldAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var sql = $"""
            {Sessions(byWorld: false, byRoom: false)}
            SELECT world_id,
                   (SUM(EXTRACT(EPOCH FROM (ended - started))) / 60.0)::numeric AS minutes,
                   COUNT(DISTINCT subject_id)::int AS visitors,
                   COUNT(*)::int AS arrivals,
                   MAX(ended) AS last_seen_at
            FROM sessions
            WHERE change = 1
            GROUP BY world_id
            """;

        var rows = await _sql.ReadAsync(
            sql,
            r => (WorldId: r.GetString(0), Counts: ReadCounts(r, 1)),
            ct,
            Window(AnalyticsSql.DayStart(from), AnalyticsSql.DayEnd(to)));

        return rows.ToDictionary(r => r.WorldId, r => r.Counts, StringComparer.Ordinal);
    }

    /// <summary>Everything recorded about one world, over all of recorded history.</summary>
    public async Task<PlaceCounts> ForWorldAsync(string worldId, CancellationToken ct)
    {
        var sql = $"{Sessions(byWorld: true, byRoom: false)}\n{Totals}";

        var rows = await _sql.ReadAsync(
            sql, r => ReadCounts(r, 0), ct, [.. Everything(), ("world", worldId)]);

        return rows.Count > 0 ? rows[0] : PlaceCounts.Nothing;
    }

    /// <summary>
    /// Everything recorded about one room, bounded to the stretch that room was open.
    /// </summary>
    /// <remarks>
    /// The bounds matter more here than anywhere else: VRChat hands the same room number out
    /// again after a room closes, so presence facts keyed on world and number alone would blend
    /// last Tuesday's evening into tonight's. The room's own open and close times tell them apart
    /// (<see cref="VRChatInstance"/>).
    /// </remarks>
    public async Task<PlaceCounts> ForRoomAsync(Room room, CancellationToken ct)
    {
        var sql = $"{Sessions(byWorld: true, byRoom: true)}\n{Totals}";
        var rows = await _sql.ReadAsync(sql, r => ReadCounts(r, 0), ct, RoomParameters(room));

        return rows.Count > 0 ? rows[0] : PlaceCounts.Nothing;
    }

    /// <summary>Who was seen in one room, longest first.</summary>
    public async Task<IReadOnlyList<PersonSeen>> PeopleInRoomAsync(Room room, CancellationToken ct)
    {
        var sql = $"""
            {Sessions(byWorld: true, byRoom: true)}
            SELECT subject_id,
                   (SUM(EXTRACT(EPOCH FROM (ended - started))) / 60.0)::numeric AS minutes,
                   COUNT(*)::int AS arrivals,
                   MIN(started) AS first_seen_at,
                   MAX(ended) AS last_seen_at
            FROM sessions
            WHERE change = 1
            GROUP BY subject_id
            ORDER BY minutes DESC, subject_id
            """;

        return await _sql.ReadAsync(
            sql,
            r => new PersonSeen(
                r.GetString(0),
                null,
                Math.Round(r.GetDecimal(1), 1),
                r.GetInt32(2),
                AnalyticsSql.InstantOf(r, 3),
                AnalyticsSql.InstantOf(r, 4)),
            ct,
            RoomParameters(room));
    }

    /// <summary>
    /// One person's own figures, over all of recorded history: time seen, where, and how often.
    /// </summary>
    public async Task<PersonCounts> ForPersonAsync(string userId, CancellationToken ct)
    {
        var sql = $"""
            {Sessions(byWorld: false, byRoom: false)}
            SELECT (SUM(EXTRACT(EPOCH FROM (ended - started))) / 60.0)::numeric AS minutes,
                   COUNT(DISTINCT world_id)::int AS worlds,
                   COUNT(DISTINCT (world_id, instance_id))::int AS rooms,
                   COUNT(*)::int AS arrivals,
                   MIN(started) AS first_seen_at,
                   MAX(ended) AS last_seen_at
            FROM sessions
            WHERE change = 1 AND subject_id = @subject
            """;

        var rows = await _sql.ReadAsync(
            sql,
            r => r.IsDBNull(0)
                ? PersonCounts.Nothing
                : new PersonCounts(
                    Math.Round(r.GetDecimal(0), 1),
                    r.GetInt32(1),
                    r.GetInt32(2),
                    r.GetInt32(3),
                    AnalyticsSql.InstantOrNull(r, 4),
                    AnalyticsSql.InstantOrNull(r, 5)),
            ct,
            [.. Everything(), ("subject", userId)]);

        return rows.Count > 0 ? rows[0] : PersonCounts.Nothing;
    }

    /// <summary>How many presence reports the window holds — below a handful, the figures are thin.</summary>
    public async Task<long> ReportsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string Sql = """
            SELECT COUNT(*) FROM modbot_event e
            WHERE e.type = ANY(@presence) AND e.occurred_at >= @from AND e.occurred_at < @to
            """;

        var rows = await _sql.ReadAsync(Sql, r => r.GetInt64(0), ct,
            ("presence", AnalyticsSql.PresenceTypes),
            ("from", AnalyticsSql.DayStart(from)),
            ("to", AnalyticsSql.DayEnd(to)));

        return rows.Count > 0 ? rows[0] : 0;
    }

    private static PlaceCounts ReadCounts(DbDataReader r, int first)
        => r.IsDBNull(first)
            ? PlaceCounts.Nothing
            : new PlaceCounts(
                Math.Round(r.GetDecimal(first), 1),
                r.GetInt32(first + 1),
                r.GetInt32(first + 2),
                AnalyticsSql.InstantOrNull(r, first + 3));

    /// <summary>All of recorded history, expressed as instants rather than as an absent bound.</summary>
    private static (string Name, object? Value)[] Everything()
        => Window(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

    private static (string Name, object? Value)[] Window(DateTimeOffset from, DateTimeOffset to) =>
    [
        ("leave", FactType.InstanceLeft),
        ("presence", AnalyticsSql.PresenceTypes),
        ("from", from),
        ("to", to),
    ];

    private static (string Name, object? Value)[] RoomParameters(Room room) =>
    [
        // Inclusive of the moment the room opened and of the moment it was last known to exist:
        // a presence report stamped on either boundary belongs to this room.
        .. Window(room.OpenedAt, room.EndsAt.AddSeconds(1)),
        ("world", room.WorldId),
        ("room", room.Number),
    ];
}

/// <summary>
/// Which room, and over what stretch of time — the three things that tell one evening in a room
/// from another evening under the same number.
/// </summary>
/// <param name="Number">VRChat's own number for the room, which is what the fact log carries.</param>
/// <param name="EndsAt">When it closed, or the last moment it was known to exist.</param>
public readonly record struct Room(string WorldId, string Number, DateTimeOffset OpenedAt, DateTimeOffset EndsAt);
