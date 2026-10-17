namespace Modbot.Core.Data.Entities;

/// <summary>
/// A saved set of everything the event form holds except its dates, for making an event from
/// (calendar templates plan). The table is <c>calendar_template</c>.
/// </summary>
/// <remarks>
/// <para>
/// The fields are kept as JSON in <see cref="Fields"/>, in the shape the event form fills in, so a
/// field the form gains later is one more property rather than one more column. The two things a
/// template can name that something else deletes are real columns: the Discord picture, so the
/// hourly clean-up of unused pictures leaves it alone, and the world list. Each is set to nothing
/// when what it names is deleted, as an event's are. <see cref="Fields"/> never holds either.
/// The Bluesky post's pictures are the one exception: they stay in <see cref="Fields"/>, and the
/// clean-up reads them from there (CalendarBlueskyPicturesInUse) so it leaves them alone too.
/// </para>
/// </remarks>
public class CalendarTemplate
{
    public const int MaxNameLength = 100;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The event fields as JSON, without the Discord picture and the world list.</summary>
    public string Fields { get; set; } = "{}";

    /// <summary>The picture cropped for Discord (calendar design §15.4), or null.</summary>
    public Guid? CoverPictureId { get; set; }

    /// <summary>The world list the event picks its world from, or null.</summary>
    public Guid? WorldListId { get; set; }

    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
