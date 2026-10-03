using Modbot.Core.Data.Entities;
using Modbot.Core.Google;

namespace Modbot.Core.Calendar;

/// <summary>How an event stands towards the Google calendar right now (Google Calendar design §3.3).</summary>
public enum CalendarGoogleWants
{
    /// <summary>Not on Google: taken off it when it is there.</summary>
    Nothing,

    /// <summary>On Google: made there when it is not yet, and kept in line with the event.</summary>
    There,

    /// <summary>
    /// Kept on Google while it is there, but never made there now: a finished event (history) or a
    /// cancelled one in the day after its date (decision 3 B).
    /// </summary>
    KeptOnly,
}

/// <summary>
/// What goes to the Google calendar, and when (Google Calendar design §3.3): one set of rules for
/// the sending loop, the event's default tick, readiness and the tests.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Members-only events never go</strong> (decision 1 A). An event is for members unless it
/// is visible to everyone and not narrowed to some of the group's roles. Unlike the public feed this
/// does not also ask for the VRChat chip: the event form shows "Visible to" for Google Calendar too
/// (§3.8).
/// </para>
/// <para>
/// <strong>A cancelled event stays, marked, for a day</strong> (decision 3 B): with "Cancelled: " in
/// front of its title and its repeat cut at the date it was cancelled on, until a day after that date
/// ends; then it is taken off.
/// </para>
/// </remarks>
public static class CalendarGoogle
{
    /// <summary>How long after its date a cancelled event stays on Google, marked cancelled.</summary>
    public static readonly TimeSpan KeepCancelledFor = TimeSpan.FromDays(1);

    /// <summary>What goes in front of a cancelled event's title on Google.</summary>
    public const string CancelledTitle = "Cancelled: ";

    private const string PublicWord = "public";

    /// <summary>
    /// A key and a calendar are stored and the last Check passed and found Modbot may change events
    /// there. A refusal the sending loop met since (a refused key, a calendar no longer shared) is
    /// written where Check writes its problem, so it counts as not set up until the next good Check.
    /// </summary>
    public static bool SetUp(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.GooglePrivateKeyEncrypted is not null
            && !string.IsNullOrWhiteSpace(settings.GoogleClientEmail)
            && !string.IsNullOrWhiteSpace(settings.GoogleKeyId)
            && GoogleCalendarIds.IsUsable(settings.GoogleCalendarId)
            && settings.GoogleCheckedAt is not null
            && settings.GoogleProblem is null
            && settings.GoogleCanChange;
    }

    /// <summary>Set up, and Sending is on: events are sent (§3.1).</summary>
    public static bool Ready(Settings settings) =>
        SetUp(settings) && settings.GoogleSendingOn && !settings.GoogleRemovingEvents;

    /// <summary>Whether an event is for the group's members only, and so never goes (decision 1 A).</summary>
    public static bool MembersOnly(CalendarEvent calendarEvent)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);
        return MembersOnly(calendarEvent.Visibility, calendarEvent.VRChatRoleIds);
    }

    /// <summary>
    /// The same rule from the two fields alone, for a read that does not load the whole event:
    /// not visible to everyone, or shown on VRChat only to some of the group's roles.
    /// </summary>
    public static bool MembersOnly(string? visibility, IReadOnlyCollection<string>? vrchatRoleIds) =>
        visibility != PublicWord || vrchatRoleIds is { Count: > 0 };

    /// <summary>
    /// Whether a new event, or one read in from VRChat, starts ticked (decision 6 A): Google is set
    /// up, and the event is not for members only.
    /// </summary>
    public static bool TicksByDefault(Settings settings, CalendarEvent calendarEvent) =>
        SetUp(settings) && !MembersOnly(calendarEvent);

    /// <summary>How the event stands towards Google at <paramref name="now"/>, from the event alone.</summary>
    public static CalendarGoogleWants WantsOf(CalendarEvent calendarEvent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        if (!calendarEvent.PublishToGoogle || calendarEvent.DeletedAt is not null || MembersOnly(calendarEvent))
            return CalendarGoogleWants.Nothing;

        return calendarEvent.State switch
        {
            CalendarEventStates.Scheduled or CalendarEventStates.Open => CalendarGoogleWants.There,
            CalendarEventStates.Finished => CalendarGoogleWants.KeptOnly,
            CalendarEventStates.Cancelled when now < KeptUntil(calendarEvent) => CalendarGoogleWants.KeptOnly,
            _ => CalendarGoogleWants.Nothing,
        };
    }

    /// <summary>Whether the event goes to Google now: Sending is on and the event wants to be there.</summary>
    public static bool Wants(CalendarEvent calendarEvent, Settings settings, DateTimeOffset now) =>
        Ready(settings) && WantsOf(calendarEvent, now) == CalendarGoogleWants.There;

    /// <summary>
    /// The date a cancelled event was cancelled on: the one it was dealing with, or its first.
    /// </summary>
    public static CalendarOccurrence CancelledDate(CalendarEvent calendarEvent) => CalendarRepeat.Current(calendarEvent);

    /// <summary>Until when a cancelled event stays on Google: a day after the date it was cancelled on ends.</summary>
    public static DateTimeOffset KeptUntil(CalendarEvent calendarEvent) =>
        CancelledDate(calendarEvent).EndsAt + KeepCancelledFor;
}
