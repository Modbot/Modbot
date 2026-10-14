namespace Modbot.Core.Data.Entities;

/// <summary>
/// One date of an event that one person came to. The table is <c>event_attendee</c>, a row for each
/// person and date.
/// </summary>
/// <remarks>
/// <para>
/// Written once a date has closed, from the same answer the event recap uses
/// (<c>EventAttendance</c>), so only people in an instance where a moderator's Modbot app stood are
/// here. The key is the promise: counting a date twice, in two copies of Modbot or after a restart,
/// writes the same row once.
/// </para>
/// <para>
/// The number of events somebody came to, and the date of their Nth, are read from these rows, so
/// changing the numbers in the settings changes the marks without counting again.
/// </para>
/// <para>
/// Names a VRChat account, so a purge of that account deletes its rows.
/// </para>
/// </remarks>
public class EventAttendee
{
    /// <summary>The VRChat user id. An opaque string; never parsed.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The event.</summary>
    public Guid EventId { get; set; }

    /// <summary>The date, as <see cref="CalendarOpening.OccurrenceStartsAt"/> names it.</summary>
    public DateTimeOffset OccurrenceStartsAt { get; set; }

    /// <summary>
    /// When the date ended, or when Modbot counted it if that came first. A moment is new when the person
    /// reached its number after the milestones were last turned on, with a date that ended after then:
    /// an event that was running at that time counts, and history counted for the first time never does.
    /// </summary>
    public DateTimeOffset CountedAt { get; set; }
}

/// <summary>
/// What Modbot keeps about one VRChat account for the milestones. The table is
/// <c>member_milestone</c>, one row for each account.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Kept, not worked out each time.</strong> The last visit has to outlive the facts it came
/// from: the fact log is pruned, and "back after 60 days" asks about a gap longer than most
/// retention settings. The count of events is the other way round, read from <see cref="EventAttendee"/>.
/// </para>
/// <para>
/// The <c>...HandledAt</c> columns are the once-only marks: written with a conditional update before
/// a post or a role, so two copies of Modbot or a restart never do the same one twice.
/// </para>
/// <para>
/// Names a VRChat account, so a purge of that account deletes its row.
/// </para>
/// </remarks>
public class MemberMilestone
{
    /// <summary>The VRChat user id. An opaque string; never parsed.</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>The last time the person was seen arriving in a group instance, as far as the scan has read.</summary>
    public DateTimeOffset? LastVisitAt { get; set; }

    /// <summary>The visit that ended the latest gap of the welcome-back length or more. Null when there has been none.</summary>
    public DateTimeOffset? BackAt { get; set; }

    /// <summary>How many whole days that gap was.</summary>
    public int? BackAfterDays { get; set; }

    /// <summary>The <see cref="BackAt"/> that was last dealt with, posted or left out. A later return is a new moment.</summary>
    public DateTimeOffset? BackHandledFor { get; set; }

    /// <summary>When the first-event moment was dealt with, posted or left out. Null while it is waiting.</summary>
    public DateTimeOffset? FirstHandledAt { get; set; }

    /// <summary>When the Regular moment was dealt with, posted or left out. Null while it is waiting.</summary>
    public DateTimeOffset? RegularHandledAt { get; set; }

    /// <summary>When Modbot gave the Regular role. Null until it has.</summary>
    public DateTimeOffset? RegularRoleGivenAt { get; set; }
}
