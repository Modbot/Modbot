namespace Modbot.Core.Data.Entities;

/// <summary>Where a <see cref="VRChatAnnouncement"/> is in its life.</summary>
public static class VRChatAnnouncementStates
{
    /// <summary>Has a time that has not come yet, or has come and the next pass sends it.</summary>
    public const string Scheduled = "scheduled";

    /// <summary>Claimed and on its way: <see cref="VRChatAnnouncement.SentAt"/> was written before VRChat was asked.</summary>
    public const string Sending = "sending";

    /// <summary>VRChat accepted it.</summary>
    public const string Sent = "sent";

    /// <summary>VRChat answered and said no: its status and its own words are kept.</summary>
    public const string Refused = "refused";

    /// <summary>
    /// Not sent, for a reason of Modbot's own (the instance closed, too late, a rate limit), or
    /// VRChat gave no clear answer and it may have gone. Never sent again by itself.
    /// </summary>
    public const string Failed = "failed";

    /// <summary>Called off before it went.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Finished, one way or another: nothing more happens to it by itself.</summary>
    public static bool IsDone(string state) => state is Sent or Refused or Failed or Cancelled;
}

/// <summary>
/// One message Modbot sends to everyone in one of the group's instances: the pop-up VRChat shows in
/// the instance, sent now or at a time. The table is <c>vrchat_announcement</c>.
/// </summary>
/// <remarks>
/// <para>
/// Not the Discord card about an instance (<see cref="VRChatInstance.AnnouncementMessageId"/>):
/// this one is shown inside VRChat, to the people standing in the instance.
/// </para>
/// <para>
/// <strong>At most once.</strong> <see cref="SentAt"/> and <see cref="VRChatAnnouncementStates.Sending"/>
/// are saved before VRChat is asked, and nothing sends a row again: a refusal, a rate limit or an
/// answer that was not clear each end it, with what happened written on it.
/// </para>
/// <para>
/// <strong><see cref="Version"/> is the concurrency token</strong>, so a Cancel saved while the
/// sender is claiming it fails one or the other, never both.
/// </para>
/// </remarks>
public class VRChatAnnouncement
{
    /// <summary>The longest title kept. VRChat's own limit is not known; it is under 300.</summary>
    public const int MaxTitleLength = 100;

    /// <summary>The longest message kept. VRChat's own limit is not known; it is under 3000.</summary>
    public const int MaxMessageLength = 500;

    public Guid Id { get; set; }

    /// <summary>Modbot's own id for the instance (<see cref="VRChatInstance.Id"/>).</summary>
    public Guid InstanceId { get; set; }

    /// <summary>
    /// The instance's location exactly as VRChat gave it (<see cref="VRChatInstance.Location"/>),
    /// copied when the announcement was written: it is the address the message goes to.
    /// </summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>The managed group when it was written. A row for another group is never sent.</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>The title VRChat is sent. Plain text, one line.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>The message VRChat is sent. Plain text, one line.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>One of <see cref="VRChatAnnouncementStates"/>.</summary>
    public string State { get; set; } = VRChatAnnouncementStates.Scheduled;

    /// <summary>When it goes. Send now is saved as the time it was pressed.</summary>
    public DateTimeOffset SendAt { get; set; }

    /// <summary>The zone the time was picked in, used to show it.</summary>
    public string TimeZone { get; set; } = "UTC";

    /// <summary>When it was claimed, written before VRChat was asked.</summary>
    public DateTimeOffset? SentAt { get; set; }

    /// <summary>The HTTP status VRChat answered with, when it answered.</summary>
    public int? StatusCode { get; set; }

    /// <summary>What went wrong, in VRChat's own words when it gave any.</summary>
    public string? Error { get; set; }

    /// <summary>The group permission VRChat said Modbot's account lacks, when it said so.</summary>
    public string? MissingPermission { get; set; }

    /// <summary>Raised on every write; the concurrency token.</summary>
    public int Version { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
