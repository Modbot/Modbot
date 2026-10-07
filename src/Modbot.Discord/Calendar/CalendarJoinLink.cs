using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;

namespace Modbot.Discord.Calendar;

/// <summary>
/// The link to an event's open instance: the one rule the calendar page's Join, the Discord event and
/// channel post, and <c>/events</c> share.
/// </summary>
public static class CalendarJoinLink
{
    /// <summary>The event's Who can join when anyone may: <see cref="CalendarEvent.AccessType"/>.</summary>
    public const string AnyoneCanJoin = "public";

    /// <summary>
    /// The link for a date to show anyone in Discord: only when Who can join is Anyone, this date is
    /// the one the event has open, and its instance is still open (<see cref="OpenAsync"/>). The
    /// rule <c>/events</c> and <c>/remindme</c> share.
    /// </summary>
    /// <param name="startsAt">When the date starts, as it now happens.</param>
    public static async Task<string?> ForAnyoneAsync(
        ModbotContext db, CalendarEvent calendarEvent, DateTimeOffset startsAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        return calendarEvent.State == CalendarEventStates.Open
            && calendarEvent.OccurrenceStartsAt == startsAt
            && calendarEvent.AccessType == AnyoneCanJoin
                ? await OpenAsync(db, calendarEvent, ct).ConfigureAwait(false)
                : null;
    }

    /// <summary>
    /// The instance's join link, while the occurrence Modbot opened is still open; null when the
    /// event is not open, nothing was opened for this date, or the instance has since closed.
    /// </summary>
    public static async Task<string?> OpenAsync(ModbotContext db, CalendarEvent calendarEvent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(calendarEvent);

        if (calendarEvent.State != CalendarEventStates.Open || calendarEvent.OccurrenceStartsAt is not { } occurrence)
            return null;

        var opening = await db.CalendarOpenings.AsNoTracking()
            .FirstOrDefaultAsync(o => o.EventId == calendarEvent.Id && o.OccurrenceStartsAt == occurrence && o.Location != null, ct)
            .ConfigureAwait(false);

        if (opening?.Location is not { } location)
            return null;

        if (opening.InstanceId is { } instanceId
            && await db.VRChatInstances.AsNoTracking().AnyAsync(i => i.Id == instanceId && i.ClosedAt != null, ct).ConfigureAwait(false))
        {
            return null;
        }

        return InstanceJoinLink.For(location);
    }
}
