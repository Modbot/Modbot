using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Calendar;

/// <summary>
/// Posts once in an event's channel when the first person is in the instance Modbot opened for it
/// (calendar auto-invite design §7).
/// </summary>
/// <remarks>
/// <para>
/// "Somebody is in" is the group instance poll's own count on the instance's row: the last count or
/// the most at once above zero. Nothing here asks VRChat.
/// </para>
/// <para>
/// <strong>Once per occurrence.</strong> <c>calendar_opening.first_join_posted_at</c> is written
/// before the post, so a restart never posts twice, and a refusal is kept on the row rather than
/// tried again every pass.
/// </para>
/// </remarks>
public sealed class CalendarFirstJoinPost
{
    /// <summary>The longest one occurrence may last; openings older than this cannot still be running.</summary>
    private static readonly TimeSpan LongestOccurrence = TimeSpan.FromDays(7);

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public CalendarFirstJoinPost(ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <summary>The post's words.</summary>
    public static string Text(string title) => $"**{title}** has started.";

    /// <returns>How many posts went out.</returns>
    public async Task<int> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var now = _clock.UtcNow;
        var since = now - LongestOccurrence;

        var openings = await _db.CalendarOpenings
            .Where(o => o.Location != null && o.InstanceId != null && o.FirstJoinDiscordPostedAt == null && o.OccurrenceStartsAt > since)
            .ToListAsync(ct).ConfigureAwait(false);

        var posted = 0;

        foreach (var opening in openings)
        {
            var calendarEvent = await _db.CalendarEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == opening.EventId, ct).ConfigureAwait(false);

            if (calendarEvent is null
                || !calendarEvent.AnnounceFirstJoinInDiscord
                || !calendarEvent.PostToChannel
                || calendarEvent.ChannelId is not { Length: > 0 } channelId
                || calendarEvent.DeletedAt is not null
                || !CalendarEventStates.IsLive(calendarEvent.State))
            {
                continue;
            }

            if (now >= opening.OccurrenceStartsAt + (calendarEvent.EndsAt - calendarEvent.StartsAt))
                continue;

            var instance = await _db.VRChatInstances.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == opening.InstanceId, ct).ConfigureAwait(false);

            if (instance is null || instance.ClosedAt is not null)
                continue;

            if ((instance.LastUserCount ?? 0) <= 0 && (instance.PeakUserCount ?? 0) <= 0)
                continue;

            // Written first: once, however many restarts.
            opening.FirstJoinDiscordPostedAt = now;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            var link = InstanceJoinLink.For(opening.Location);
            var outcome = await gateway.PostAsync(
                channelId.Trim(),
                Text(calendarEvent.Title),
                [],
                link is null ? null : [new DiscordLinkButton("Join", link)],
                ct).ConfigureAwait(false);

            if (outcome.Sent)
            {
                posted++;
                continue;
            }

            var error = outcome.Error ?? "Discord refused the post.";
            opening.FirstJoinDiscordPostError = error.Length <= 1024 ? error : error[..1024];
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _log.Warning(
                "Could not post that the event {EventId} has started: {Error}", calendarEvent.Id, opening.FirstJoinDiscordPostError);
        }

        return posted;
    }
}
