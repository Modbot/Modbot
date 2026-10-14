using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Core.Calendar;

/// <summary>
/// Writes how many people were interested in a date of an event when it started: Discord's count
/// from the Discord side, VRChat's from the VRChat side (<see cref="CalendarInterest"/>).
/// </summary>
/// <remarks>
/// Each side is written once per date. The second write for the same side, from a restart or from
/// another copy of Modbot, changes nothing, so the number stays what it was at the start. The two
/// sides share a row and write different columns.
/// </remarks>
public sealed class CalendarInterestStore
{
    private readonly ModbotContext _db;

    public CalendarInterestStore(ModbotContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    /// <summary>Which sides already have a count for these dates of one event.</summary>
    public async Task<IReadOnlyDictionary<DateTimeOffset, CalendarInterest>> ForEventAsync(
        Guid eventId, CancellationToken ct = default)
        => await _db.CalendarInterests.AsNoTracking()
            .Where(i => i.EventId == eventId)
            .ToDictionaryAsync(i => i.OccurrenceStartsAt, ct).ConfigureAwait(false);

    /// <summary>Writes Discord's count for a date, once. False when it was written before.</summary>
    /// <param name="count">The count, or null when Discord did not give one.</param>
    public Task<bool> RecordDiscordAsync(
        Guid eventId, DateTimeOffset startsAt, int? count, DateTimeOffset now, CancellationToken ct = default)
        => RecordAsync(
            eventId,
            startsAt,
            rows => rows.Where(i => i.DiscordTakenAt == null)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(i => i.DiscordInterested, count)
                    .SetProperty(i => i.DiscordTakenAt, now), ct),
            () => new CalendarInterest
            {
                EventId = eventId,
                OccurrenceStartsAt = startsAt,
                DiscordInterested = count,
                DiscordTakenAt = now,
            },
            ct);

    /// <summary>Writes VRChat's count for a date, once. False when it was written before.</summary>
    /// <param name="count">The count, or null when VRChat's calendar did not list the date or gave none.</param>
    public Task<bool> RecordVRChatAsync(
        Guid eventId, DateTimeOffset startsAt, int? count, DateTimeOffset now, CancellationToken ct = default)
        => RecordAsync(
            eventId,
            startsAt,
            rows => rows.Where(i => i.VRChatTakenAt == null)
                .ExecuteUpdateAsync(set => set
                    .SetProperty(i => i.VRChatInterested, count)
                    .SetProperty(i => i.VRChatTakenAt, now), ct),
            () => new CalendarInterest
            {
                EventId = eventId,
                OccurrenceStartsAt = startsAt,
                VRChatInterested = count,
                VRChatTakenAt = now,
            },
            ct);

    private async Task<bool> RecordAsync(
        Guid eventId,
        DateTimeOffset startsAt,
        Func<IQueryable<CalendarInterest>, Task<int>> fill,
        Func<CalendarInterest> made,
        CancellationToken ct)
    {
        var row = _db.CalendarInterests.Where(i => i.EventId == eventId && i.OccurrenceStartsAt == startsAt);

        if (await fill(row).ConfigureAwait(false) > 0)
            return true;

        // The row is there and this side is filled, or there is no row yet.
        if (await row.AnyAsync(ct).ConfigureAwait(false))
            return false;

        var created = made();
        _db.CalendarInterests.Add(created);

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // The other side, or another copy of Modbot, wrote the row between the look and the insert.
            _db.Entry(created).State = EntityState.Detached;
            return await fill(row).ConfigureAwait(false) > 0;
        }
    }
}
