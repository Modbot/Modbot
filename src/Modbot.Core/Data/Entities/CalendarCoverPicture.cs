namespace Modbot.Core.Data.Entities;

/// <summary>
/// A picture cropped in the event form for Discord: the event's cover and the channel post's picture
/// (calendar design §15.4, added 2026-10-02).
/// </summary>
/// <remarks>
/// <para>
/// Kept by Modbot because nothing else can keep it. Discord is sent the bytes of a cover each time
/// the event is made or changed, and the crop is made in the browser, so the cropped picture has to
/// live somewhere between the form and the next pass of the Discord loop. VRChat's picture does not
/// need this: VRChat keeps the file and Modbot keeps its id.
/// </para>
/// <para>
/// One row per upload, never changed. An event points at one (<see cref="CalendarEvent.CoverPictureId"/>);
/// a row no live event points at is deleted when its event is deleted or given another cover, and
/// one uploaded but never saved is deleted once it is a day old (<see cref="Calendar.CalendarCoverSweep"/>).
/// </para>
/// </remarks>
public class CalendarCoverPicture
{
    /// <summary>The largest cover kept: 8 MB, under Discord's 10 MB for a cover.</summary>
    public const int MaxBytes = 8 * 1024 * 1024;

    public Guid Id { get; set; }

    /// <summary>The picture: a PNG or JPEG, or a GIF or WebP, as its first bytes say.</summary>
    public byte[] Bytes { get; set; } = [];

    /// <summary>The type its bytes have.</summary>
    public string ContentType { get; set; } = "image/png";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who uploaded it.</summary>
    public Guid? CreatedByUserId { get; set; }
}
