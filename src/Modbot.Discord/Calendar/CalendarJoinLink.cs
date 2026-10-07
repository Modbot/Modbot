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
