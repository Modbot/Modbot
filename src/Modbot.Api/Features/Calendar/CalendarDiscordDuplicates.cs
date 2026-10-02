using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;

namespace Modbot.Api.Features.Calendar;

/// <summary>One copy of a Discord server event in a group of possible duplicates (calendar design §16).</summary>
/// <param name="Id">Discord's id for the server event.</param>
/// <param name="Name">The event's title as Discord shows it.</param>
/// <param name="MadeBy"><c>modbot</c>, <c>bot</c>, <c>person</c> or <c>unknown</c>. A person is never named.</param>
/// <param name="BotName">The bot's or app's name, for <c>modbot</c> and <c>bot</c>; null otherwise.</param>
/// <param name="CalendarEventId">
/// The Modbot calendar event this copy is the Discord event of, when it is one: its Discord place
/// holds this id.
/// </param>
/// <param name="CalendarEventTitle">That calendar event's title.</param>
public sealed record CalendarDiscordCopyView(
    string Id,
    string Name,
    DateTimeOffset StartsAt,
    bool Started,
    string MadeBy,
    string? BotName,
    Guid? CalendarEventId,
    string? CalendarEventTitle);

/// <summary>Discord server events that look like copies of each other: the same title, starting close together.</summary>
/// <param name="Title">The first copy's title.</param>
/// <param name="StartsAt">The first copy's start.</param>
/// <param name="Copies">Every copy, soonest first. Always two or more.</param>
public sealed record CalendarDuplicateView(string Title, DateTimeOffset StartsAt, IReadOnlyList<CalendarDiscordCopyView> Copies);

/// <param name="Read">
/// Discord's list was read. False when no server is set, the bot is not connected or Discord did not
/// answer; the list is then empty and says nothing about duplicates.
/// </param>
/// <param name="ReadAt">When Discord was asked. The answer is kept five minutes.</param>
public sealed record CalendarDiscordDuplicatesView(
    bool Read,
    DateTimeOffset? ReadAt,
    IReadOnlyList<CalendarDuplicateView> Duplicates);

/// <summary>
/// The Discord server's events that look like copies (calendar design §16), for the calendar page
/// and Health. Read-only: nothing is deleted or changed in Discord.
/// </summary>
public static class CalendarDiscordDuplicates
{
    public static async Task<CalendarDiscordDuplicatesView> BuildAsync(
        ModbotContext db, IDiscordServerEvents? serverEvents, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var guildId = (await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct))?.Trim();

        if (serverEvents is null || string.IsNullOrEmpty(guildId))
            return new CalendarDiscordDuplicatesView(false, null, []);

        var list = await serverEvents.ReadAsync(guildId, ct);
        if (list is null)
            return new CalendarDiscordDuplicatesView(false, null, []);

        var groups = DiscordEventDuplicates.Find(list.Events);
        if (groups.Count == 0)
            return new CalendarDiscordDuplicatesView(true, list.ReadAt, []);

        // Which copies are the Discord events of Modbot's own calendar events.
        var ids = groups.SelectMany(g => g).Select(e => e.Id).ToList();
        var ours = await db.CalendarEventPlaces.AsNoTracking()
            .Where(p => p.Place == CalendarPlaces.DiscordEvent && p.ExternalId != null && ids.Contains(p.ExternalId))
            .Join(
                db.CalendarEvents.AsNoTracking().Where(e => e.DeletedAt == null),
                p => p.EventId,
                e => e.Id,
                (p, e) => new { DiscordId = p.ExternalId!, e.Id, e.Title })
            .ToListAsync(ct);

        var byDiscordId = ours
            .GroupBy(o => o.DiscordId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        return new CalendarDiscordDuplicatesView(
            true,
            list.ReadAt,
            [.. groups.Select(g => new CalendarDuplicateView(
                g[0].Name,
                g[0].StartsAt,
                [.. g.Select(e =>
                {
                    var own = byDiscordId.GetValueOrDefault(e.Id);

                    return new CalendarDiscordCopyView(
                        e.Id,
                        e.Name,
                        e.StartsAt,
                        e.Started,
                        // The event behind a calendar place is Modbot's, whoever Discord says made it.
                        own is not null ? DiscordEventMakers.Modbot : e.MadeBy,
                        e.BotName,
                        own?.Id,
                        own?.Title);
                })]))]);
    }
}
