using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Calendar;

/// <summary>
/// Writes down how many people are interested on Discord, once, when a date of an event has
/// started: the count on the Discord server event Modbot made for that date.
/// </summary>
/// <remarks>
/// <para>
/// Each date is written once (<see cref="CalendarInterestStore"/>), so a later pass, a restart or a
/// second copy of Modbot never changes the number. The pass asks Discord for the server's events
/// only while a date with a Discord event is running and has no count yet: one request, and none at
/// all the rest of the time.
/// </para>
/// <para>
/// A date whose Discord event is no longer in Discord's list when it is looked for is written with no
/// count, so it is not looked for again.
/// </para>
/// </remarks>
public sealed class EventInterestSnapshot
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly CalendarInterestStore _store;
    private readonly ILogger _log;

    public EventInterestSnapshot(ModbotContext db, IModbotClock clock, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _store = new CalendarInterestStore(db);
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <returns>How many counts were written.</returns>
    public async Task<int> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.DiscordGuildId?.Trim() is not { Length: > 0 } guildId)
            return 0;

        var now = _clock.UtcNow;

        var places = await _db.CalendarEventPlaces.AsNoTracking()
            .Where(p => p.Place == CalendarPlaces.DiscordEvent && p.ExternalId != null && p.OccurrenceStartsAt != null)
            .ToListAsync(ct).ConfigureAwait(false);

        if (places.Count == 0)
            return 0;

        var ids = places.Select(p => p.EventId).ToList();

        var events = await _db.CalendarEvents.AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.DeletedAt == null)
            .ToDictionaryAsync(e => e.Id, ct).ConfigureAwait(false);

        var due = new List<(Guid EventId, DateTimeOffset StartsAt, string DiscordId)>();

        foreach (var place in places)
        {
            if (!events.TryGetValue(place.EventId, out var calendarEvent)
                || CalendarRepeat.ForDate(calendarEvent, place.OccurrenceStartsAt!.Value) is not { } occurrence
                || now < occurrence.StartsAt
                || now >= occurrence.EndsAt)
            {
                continue;
            }

            var taken = await _db.CalendarInterests.AsNoTracking()
                .AnyAsync(i => i.EventId == place.EventId
                    && i.OccurrenceStartsAt == occurrence.StartsAt
                    && i.DiscordTakenAt != null, ct).ConfigureAwait(false);

            if (!taken)
                due.Add((place.EventId, occurrence.StartsAt, place.ExternalId!));
        }

        if (due.Count == 0)
            return 0;

        // One request for all of them. No answer is tried again on the next pass.
        if (await gateway.ReadServerEventsAsync(guildId, ct).ConfigureAwait(false) is not { } listed)
            return 0;

        var written = 0;

        foreach (var (eventId, startsAt, discordId) in due)
        {
            var count = listed.FirstOrDefault(e => string.Equals(e.Id, discordId, StringComparison.Ordinal))?.Interested;

            if (await _store.RecordDiscordAsync(eventId, startsAt, count, now, ct).ConfigureAwait(false))
                written++;
        }

        if (written > 0)
            _log.Information("Wrote down Discord's interested count for {Dates} dates", written);

        return written;
    }
}
