using Modbot.Api.Features.Analytics.Instances;
using Modbot.Api.Features.Places;

namespace Modbot.Api.Features.Calendar;

/// <summary>One time an event ran, and what it did (<see cref="CalendarResults"/>).</summary>
/// <param name="StartsAt">When this time started, as the event's rule or Modbot's opening of it says.</param>
/// <param name="Instance">
/// The instance it ran in, with the same figures the instance popup shows: the most at once over the
/// instance's whole life and how long it ran. The one Modbot opened for it; otherwise the group's
/// instance in the event's world that was open longest while the event ran. Null when there is
/// neither.
/// </param>
/// <param name="OpenedByModbot">True when <paramref name="Instance"/> is the one Modbot opened for this time.</param>
/// <param name="NewMembers">People who joined the group from when the event opened until a day after it ended.</param>
/// <param name="JoinRequests">Join requests received over the same stretch.</param>
/// <param name="Seen">
/// What a moderator's client saw in the instance: people, people-time and joins, as the instance
/// popup counts them. Null without ViewAuditLog, in a list, or with no instance.
/// </param>
public sealed record CalendarOccurrenceResult(
    Guid EventId,
    string Title,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    InstanceRow? Instance,
    bool OpenedByModbot,
    int NewMembers,
    int JoinRequests,
    PlaceCounts? Seen);

/// <summary>
/// The middle value of an event's earlier times, to set one time beside. Each figure is null when
/// none of the earlier times has it: no instance, or no moderator's client there.
/// </summary>
/// <param name="Times">How many earlier times were compared: up to <see cref="CalendarResults.Earlier"/>.</param>
public sealed record CalendarUsual(
    int Times,
    int? PeakPeople,
    decimal? MinutesOpen,
    int NewMembers,
    int JoinRequests,
    int? PeopleSeen,
    decimal? MinutesSeen);

/// <param name="People">
/// Who a moderator's client saw in the instance, longest first. Empty without ViewAuditLog.
/// </param>
/// <param name="Usual">The event's earlier times, or null when this is its first.</param>
/// <param name="CanSeeWhoWasThere">True with ViewAuditLog, as for the instance popup.</param>
public sealed record CalendarResultsView(
    CalendarOccurrenceResult Occurrence,
    IReadOnlyList<PersonSeen> People,
    CalendarUsual? Usual,
    bool CanSeeWhoWasThere,
    DateTimeOffset Now);

/// <param name="Occurrences">Every time an event ran in the stretch, the most at once first.</param>
/// <param name="From">The start of the stretch.</param>
public sealed record CalendarPastView(
    IReadOnlyList<CalendarOccurrenceResult> Occurrences,
    DateTimeOffset From,
    DateTimeOffset Now);
