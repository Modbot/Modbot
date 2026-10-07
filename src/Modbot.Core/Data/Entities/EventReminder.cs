namespace Modbot.Core.Data.Entities;

/// <summary>The words stored in <see cref="EventReminder.State"/> (Discord commands design §3.5).</summary>
public static class EventReminderStates
{
    /// <summary>Asked for, and not yet due. The only state a member can stop.</summary>
    public const string Waiting = "waiting";

    /// <summary>
    /// The direct message is going out. Written before it is sent, so a crash between the two leaves
    /// it here, counted as sent and never sent again.
    /// </summary>
    public const string Sending = "sending";

    /// <summary>The direct message went out.</summary>
    public const string Sent = "sent";

    /// <summary>The member pressed Stop.</summary>
    public const string Stopped = "stopped";

    /// <summary>
    /// Not sent because there was nothing to remind about by then: the date or the event was
    /// cancelled, the event is gone, the date started, or the reminder was more than 15 minutes late.
    /// </summary>
    public const string Skipped = "skipped";

    /// <summary>Discord did not take the message (direct messages closed, or a refusal).</summary>
    public const string Failed = "failed";

    /// <summary>States a reminder never leaves: the ones the 30-day clean-up deletes.</summary>
    public static bool IsFinished(string state) => state is Sent or Stopped or Skipped or Failed;
}

/// <summary>
/// One member's request for a direct message before one date of an event, made with
/// <c>/remindme</c> in Discord. The table is <c>event_reminder</c> (Discord commands design §3.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Running the command is the opt-in</strong> for this one reminder, and Modbot proved direct
/// messages reach the member by sending a confirmation first: a member whose direct messages are
/// closed gets no row at all. Event invites are separate -- neither signs anybody up for the other.
/// </para>
/// <para>
/// <strong>Keyed on the date's planned start.</strong> A date moved on its own keeps the time it was
/// planned for as its name (<c>CalendarOccurrence.PlannedStartsAt</c>), so the reminder follows the
/// move: <see cref="RemindAt"/> is worked out again from the date as it stands when the reminder is
/// due. Only the next date of a repeating event is reminded (decision 9).
/// </para>
/// <para>
/// One waiting reminder per member per date, and at most ten waiting per member. The ids are opaque
/// text, never parsed or validated (foundation §3.1.1). A purge deletes the person's rows; sent,
/// stopped, skipped and failed rows are deleted 30 days after they last changed.
/// </para>
/// </remarks>
public class EventReminder
{
    /// <summary>The most reminders one member can have waiting at once.</summary>
    public const int MostWaiting = 10;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The Discord account that asked, and the one the message goes to. A snowflake, never parsed.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    public Guid EventId { get; set; }

    /// <summary>The date it is about, as its repeat plans it: <c>CalendarOccurrence.PlannedStartsAt</c>.</summary>
    public DateTimeOffset OccurrenceStartsAt { get; set; }

    /// <summary>How long before the date starts the member asked to be told: 15, 60 or 1,440.</summary>
    public int MinutesBefore { get; set; }

    /// <summary>
    /// When the message is due: the date's start less <see cref="MinutesBefore"/>. Worked out again
    /// when the date has been moved.
    /// </summary>
    public DateTimeOffset RemindAt { get; set; }

    /// <summary>One of <see cref="EventReminderStates"/>.</summary>
    public string State { get; set; } = EventReminderStates.Waiting;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the state last changed. What the 30-day clean-up counts from.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the direct message was sent, or about to be.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>When the member pressed Stop.</summary>
    public DateTimeOffset? StoppedAt { get; set; }
}
