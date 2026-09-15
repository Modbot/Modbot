using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Live;

namespace Modbot.AI.Alerts;

/// <summary>One open room, as the two room watchers need it.</summary>
/// <param name="Where">The world's name, or its id when Modbot has no name for it.</param>
/// <param name="Watched">Whether a moderator's client is in it.</param>
public sealed record OpenRoom(Guid Id, string Where, decimal People, bool Watched);

/// <summary>
/// Everything one pass of the watchers reads, gathered in a handful of queries.
/// </summary>
/// <param name="ByType">
/// For each fact type, how many happened in the window ending now and in the same window on each of
/// the last fourteen days. Index 0 is the window just ended.
/// </param>
/// <param name="NewAccounts">The same, counting only joiners whose VRChat account is under a month old.</param>
/// <param name="RoomPeaks">The most people in one room, for each group room opened in the last fourteen days.</param>
/// <param name="Rooms">The group's open rooms right now.</param>
/// <param name="ActiveWeeks">
/// Active members for the week just ended and the four weeks before it, newest first. Empty when
/// the weekly watcher was not due.
/// </param>
public sealed record AlertReadings(
    IReadOnlyDictionary<string, IReadOnlyList<decimal>> ByType,
    IReadOnlyList<decimal> NewAccounts,
    IReadOnlyList<decimal> RoomPeaks,
    IReadOnlyList<OpenRoom> Rooms,
    IReadOnlyList<decimal> ActiveWeeks)
{
    /// <summary>The counts for one watcher: its fact types added together, window by window.</summary>
    public IReadOnlyList<decimal> Windows(IReadOnlyList<string> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        var windows = new decimal[AlertFigureReader.HistoryDays + 1];

        foreach (var type in types)
        {
            if (!ByType.TryGetValue(type, out var counts))
                continue;

            for (var i = 0; i < windows.Length && i < counts.Count; i++)
                windows[i] += counts[i];
        }

        return windows;
    }
}

/// <summary>
/// Reads the figures the watchers judge, from Modbot's own tables (AI insights design §8.5).
/// </summary>
/// <remarks>
/// <para>
/// Four reads a pass at most, and every one of them is skipped when no watcher that is on wants it:
/// </para>
/// <list type="bullet">
/// <item><description>one grouped count over the fact log for the types the watchers that are on
/// count, over fifteen days, on the <c>(type, occurred_at)</c> index;</description></item>
/// <item><description>the same again for joiners with a new VRChat account, joined to
/// <c>vrchat_user</c>;</description></item>
/// <item><description>the open group rooms and their head counts, and who is watching them, only
/// while a room watcher is on;</description></item>
/// <item><description>active members per week from the daily totals, only once a UTC day, because
/// the figure is whole days and cannot change inside one.</description></item>
/// </list>
/// <para>
/// Nothing here selects a person's id or display name except where the live-room reader needs one
/// to answer "is anybody watching", and that answer is a yes or no by the time it leaves.
/// </para>
/// </remarks>
public sealed class AlertFigureReader(ModbotContext db)
{
    /// <summary>How many earlier days a window is compared with.</summary>
    public const int HistoryDays = 14;

    /// <summary>How many whole weeks before this one the weekly watcher compares with.</summary>
    public const int HistoryWeeks = 4;

    /// <summary>How long a window is.</summary>
    public static readonly TimeSpan WindowLength = TimeSpan.FromHours(1);

    /// <summary>How long a room's own peaks are collected over, for "usually this full".</summary>
    public static readonly TimeSpan RoomHistory = TimeSpan.FromDays(HistoryDays);

    /// <summary>What "a new account" means: made less than this long before the person joined.</summary>
    public static readonly TimeSpan NewAccountAge = TimeSpan.FromDays(30);

    /// <summary>The metrics that say somebody was active, the same two the My Server page uses.</summary>
    public static readonly string[] ActivityMetrics =
        [DailyTotalMetrics.DiscordMemberMessages, DailyTotalMetrics.DiscordMemberVoiceMinutes];

