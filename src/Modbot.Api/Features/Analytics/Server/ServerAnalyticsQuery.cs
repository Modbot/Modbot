using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Analytics.Server;

/// <summary>
/// "Is the Discord server healthy, and who keeps it going?" (M5 spec §6).
/// </summary>
/// <remarks>
/// <para>
/// Nearly everything is read from daily totals, which are kept forever: member counts, joins and
/// leaves, messages per day, channel and hour, voice minutes, moderation actions, and each person's
/// messages and voice minutes per day. Being active is having a row in either of the last two, so
/// "active this week" is the distinct people across seven days of rows, never a sum of daily counts.
/// </para>
/// <para>
/// Two things need more than a day's totals. New members who stayed pair each join fact with the
/// leaves after it, so they cover what the fact log still holds. Member health is about now, so it
/// reads the stored member list for who is in the server and ignores the window.
/// </para>
/// </remarks>
public sealed class ServerAnalyticsQuery(ModbotContext db)
{
    /// <summary>The spans new members are followed for.</summary>
    public static readonly int[] StaySpans = [7, 30];

    public const int TopCount = 20;

    private static readonly string[] ActivityMetrics =
    [
        DailyTotalMetrics.DiscordMemberMessages,
        DailyTotalMetrics.DiscordMemberVoiceMinutes,
    ];

    private readonly AnalyticsSql _sql = new(db);

    public async Task<ServerAnalytics> RunAsync(DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken ct = default)
    {
        var guildId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct);
        guildId = string.IsNullOrWhiteSpace(guildId) ? null : guildId.Trim();

        var totals = await _sql.DailyTotalsAsync(from, to,
        [
            DailyTotalMetrics.DiscordMembersCount,
            DailyTotalMetrics.DiscordMembersJoined,
            DailyTotalMetrics.DiscordMembersLeft,
            DailyTotalMetrics.DiscordMessages,
            DailyTotalMetrics.DiscordVoiceMinutes,
            DailyTotalMetrics.DiscordBans,
            DailyTotalMetrics.DiscordKicks,
            DailyTotalMetrics.DiscordTimeouts,
            DailyTotalMetrics.DiscordMessagesRemoved,
            DailyTotalMetrics.DiscordChannelMessages,
            DailyTotalMetrics.DiscordMessagesByHour,
            DailyTotalMetrics.DiscordMemberMessages,
            DailyTotalMetrics.DiscordMemberVoiceMinutes,
        ], ct);

        var today = AnalyticsSql.DayOf(now);

