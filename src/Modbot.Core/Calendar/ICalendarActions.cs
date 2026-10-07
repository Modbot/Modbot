using Modbot.Core.Discord;

namespace Modbot.Core.Calendar;

/// <summary>One upcoming event, as a suggestion under <c>/event</c>.</summary>
/// <param name="NextStartsAt">When its next date starts.</param>
/// <param name="When">
/// That start as plain words in the event's own time zone, such as "Sat 10 Oct, 20:00 UTC": a
/// suggestion is text, and cannot carry Discord's own timestamps.
/// </param>
public sealed record CalendarEventChoice(Guid Id, string Title, DateTimeOffset NextStartsAt, string When);

/// <summary>One upcoming date of an event, as a suggestion under <c>/event cancel-date</c>.</summary>
/// <param name="PlannedStartsAt">The date as the event's repeat plans it: what a date is known by.</param>
/// <param name="StartsAt">When it really starts: different from the plan when the date was moved.</param>
/// <param name="When">The start as plain words in the event's own time zone.</param>
public sealed record CalendarDateChoice(DateTimeOffset PlannedStartsAt, DateTimeOffset StartsAt, string When);

/// <summary>How <c>/event open</c> went.</summary>
/// <param name="Opened">VRChat opened the instance.</param>
/// <param name="Message">The sentence to show when it did not open: the refusal's words, or what VRChat said.</param>
/// <param name="Title">The event's title, when it was found.</param>
public sealed record CalendarOpenAnswer(bool Opened, string Message, string? Title = null);

/// <summary>What <c>/event cancel-date</c> would do, found out before anyone is asked to confirm.</summary>
/// <param name="Allowed">It would go ahead.</param>
/// <param name="Message">When it would not: the sentence to show.</param>
/// <param name="Title">The event's title, for the question.</param>
/// <param name="StartsAt">When the date starts, for the question.</param>
/// <param name="WholeEvent">A one-off event has no other date: cancelling its only date cancels the event.</param>
public sealed record CalendarCancelPlan(bool Allowed, string? Message, string Title, DateTimeOffset StartsAt, bool WholeEvent)
{
    public static CalendarCancelPlan Refused(string message) => new(false, message, string.Empty, default, false);
}

/// <summary>How a cancel went.</summary>
/// <param name="Done">The date, or the event, is cancelled now.</param>
/// <param name="Message">The sentence to show.</param>
/// <param name="Repeat">It was cancelled already, so nothing changed and nothing was written.</param>
public sealed record CalendarCancelAnswer(bool Done, string Message, bool Repeat = false);

/// <summary>
/// What a staff member may do to the calendar from Discord, done exactly the way the web app does
/// it (Discord commands design §3.7 and §4, step 7).
/// </summary>
/// <remarks>
/// <para>
/// In Core so the bot can ask without depending on the API or the VRChat project, whose
/// <c>CalendarOpener</c> and <c>CalendarCancellations</c> do the work: the API's endpoints call the
/// same two, so a press in Discord and a press in the web app give the same answer, write the same
/// facts and make the same rows.
/// </para>
/// <para>
/// The caller has already resolved who is acting and checked their permission. The implementation
/// checks Manage calendar again: one place that forgot is not allowed to be the one that cancels.
/// </para>
/// </remarks>
public interface ICalendarActions
{
    /// <summary>
    /// The upcoming events whose title holds <paramref name="typed"/>, soonest first. Empty for a
    /// caller who may not see the calendar: the web app lists events only with See calendar.
    /// </summary>
    Task<IReadOnlyList<CalendarEventChoice>> UpcomingAsync(string typed, int most, StaffMember by, CancellationToken ct = default);

    /// <summary>The event's next dates, soonest first. Empty for a caller who may not see the calendar.</summary>
    Task<IReadOnlyList<CalendarDateChoice>> DatesAsync(Guid eventId, int most, StaffMember by, CancellationToken ct = default);

    /// <summary>Opens the event's instance now: <see cref="CalendarOpenAnswer.Message"/> is the endpoint's words for a refusal.</summary>
    Task<CalendarOpenAnswer> OpenNowAsync(Guid eventId, StaffMember by, CancellationToken ct = default);

    /// <summary>
    /// Every check a cancel makes before it changes anything, without changing it. A one-off event's
    /// only date is the whole event.
    /// </summary>
    /// <param name="sayIt">Also post that it is cancelled, in the event's channel.</param>
    Task<CalendarCancelPlan> PlanCancelDateAsync(
        Guid eventId, DateTimeOffset plannedStartsAt, bool sayIt, StaffMember by, CancellationToken ct = default);

    /// <summary>
    /// Cancels one date of a repeating event, or a one-off event whole. Cancelled already is done,
    /// and writes nothing a second time.
    /// </summary>
    Task<CalendarCancelAnswer> CancelDateAsync(
        Guid eventId, DateTimeOffset plannedStartsAt, bool sayIt, StaffMember by, CancellationToken ct = default);
}
