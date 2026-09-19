using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.DailyTotals;
using Modbot.Api.Auth;
using Modbot.Api.Features.Analytics;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordMembers;

/// <summary>A file attached to a Discord message. The file itself was never downloaded.</summary>
public sealed record DiscordAttachmentView(string Name, string? Type, long? Size, string? Url);

/// <summary>One stored message a Discord member wrote.</summary>
/// <param name="ChannelName">The channel's name as last stored, or null when the channel is not known.</param>
/// <param name="ThreadId">The thread it was posted in, or null for a message straight in the channel.</param>
/// <param name="ThreadName">The thread's name as last stored, or null.</param>
/// <param name="Text">As it reads now. Empty when the message had none, or when the bot could not read message text.</param>
/// <param name="EditedAt">When the text last changed, or null if it never did.</param>
/// <param name="DeletedAt">When it was deleted. A deleted message is still returned, marked.</param>
public sealed record DiscordMemberMessageView(
    string MessageId,
    DateTimeOffset SentAt,
    string ChannelId,
    string? ChannelName,
    string? ThreadId,
    string? ThreadName,
    string Text,
    IReadOnlyList<DiscordAttachmentView> Attachments,
    int EmbedCount,
    string? ReplyToId,
    DateTimeOffset? EditedAt,
    DateTimeOffset? DeletedAt);

/// <param name="Total">Messages stored for this person, across every page.</param>
public sealed record DiscordMemberMessagesResponse(
    IReadOnlyList<DiscordMemberMessageView> Messages,
    int Total,
    int Page,
    int PageSize);

/// <summary>One join or leave of the Discord server, from the fact log.</summary>
/// <param name="Change"><c>joined</c> or <c>left</c>.</param>
/// <param name="Before">The end of the window when only a window is known, as after the bot was offline.</param>
public sealed record DiscordMembershipChange(string Change, DateTimeOffset At, DateTimeOffset? Before);

/// <summary>What Modbot can count about one Discord member.</summary>
/// <param name="MessagesPerDay">Each of the last thirty days, oldest first, zero where nothing was sent.</param>
/// <param name="VoiceMinutesPerDay">The same thirty days, in minutes in voice.</param>
/// <param name="MessagesAllTime">Every message counted in the daily totals, for as long as they go back.</param>
/// <param name="FirstSeenAt">When Modbot first saw them in the server, or null when it never has.</param>
/// <param name="History">Joins and leaves the fact log still holds, oldest first.</param>
public sealed record DiscordMemberMetrics(
    string UserId,
    IReadOnlyList<DayValue> MessagesPerDay,
    IReadOnlyList<DayValue> VoiceMinutesPerDay,
    decimal MessagesAllTime,
    decimal VoiceMinutesAllTime,
    DateTimeOffset? FirstSeenAt,
    DateTimeOffset? JoinedAt,
    DateTimeOffset? LeftAt,
    IReadOnlyList<DiscordMembershipChange> History,
    DateTimeOffset Now);

