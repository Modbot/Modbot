using Modbot.Core.Data.Entities;

namespace Modbot.Core.Calendar;

/// <summary>What moving an event along its timeline did.</summary>
public enum CalendarStep
{
    /// <summary>Nothing changed.</summary>
    None = 0,

    /// <summary>The occurrence it is about moved on, and it is not open yet.</summary>
    NextOccurrence = 1,

    /// <summary>An occurrence opened.</summary>
    Opened = 2,

    /// <summary>No occurrences are left.</summary>
    Finished = 3,
}

/// <summary>
/// Moves an event through <c>scheduled → open → scheduled … finished</c> (calendar design §2.1).
/// </summary>
/// <remarks>
/// Asks nothing of VRChat or Discord, so it runs whatever state they are in: a cold stop on the
/// calendar lane must never leave an event showing as open after it ended.
/// </remarks>
public static class CalendarTimeline
{
    /// <summary>Brings an event's state and current occurrence in line with <paramref name="now"/>.</summary>
    public static CalendarStep Advance(CalendarEvent calendarEvent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        if (!CalendarEventStates.IsLive(calendarEvent.State) || calendarEvent.DeletedAt is not null)
            return CalendarStep.None;

        if (CalendarRepeat.Next(calendarEvent, now) is not { } occurrence)
        {
            calendarEvent.State = CalendarEventStates.Finished;
            return CalendarStep.Finished;
        }

        var moved = calendarEvent.OccurrenceStartsAt != occurrence.StartsAt;
        var wasOpen = calendarEvent.State == CalendarEventStates.Open && !moved;
        var open = now >= CalendarRepeat.OpensAt(calendarEvent, occurrence);

        calendarEvent.OccurrenceStartsAt = occurrence.StartsAt;
        calendarEvent.State = open ? CalendarEventStates.Open : CalendarEventStates.Scheduled;

        if (open && !wasOpen)
            return CalendarStep.Opened;

        return moved ? CalendarStep.NextOccurrence : CalendarStep.None;
    }
}
