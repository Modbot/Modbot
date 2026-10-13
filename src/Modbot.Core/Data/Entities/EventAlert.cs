namespace Modbot.Core.Data.Entities;

/// <summary>The numbers and words event alerts share (event alerts design).</summary>
public static class EventAlertSettings
{
    /// <summary>The fewest minutes before the start an alert may go.</summary>
    public const int LeastMinutesBefore = 5;

    /// <summary>The most minutes before the start an alert may go: one day.</summary>
    public const int MostMinutesBefore = 1440;

    /// <summary>What a server that never chose gets.</summary>
    public const int DefaultMinutesBefore = 60;

    /// <summary>A member's way of being told: a direct message.</summary>
    public const string Direct = "direct";

    /// <summary>A member's way of being told: a ping in the chosen channel.</summary>
    public const string Ping = "ping";

    public static bool IsWay(string? way) => way is Direct or Ping;
}

/// <summary>The words stored in <see cref="EventAlertDelivery.State"/>.</summary>
public static class EventAlertDeliveryStates
{
    /// <summary>Queued, not yet sent.</summary>
    public const string Waiting = "waiting";

    /// <summary>
    /// Going out. Written before the message is sent, so a restart between the two leaves it here,
    /// counted as sent and never sent again.
    /// </summary>
    public const string Sending = "sending";

    public const string Sent = "sent";

    /// <summary>Discord did not take it (direct messages closed, a refusal, a rate limit, no clear answer). Never tried again.</summary>
    public const string Failed = "failed";

    /// <summary>Not sent because there was nothing left to alert about: the event or date went, the date started, the member left.</summary>
    public const string Skipped = "skipped";

    public static bool IsFinished(string state) => state is Sent or Failed or Skipped;
}

/// <summary>
/// One member's wish to hear before events of one category, or before one event. The table is
/// <c>event_alert_signup</c> (event alerts design §4).
/// </summary>
/// <remarks>
/// <para>
/// Exactly one of <see cref="CategoryId"/> and <see cref="EventId"/> is set. The category is the
/// ordinary case, made with <c>/notify</c> or the Notify me button on an event that carries
/// categories; the event is for the button on an event that carries none. Both cascade, so a deleted
/// category or event takes its sign-ups with it. A rename changes nothing here: the id is the
/// identity.
/// </para>
/// <para>
/// The Discord id is opaque text, never parsed or validated (foundation §3.1.1). A purge deletes
/// the person's rows.
/// </para>
/// </remarks>
public class EventAlertSignup
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string DiscordUserId { get; set; } = string.Empty;

    public Guid? CategoryId { get; set; }

    public Guid? EventId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A member's choice of how they are told, when both ways are on. The table is
/// <c>event_alert_member</c>. A member with no row is told by direct message, or by ping where
/// direct messages are off.
/// </summary>
public class EventAlertMember
{
    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>One of <see cref="EventAlertSettings.Direct"/> and <see cref="EventAlertSettings.Ping"/>.</summary>
    public string Way { get; set; } = EventAlertSettings.Direct;

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One date of an event that has been alerted. The table is <c>event_alert</c>; the row is the
/// promise that a date alerts once (event alerts design §5).
/// </summary>
/// <remarks>
/// Keyed on the event and the date's planned start (<c>CalendarOccurrence.PlannedStartsAt</c>), so a
/// date moved on its own is the same date. For an event that does not repeat any row counts, so a
/// start moved after the alert was queued does not alert again. Deleted 30 days after it was queued,
/// and its deliveries with it.
/// </remarks>
public class EventAlert
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid EventId { get; set; }

    /// <summary>The date it is about, as its repeat plans it.</summary>
    public DateTimeOffset OccurrenceStartsAt { get; set; }

    public DateTimeOffset QueuedAt { get; set; }

    /// <summary>When the last delivery was done and the alert's fact written; null while any is waiting.</summary>
    public DateTimeOffset? FinishedAt { get; set; }
}

/// <summary>
/// One member's alert for one <see cref="EventAlert"/>: a direct message, or a mention in the ping.
/// The table is <c>event_alert_delivery</c>. It names the member by Discord id, so it is deleted with
/// its alert and by a purge.
/// </summary>
public class EventAlertDelivery
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid AlertId { get; set; }

    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>One of <see cref="EventAlertSettings.Direct"/> and <see cref="EventAlertSettings.Ping"/>.</summary>
    public string Way { get; set; } = EventAlertSettings.Direct;

    /// <summary>One of <see cref="EventAlertDeliveryStates"/>.</summary>
    public string State { get; set; } = EventAlertDeliveryStates.Waiting;

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>True when a failed direct message was refused because the member's direct messages are closed. Counted in the alert's fact.</summary>
    public bool DirectMessagesClosed { get; set; }
}