/// <summary>
/// One Discord member's stored messages, and what can be counted about them, for their popup.
/// </summary>
/// <remarks>
/// <para>
/// Messages need <see cref="ModbotPermissions.ReadDiscordMessages"/>. Nothing else in Modbot shows a
/// person's messages: analytics only count them, and a flag quotes the one line a rule matched.
/// Reading everything somebody wrote, including what they deleted, is a bigger thing to hand out,
/// so it is its own permission. Deleted messages are returned and marked, because the deleted one
/// is often the one a moderator needs.
/// </para>
/// <para>
/// The counts need <see cref="ModbotPermissions.ViewProfile"/>, like a VRChat person's metrics. They
/// come from the daily totals, which are kept after messages themselves have been dropped by
/// retention, so the counts can go back further than the messages do.
/// </para>
/// </remarks>
public static class DiscordMemberActivity
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    /// <summary>How many days the per-day figures cover.</summary>
    public const int Days = 30;

    public static RouteGroupBuilder MapDiscordMemberActivity(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/{id}/messages", async (
                [FromRoute] string id,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                [FromQuery] string? at,
                [FromServices] ModbotContext db,
                CancellationToken ct) =>
                Results.Ok(await MessagesAsync(db, id, page, pageSize, at, ct)))
            .RequiresFlag(ModbotPermissions.ReadDiscordMessages)
            .WithName("GetDiscordMemberMessages")
            .WithSummary("List Discord member messages")
            .WithDescription(
                "Messages in the server in settings, newest first. A deleted message is returned with "
                + "`deletedAt` set and an edited one with `editedAt`; `text` is the text as it reads now. "
                + "Pass `at` with a message id instead of `page` to get the page that message is on, "
                + "which is what a link to one message opens. Needs Read Discord messages.")
            .Produces<DiscordMemberMessagesResponse>()
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id}/metrics", async (
                [FromRoute] string id,
                [FromServices] ModbotContext db,
                [FromServices] IModbotClock clock,
                CancellationToken ct) =>
                Results.Ok(await MetricsAsync(db, clock.UtcNow, id, ct)))
            .RequiresFlag(ModbotPermissions.ViewProfile)
            .WithName("GetDiscordMemberMetrics")
            .WithSummary("Get Discord member activity")
            .WithDescription(
                "Messages and voice minutes per day, and joins and leaves, for one Discord member.")
            .Produces<DiscordMemberMetrics>()
            .Produces(StatusCodes.Status403Forbidden);

        return group;
    }

    /// <param name="at">
    /// A message id to open at. The page holding it is returned, whatever <paramref name="page"/>
    /// says, so a link to one message lands on it rather than on the newest page.
    /// </param>
    internal static async Task<DiscordMemberMessagesResponse> MessagesAsync(
        ModbotContext db, string id, int? page, int? pageSize, string? at, CancellationToken ct)
    {
        var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
        var number = Math.Max(page ?? 1, 1);

        var guildId = await DiscordMemberEndpoints.GuildIdAsync(db, ct);

        var query = db.DiscordMessages.AsNoTracking()
            .Where(m => m.AuthorId == id && (guildId == null || m.GuildId == guildId));

        var total = await query.CountAsync(ct);

        if (!string.IsNullOrWhiteSpace(at)
            && await query.FirstOrDefaultAsync(m => m.MessageId == at, ct) is { } asked)
        {
            // Which page it is on is how many of this person's messages are newer than it. The
            // list is paged by offset, so this is the one number that finds it.
            var newer = await query.CountAsync(
                m => m.SentAt > asked.SentAt || (m.SentAt == asked.SentAt && string.Compare(m.MessageId, asked.MessageId) > 0),
                ct);

            number = (newer / size) + 1;
        }

        var rows = await query
            .OrderByDescending(m => m.SentAt)
            .ThenByDescending(m => m.MessageId)
            .Skip((number - 1) * size)
            .Take(size)
            .ToListAsync(ct);

        var channelIds = rows.Select(r => r.ChannelId).Distinct(StringComparer.Ordinal).ToList();
        var channels = channelIds.Count == 0
            ? []
            : await db.DiscordChannels.AsNoTracking()
                .Where(c => channelIds.Contains(c.ChannelId))
                .ToDictionaryAsync(c => c.ChannelId, c => c.Name, StringComparer.Ordinal, ct);

        // Threads are not in the channel list; the read-back row is the one place their names are kept.
        var threadIds = rows.Where(r => r.ThreadId != null).Select(r => r.ThreadId!).Distinct(StringComparer.Ordinal).ToList();
        var threads = threadIds.Count == 0
            ? []
            : await db.DiscordReadBacks.AsNoTracking()
                .Where(t => threadIds.Contains(t.ChannelId))
                .ToDictionaryAsync(t => t.ChannelId, t => t.Name, StringComparer.Ordinal, ct);

        return new DiscordMemberMessagesResponse(
            rows.Select(r => new DiscordMemberMessageView(
                r.MessageId,
                r.SentAt,
                r.ChannelId,
                channels.GetValueOrDefault(r.ChannelId),
                r.ThreadId,
                r.ThreadId is { } thread ? threads.GetValueOrDefault(thread) : null,
                r.Text,
                Attachments(r.Attachments),
                r.EmbedCount,
                r.ReplyToId,
                r.EditedAt,
                r.DeletedAt)).ToList(),
            total,
            number,
            size);
    }

    internal static async Task<DiscordMemberMetrics> MetricsAsync(
        ModbotContext db, DateTimeOffset now, string id, CancellationToken ct)
    {
        var dimension = DailyTotalDimensions.ForUser(FactPlatform.Discord, id);
        var to = AnalyticsSql.DayOf(now);
        var from = to.AddDays(-(Days - 1));

        string[] metrics = [DailyTotalMetrics.DiscordMemberMessages, DailyTotalMetrics.DiscordMemberVoiceMinutes];

        var recent = await db.DailyTotals.AsNoTracking()
            .Where(t => metrics.Contains(t.Metric) && t.Dimension == dimension && t.Day >= from && t.Day <= to)
            .Select(t => new { t.Metric, t.Day, t.Value })
            .ToListAsync(ct);

        var allTime = await db.DailyTotals.AsNoTracking()
            .Where(t => metrics.Contains(t.Metric) && t.Dimension == dimension)
            .GroupBy(t => t.Metric)
            .Select(g => new { Metric = g.Key, Value = g.Sum(t => t.Value) })
            .ToDictionaryAsync(g => g.Metric, g => g.Value, StringComparer.Ordinal, ct);

        IReadOnlyList<DayValue> Series(string metric) =>
            Enumerable.Range(0, Days)
                .Select(i => from.AddDays(i))
                .Select(day => new DayValue(day, recent.Where(r => r.Metric == metric && r.Day == day).Sum(r => r.Value)))
                .ToList();

        var guildId = await DiscordMemberEndpoints.GuildIdAsync(db, ct);
        var member = await db.DiscordMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == id && (guildId == null || m.GuildId == guildId), ct);

        string[] changes = [FactType.DiscordMemberJoined, FactType.DiscordMemberLeft];

        var history = await db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.Discord && e.SubjectId == id && changes.Contains(e.Type))
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.Id)
            .Select(e => new { e.Type, e.OccurredAt, e.OccurredBefore })
            .ToListAsync(ct);

        return new DiscordMemberMetrics(
            id,
            Series(DailyTotalMetrics.DiscordMemberMessages),
            Series(DailyTotalMetrics.DiscordMemberVoiceMinutes),
            allTime.GetValueOrDefault(DailyTotalMetrics.DiscordMemberMessages),
            allTime.GetValueOrDefault(DailyTotalMetrics.DiscordMemberVoiceMinutes),
            member?.FirstSeenAt,
            member?.JoinedAt,
            member?.LeftAt,
            history
                .Select(h => new DiscordMembershipChange(
                    h.Type == FactType.DiscordMemberJoined ? "joined" : "left",
                    h.OccurredAt,
                    h.OccurredBefore))
                .ToList(),
            now);
    }

    /// <summary>The stored attachment list, or none when it cannot be read.</summary>
    private static IReadOnlyList<DiscordAttachmentView> Attachments(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<StoredAttachment>>(json, Json)?
                .Select(a => new DiscordAttachmentView(a.Name ?? "file", a.Type, a.Size, a.Url))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record StoredAttachment(string? Name, string? Type, long? Size, string? Url);
}