        return new ServerAnalytics(
            from,
            to,
            totals.Series(DailyTotalMetrics.DiscordMembersCount),
            totals.Series(DailyTotalMetrics.DiscordMembersJoined),
            totals.Series(DailyTotalMetrics.DiscordMembersLeft),
            totals.Series(DailyTotalMetrics.DiscordMessages),
            totals.Series(DailyTotalMetrics.DiscordVoiceMinutes),
            await ActiveAsync(from, to, ct),
            await ChannelsAsync(totals, guildId, ct),
            HourOfWeek(totals),
            await NewMembersAsync(from, to, now, ct),
            totals.Series(DailyTotalMetrics.DiscordBans),
            totals.Series(DailyTotalMetrics.DiscordKicks),
            totals.Series(DailyTotalMetrics.DiscordTimeouts),
            totals.Series(DailyTotalMetrics.DiscordMessagesRemoved),
            await ContributorsAsync(totals, guildId, ct),
            await HealthAsync(guildId, today, ct),
            await AnalyticsCoverageQuery.RunAsync(db, ct),
            now);
    }

    /// <summary>Distinct active people for each day of the window, and the week and thirty days ending on it.</summary>
    private async Task<IReadOnlyList<ActiveMembersDay>> ActiveAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        const string Sql = """
            WITH act AS (
                SELECT DISTINCT t.day, t.dimension
                FROM modbot_daily_total t
                WHERE t.metric = ANY(@metrics) AND t.day > @firstDay - 30 AND t.day <= @lastDay
            ),
            days AS (
                SELECT gs::date AS day
                FROM generate_series(@firstDay::date, @lastDay::date, interval '1 day') AS gs
            )
            SELECT d.day,
                   (SELECT COUNT(DISTINCT a.dimension) FROM act a WHERE a.day = d.day)::int,
                   (SELECT COUNT(DISTINCT a.dimension) FROM act a WHERE a.day > d.day - 7 AND a.day <= d.day)::int,
                   (SELECT COUNT(DISTINCT a.dimension) FROM act a WHERE a.day > d.day - 30 AND a.day <= d.day)::int
            FROM days d
            ORDER BY d.day
            """;

        return await _sql.ReadAsync(
            Sql,
            r => new ActiveMembersDay(AnalyticsSql.DayOf(r, 0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)),
            ct,
            ("metrics", ActivityMetrics),
            ("firstDay", from),
            ("lastDay", to));
    }

    private async Task<IReadOnlyList<ChannelMessages>> ChannelsAsync(
        IReadOnlyList<DailyTotalRow> totals, string? guildId, CancellationToken ct)
    {
        var top = totals
            .Where(r => r.Metric == DailyTotalMetrics.DiscordChannelMessages)
            .GroupBy(r => r.Dimension, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Messages: g.Sum(r => r.Value)))
            .OrderByDescending(c => c.Messages)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .Take(TopCount)
            .ToList();

        var ids = top.Select(c => c.Id).ToList();
        var names = await db.DiscordChannels.AsNoTracking()
            .Where(c => ids.Contains(c.ChannelId) && (guildId == null || c.GuildId == guildId))
            .ToDictionaryAsync(c => c.ChannelId, c => c.Name, StringComparer.Ordinal, ct);

        return top.Select(c => new ChannelMessages(c.Id, names.GetValueOrDefault(c.Id), c.Messages)).ToList();
    }

    private static MessageHours HourOfWeek(IReadOnlyList<DailyTotalRow> totals)
    {
        var buckets = new decimal[168];

        foreach (var row in totals.Where(r => r.Metric == DailyTotalMetrics.DiscordMessagesByHour))
        {
            if (!int.TryParse(row.Dimension, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) || hour is < 0 or > 23)
                continue;

            // Monday is 0, as on the Instances page.
            var weekday = ((int)row.Day.DayOfWeek + 6) % 7;
            buckets[weekday * 24 + hour] += row.Value;
        }

        return new MessageHours(buckets);
    }

    /// <summary>People who joined in the window long enough ago, and whether they stayed and kept talking.</summary>
    private async Task<IReadOnlyList<NewMembersStayed>> NewMembersAsync(DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken ct)
    {
        const string Sql = """
            WITH joins AS (
                SELECT e.subject_id, MIN(e.occurred_at) AS joined
                FROM modbot_event e
                WHERE e.type = @join AND e.subject_platform = @discord
                  AND e.occurred_at >= @from AND e.occurred_at < @to AND e.occurred_at <= @latest
                GROUP BY e.subject_id
            )
            SELECT COUNT(*)::int,
                   COUNT(*) FILTER (WHERE NOT EXISTS (
                       SELECT 1 FROM modbot_event g
                       WHERE g.subject_platform = @discord AND g.subject_id = j.subject_id
                         AND g.type = ANY(@gone)
                         AND g.occurred_at > j.joined AND g.occurred_at <= j.joined + @span))::int,
                   COUNT(*) FILTER (WHERE EXISTS (
                       SELECT 1 FROM modbot_daily_total t
                       WHERE t.metric = ANY(@metrics)
                         AND t.dimension = 'discord:' || j.subject_id
                         AND t.day >= (j.joined AT TIME ZONE 'UTC')::date + @spanDays
                         AND t.day < (j.joined AT TIME ZONE 'UTC')::date + @spanDays + 7))::int
            FROM joins j
            """;

        var result = new List<NewMembersStayed>();

        foreach (var days in StaySpans)
        {
            var rows = await _sql.ReadAsync(
                Sql,
                r => (Joined: r.GetInt32(0), Here: r.GetInt32(1), Active: r.GetInt32(2)),
                ct,
                ("join", FactType.DiscordMemberJoined),
                ("discord", (short)FactPlatform.Discord),
                ("gone", new[] { FactType.DiscordMemberLeft, FactType.DiscordMemberKicked, FactType.DiscordMemberBanned }),
                ("from", AnalyticsSql.DayStart(from)),
                ("to", AnalyticsSql.DayEnd(to)),
                ("latest", now.AddDays(-days)),
                ("span", TimeSpan.FromDays(days)),
                ("spanDays", days),
                ("metrics", ActivityMetrics));

            var (joined, here, active) = rows.Count > 0 ? rows[0] : (0, 0, 0);
            result.Add(new NewMembersStayed(days, joined, here, active));
        }

        return result;
    }

    /// <summary>The people who sent the most messages in the window, with their voice minutes. Bots never have rows.</summary>
    private async Task<IReadOnlyList<Contributor>> ContributorsAsync(
        IReadOnlyList<DailyTotalRow> totals, string? guildId, CancellationToken ct)
    {
        var byPerson = totals
            .Where(r => r.Metric is DailyTotalMetrics.DiscordMemberMessages or DailyTotalMetrics.DiscordMemberVoiceMinutes)
            .GroupBy(r => r.Dimension, StringComparer.Ordinal)
            .Select(g => (
                Dimension: g.Key,
                Messages: g.Where(r => r.Metric == DailyTotalMetrics.DiscordMemberMessages).Sum(r => r.Value),
                Voice: g.Where(r => r.Metric == DailyTotalMetrics.DiscordMemberVoiceMinutes).Sum(r => r.Value)))
            .OrderByDescending(p => p.Messages)
            .ThenByDescending(p => p.Voice)
            .ThenBy(p => p.Dimension, StringComparer.Ordinal)
            .Take(TopCount)
            .ToList();

        var people = await PeopleAsync(byPerson.Select(p => p.Dimension), guildId, ct);

        return byPerson
            .Select(p => new Contributor(people[p.Dimension], p.Messages, p.Voice))
            .ToList();
    }

    private async Task<MemberHealth> HealthAsync(string? guildId, DateOnly today, CancellationToken ct)
    {
        if (guildId is null)
            return new MemberHealth(0, 0, 0, []);

        var members = await db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && m.LeftAt == null && !m.IsBot)
            .Select(m => m.UserId)
            .ToListAsync(ct);

        var current = members.Select(id => "discord:" + id).ToHashSet(StringComparer.Ordinal);

        var rows = await db.DailyTotals.AsNoTracking()
            .Where(r => ActivityMetrics.Contains(r.Metric) && r.Day > today.AddDays(-60) && r.Day <= today)
            .Select(r => new { r.Day, r.Metric, r.Dimension, r.Value })
            .ToListAsync(ct);

        var recentStart = today.AddDays(-30);

        var recent = rows.Where(r => r.Day > recentStart).Select(r => r.Dimension).ToHashSet(StringComparer.Ordinal);

        var before = rows
            .Where(r => r.Day <= recentStart && current.Contains(r.Dimension) && !recent.Contains(r.Dimension))
            .GroupBy(r => r.Dimension, StringComparer.Ordinal)
            .Select(g => (
                Dimension: g.Key,
                Messages: g.Where(r => r.Metric == DailyTotalMetrics.DiscordMemberMessages).Sum(r => r.Value),
                Voice: g.Where(r => r.Metric == DailyTotalMetrics.DiscordMemberVoiceMinutes).Sum(r => r.Value)))
            .OrderByDescending(p => p.Messages)
            .ThenByDescending(p => p.Voice)
            .ThenBy(p => p.Dimension, StringComparer.Ordinal)
            .ToList();

        var shown = before.Take(TopCount).ToList();
        var people = await PeopleAsync(shown.Select(p => p.Dimension), guildId, ct);

        return new MemberHealth(
            current.Count,
            current.Count(recent.Contains),
            before.Count,
            shown.Select(p => new Contributor(people[p.Dimension], p.Messages, p.Voice)).ToList());
    }

    /// <summary>Names for <c>discord:&lt;id&gt;</c> dimensions, from the stored member list.</summary>
    private async Task<Dictionary<string, Person>> PeopleAsync(IEnumerable<string> dimensions, string? guildId, CancellationToken ct)
    {
        var split = dimensions
            .Distinct(StringComparer.Ordinal)
            .Select(d => (Dimension: d, Parts: AnalyticsSql.SplitDimension(d)))
            .ToList();

        var ids = split.Select(s => s.Parts.Id).ToList();

        var names = await db.DiscordMembers.AsNoTracking()
            .Where(m => ids.Contains(m.UserId) && (guildId == null || m.GuildId == guildId))
            .ToDictionaryAsync(m => m.UserId, m => m.DisplayName, StringComparer.Ordinal, ct);

        return split.ToDictionary(
            s => s.Dimension,
            s => new Person(s.Parts.Platform, s.Parts.Id, names.GetValueOrDefault(s.Parts.Id)),
            StringComparer.Ordinal);
    }
}
