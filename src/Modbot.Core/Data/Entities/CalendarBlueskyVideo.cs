namespace Modbot.Core.Data.Entities;

/// <summary>
/// A video picked in the event form for Bluesky's post, kept until the post goes out.
/// </summary>
/// <remarks>
/// <para>
/// Kept by Modbot because nothing else can keep it: Bluesky deletes a video that no post points to
/// within hours (atproto.com/specs/blob), and an event's post goes out days after the event is saved.
/// So the file is sent to Modbot when it is picked, and to Bluesky only when the post is made.
/// </para>
/// <para>
/// One row per upload, never changed. An event, a template or a Bluesky post still to go points at it;
/// a row nothing live points at is deleted when its event is deleted or given another video, and one
/// uploaded but never saved is deleted once it is a day old (<see cref="Calendar.CalendarCoverSweep"/>).
/// Bytes are kept in the database like <see cref="CalendarCoverPicture"/>'s, capped at <see cref="Files.VideoFormats.MaxBytes"/>.
/// </para>
/// </remarks>
public class CalendarBlueskyVideo
{
    public Guid Id { get; set; }

    /// <summary>The video: MP4, MPEG, WebM or QuickTime, as its first bytes say.</summary>
    public byte[] Bytes { get; set; } = [];

    /// <summary>The type its bytes have.</summary>
    public string ContentType { get; set; } = "video/mp4";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who uploaded it.</summary>
    public Guid? CreatedByUserId { get; set; }
}
