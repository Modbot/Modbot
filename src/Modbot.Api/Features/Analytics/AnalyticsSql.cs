using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Analytics;

/// <summary>
/// The pieces every analytics query shares: running a multi-column statement, the window bounds,
/// the daily totals for a window, and putting names to ids.
/// </summary>
/// <remarks>
/// <para>
/// ADO rather than <c>SqlQueryRaw</c> because these statements project several columns, and the
/// EF helper's single-column <c>Value</c> shape does not fit. Every value travels as a parameter;
/// the only interpolated fragments are constants from this assembly.
/// </para>
/// <para>
/// One class rather than four copies so that a fix to, say, how a <c>timestamptz</c> bound is
/// built lands on every page at once. The old metrics endpoint carried its own copy of this and
/// would have been the fifth.
/// </para>
/// </remarks>
public sealed class AnalyticsSql(ModbotContext db)
{
    public ModbotContext Db => db;

    /// <summary>The three fact types a desktop client reports about who is in an instance.</summary>
    public static readonly string[] PresenceTypes =
    [
        FactType.InstanceJoined,
        FactType.InstancePresenceObserved,
        FactType.InstanceLeft,
    ];

    /// <summary>The two presence types that mean "this person is here".</summary>
    public static readonly string[] ArrivalTypes =
    [
        FactType.InstanceJoined,
        FactType.InstancePresenceObserved,
    ];

    public async Task<IReadOnlyList<T>> ReadAsync<T>(
        string sql,
        Func<DbDataReader, T> read,
        CancellationToken ct,
        params (string Name, object? Value)[] parameters)
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
                parameter.Value = value ?? DBNull.Value;
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

    /// <summary>
    /// Every daily total row inside the window for the metrics named.
    /// </summary>
    public async Task<IReadOnlyList<DailyTotalRow>> DailyTotalsAsync(
        DateOnly from,
        DateOnly to,
        IReadOnlyCollection<string> metrics,
        CancellationToken ct)
    {
        var names = metrics.ToArray();

        return await db.DailyTotals.AsNoTracking()
            .Where(r => r.Day >= from && r.Day <= to && names.Contains(r.Metric))
            .Select(r => new DailyTotalRow(r.Day, r.Metric, r.Dimension, r.Value))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Puts a display name to each id, where any fact recorded one.
    /// </summary>
    /// <remarks>
    /// Two places a name can be: <c>actorDisplayName</c> on an audit-log fact the person caused,
    /// and <c>displayName</c> on a client's presence report about them. The most recent of either
    /// wins, because display names change and the latest is the one a moderator will recognise.
    /// Both lookups sit on indexed columns, so this is two index scans and not a payload search.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, string>> NamesAsync(
        IReadOnlyCollection<string> ids,
        CancellationToken ct)
    {
        if (ids.Count == 0)
            return new Dictionary<string, string>(StringComparer.Ordinal);

        const string Sql = """
            SELECT DISTINCT ON (n.id) n.id, n.name
            FROM (
                SELECT e.actor_id AS id, e.data->>'actorDisplayName' AS name, e.occurred_at
                FROM modbot_event e
                WHERE e.actor_id = ANY(@ids) AND e.data ? 'actorDisplayName'
                UNION ALL
                SELECT e.subject_id, e.data->>'displayName', e.occurred_at
                FROM modbot_event e
                WHERE e.subject_id = ANY(@ids) AND e.data ? 'displayName'
            ) n
            WHERE n.name IS NOT NULL AND n.name <> ''
            ORDER BY n.id, n.occurred_at DESC
            """;

        var rows = await ReadAsync(
            Sql,
            r => (Id: r.GetString(0), Name: r.GetString(1)),
            ct,
            ("ids", ids.Distinct(StringComparer.Ordinal).ToArray()));

        return rows.ToDictionary(r => r.Id, r => r.Name, StringComparer.Ordinal);
    }

    /// <summary>
    /// Splits a daily total dimension into platform and id. The id half is opaque — split on the
    /// first colon and never validate either side (spec 3.1.1).
    /// </summary>
    public static (string Platform, string Id) SplitDimension(string dimension)
    {
        var colon = dimension.IndexOf(':', StringComparison.Ordinal);

        return colon < 0
            ? ("unknown", dimension)
            : (dimension[..colon], dimension[(colon + 1)..]);
    }

    /// <summary>UTC midnight beginning <paramref name="day"/>, as a <c>timestamptz</c> bound.</summary>
    /// <remarks>
    /// A <c>DateTimeOffset</c> and not a bare <c>DateTime</c>: compared against a
    /// <c>timestamptz</c> column, a timestamp without a zone is interpreted in the session's time
    /// zone, and the bound would then move with a connection setting.
    /// </remarks>
    public static DateTimeOffset DayStart(DateOnly day)
        => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    /// <summary>The exclusive upper bound of a window ending on <paramref name="to"/>.</summary>
    public static DateTimeOffset DayEnd(DateOnly to) => DayStart(to.AddDays(1));

    public static DateOnly DayOf(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);

    public static DateOnly DayOf(DbDataReader reader, int ordinal)
        => DateOnly.FromDateTime(reader.GetDateTime(ordinal));

    public static DateTimeOffset InstantOf(DbDataReader reader, int ordinal)
        => reader.GetFieldValue<DateTimeOffset>(ordinal);

    public static DateTimeOffset? InstantOrNull(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);
}

public sealed record DailyTotalRow(DateOnly Day, string Metric, string Dimension, decimal Value);

/// <summary>Shorthand over a window's daily total rows.</summary>
public static class DailyTotalRows
{
    /// <summary>The undimensioned series for one metric, in day order.</summary>
    public static List<DayValue> Series(this IReadOnlyList<DailyTotalRow> rows, string metric) => rows
        .Where(r => r.Metric == metric && r.Dimension.Length == 0)
        .OrderBy(r => r.Day)
        .Select(r => new DayValue(r.Day, r.Value))
        .ToList();

    /// <summary>One metric summed per day across every dimension, in day order.</summary>
    public static List<DayValue> SummedPerDay(this IReadOnlyList<DailyTotalRow> rows, IReadOnlySet<string> metrics) => rows
        .Where(r => metrics.Contains(r.Metric))
        .GroupBy(r => r.Day)
        .OrderBy(g => g.Key)
        .Select(g => new DayValue(g.Key, g.Sum(r => r.Value)))
        .ToList();

    public static decimal Total(this IReadOnlyList<DailyTotalRow> rows, string metric)
        => rows.Where(r => r.Metric == metric && r.Dimension.Length == 0).Sum(r => r.Value);
}