    /// <summary>
    /// Reads only what the watchers in <paramref name="on"/> need. A deployment watching one thing
    /// pays for one thing.
    /// </summary>
    /// <param name="wantWeeks">
    /// Whether to read the weekly figure. It is whole UTC days and cannot change inside one, so the
    /// caller asks for it once a day rather than every pass.
    /// </param>
    public async Task<AlertReadings> ReadAsync(
        AlertWindows windows, IReadOnlySet<string> on, bool wantWeeks, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(on);

        var types = AlertWatcherRules.All
            .Where(r => on.Contains(r.Watcher))
            .SelectMany(r => r.Types)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var byType = types.Length > 0
            ? await ByTypeAsync(windows.End, types, ct)
            : new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal);

        var newAccounts = on.Contains(AlertWatchers.NewAccounts)
            ? await NewAccountsAsync(windows.End, ct)
            : [];

        var (peaks, rooms) = on.Overlaps(AlertWatcherRules.RoomWatchers)
            ? await RoomsAsync(windows.End, ct)
            : ([], []);

        var weeks = wantWeeks ? await ActiveWeeksAsync(windows.LastWholeDay, ct) : [];

        return new AlertReadings(byType, newAccounts, peaks, rooms, weeks);
    }

    /// <summary>
    /// The fact types asked for, bucketed by how many days back their window is.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<decimal>>> ByTypeAsync(
        DateTimeOffset end, string[] types, CancellationToken ct)
    {
        // The day bucket and the "inside the window" test are both arithmetic on occurred_at, so
        // the range below it is what picks the rows, and that range is the index and the partition
        // pruning. Fifteen days of a handful of types is a small read.
        const string Sql = """
            SELECT e.type,
                   (floor(extract(epoch FROM (@anchor - e.occurred_at)) / 86400))::int AS days_ago,
                   count(*)::bigint AS n
            FROM modbot_event e
            WHERE e.type = ANY(@types)
              AND e.occurred_at >= @from
              AND e.occurred_at < @anchor
              AND mod((extract(epoch FROM (@anchor - e.occurred_at)))::bigint, 86400) < @seconds
            GROUP BY 1, 2
            """;

        var rows = await ReadAsync(
            Sql,
            r => (Type: r.GetString(0), DaysAgo: r.GetInt32(1), Count: (decimal)r.GetInt64(2)),
            ct,
            ("anchor", end),
            ("from", end - TimeSpan.FromDays(HistoryDays + 1)),
            ("types", types),
            ("seconds", (long)WindowLength.TotalSeconds));

        var byType = new Dictionary<string, IReadOnlyList<decimal>>(StringComparer.Ordinal);

        foreach (var group in rows.GroupBy(r => r.Type, StringComparer.Ordinal))
            byType[group.Key] = Buckets(group.Select(r => (r.DaysAgo, r.Count)));

        return byType;
    }

    /// <summary>Joiners whose VRChat account was made less than a month before they joined.</summary>
    private async Task<IReadOnlyList<decimal>> NewAccountsAsync(DateTimeOffset end, CancellationToken ct)
    {
        const string Sql = """
            SELECT (floor(extract(epoch FROM (@anchor - e.occurred_at)) / 86400))::int AS days_ago,
                   count(*)::bigint AS n
            FROM modbot_event e
            JOIN vrchat_user u ON u.user_id = e.subject_id
            WHERE e.type = @type
              AND e.occurred_at >= @from
              AND e.occurred_at < @anchor
              AND mod((extract(epoch FROM (@anchor - e.occurred_at)))::bigint, 86400) < @seconds
              AND u.date_joined IS NOT NULL
              AND u.date_joined > (e.occurred_at - make_interval(days => @age))::date
            GROUP BY 1
            """;

        var rows = await ReadAsync(
            Sql,
            r => (DaysAgo: r.GetInt32(0), Count: (decimal)r.GetInt64(1)),
            ct,
            ("anchor", end),
            ("from", end - TimeSpan.FromDays(HistoryDays + 1)),
            ("type", FactType.MemberJoined),
            ("seconds", (long)WindowLength.TotalSeconds),
            ("age", (int)NewAccountAge.TotalDays));

        return Buckets(rows);
    }

    /// <summary>The group's open rooms, and how full rooms here usually get.</summary>
    private async Task<(IReadOnlyList<decimal> Peaks, IReadOnlyList<OpenRoom> Rooms)> RoomsAsync(
        DateTimeOffset end, CancellationToken ct)
    {
        var groupId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.ManagedGroupId)
            .FirstOrDefaultAsync(ct);

        if (groupId is not { Length: > 0 })
            return ([], []);

        var peaks = await db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == groupId && i.OpenedAt >= end - RoomHistory && i.PeakUserCount != null)
            .Select(i => (decimal)i.PeakUserCount!.Value)
            .ToListAsync(ct);

        var open = await db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == groupId && i.SeenInGroupList && i.ClosedAt == null && i.HeadCount != null)
            .OrderByDescending(i => i.HeadCount)
            .Take(20)
            .ToListAsync(ct);

        if (open.Count == 0)
            return (peaks, []);

        var worldIds = open.Select(r => r.WorldId).Distinct(StringComparer.Ordinal).ToList();
        var names = await db.VRChatWorlds.AsNoTracking()
            .Where(w => worldIds.Contains(w.WorldId))
            .ToDictionaryAsync(w => w.WorldId, w => w.Name, StringComparer.Ordinal, ct);

        var people = await new RoomPeopleReader(db).ForRoomsAsync(open, ct);

        var rooms = open
            .Select(r => new OpenRoom(
                r.Id,
                names.GetValueOrDefault(r.WorldId) ?? r.WorldId,
                r.HeadCount!.Value,
                people.TryGetValue(r.Id, out var p) && p.IsWatched))
            .ToList();

        return (peaks, rooms);
    }

    /// <summary>
    /// Active members for the week ending <paramref name="lastWholeDay"/> and the four weeks before.
    /// </summary>
    /// <remarks>
    /// Distinct people across seven days of per-person daily total rows, never a sum of daily
    /// counts, which is the same thing the My Server page means by active.
    /// </remarks>
    private async Task<IReadOnlyList<decimal>> ActiveWeeksAsync(DateOnly lastWholeDay, CancellationToken ct)
    {
        const string Sql = """
            SELECT (@lastDay::date - t.day) / 7 AS weeks_ago,
                   count(DISTINCT t.dimension)::bigint AS n
            FROM modbot_daily_total t
            WHERE t.metric = ANY(@metrics)
              AND t.dimension <> ''
              AND t.value > 0
              AND t.day <= @lastDay
              AND t.day > @lastDay::date - @days
            GROUP BY 1
            ORDER BY 1
            """;

        var rows = await ReadAsync(
            Sql,
            r => (WeeksAgo: r.GetInt32(0), Count: (decimal)r.GetInt64(1)),
            ct,
            ("lastDay", lastWholeDay),
            ("metrics", ActivityMetrics),
            ("days", (HistoryWeeks + 1) * 7));

        var weeks = new decimal[HistoryWeeks + 1];

        foreach (var (weeksAgo, count) in rows)
        {
            if (weeksAgo >= 0 && weeksAgo < weeks.Length)
                weeks[weeksAgo] = count;
        }

        return weeks;
    }

    private static IReadOnlyList<decimal> Buckets(IEnumerable<(int DaysAgo, decimal Count)> rows)
    {
        var buckets = new decimal[HistoryDays + 1];

        foreach (var (daysAgo, count) in rows)
        {
            if (daysAgo >= 0 && daysAgo < buckets.Length)
                buckets[daysAgo] = count;
        }

        return buckets;
    }

    /// <summary>
    /// Runs a multi-column statement. ADO because these project several columns, and EF's
    /// single-column helper does not fit; every value travels as a parameter.
    /// </summary>
    private async Task<IReadOnlyList<T>> ReadAsync<T>(
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
}
