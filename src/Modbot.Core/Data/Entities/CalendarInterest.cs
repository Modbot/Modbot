namespace Modbot.Core.Data.Entities;

/// <summary>
/// How many people had said they were interested in one date of an event when it started: Discord's
/// count on the server event and VRChat's count on its calendar. The table is <c>calendar_interest</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Once per date and side.</strong> Each count is written the first time it is read after
/// the date has started and never changed after, so the number is what it was at the start and not
/// what it grew to later. A side that could not be read (the event was not on that site, or the
/// site did not answer in time) stays null.
/// </para>
/// <para>
/// The date is known by its start, as <see cref="CalendarOpening.OccurrenceStartsAt"/> is, so the
/// two line up with the people who came. Counts only: nobody is named.
/// </para>
/// </remarks>
public class CalendarInterest
{
    public Guid EventId { get; set; }

    /// <summary>The date's start, as the calendar's repeat or a move says it.</summary>
    public DateTimeOffset OccurrenceStartsAt { get; set; }

    /// <summary>How many people were interested on Discord. Null when it was not read.</summary>
    public int? DiscordInterested { get; set; }

    /// <summary>When the Discord count was read. Null until then.</summary>
    public DateTimeOffset? DiscordTakenAt { get; set; }

    /// <summary>How many people were interested on VRChat. Null when it was not read.</summary>
    public int? VRChatInterested { get; set; }

    /// <summary>When the VRChat count was read. Null until then.</summary>
    public DateTimeOffset? VRChatTakenAt { get; set; }
}
