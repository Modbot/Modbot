namespace Modbot.Core.Data.Entities;

/// <summary>
/// A category an admin made in Modbot, such as Karaoke or Movie night, that an event can carry any
/// number of. They add to the fixed list VRChat gives <see cref="CalendarEvent.Category"/>, and are
/// for Discord and the other places VRChat's list is too short for. The table is
/// <c>calendar_category</c>.
/// </summary>
/// <remarks>
/// <para>
/// An event keeps its categories through <see cref="CalendarEvent.Categories"/>, joined in
/// <c>calendar_event_category</c>. The id is the category's whole identity: a rename changes the name
/// every event shows and nothing else, and anything that later needs to follow a category, such as
/// people asking to hear about it, holds this id.
/// </para>
/// <para>
/// Nothing here is about a member: a name and who made it. A purge has nothing to find in it.
/// </para>
/// </remarks>
public class CalendarCategory
{
    public const int MaxNameLength = 40;

    /// <summary>The most characters kept for a tag typed on a category: Bluesky takes 64 graphemes, which can be more characters.</summary>
    public const int MaxBlueskyTagLength = 256;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The tag an event's Bluesky post ends with, without the <c>#</c>. Null is the one the name makes
    /// (<see cref="Modbot.Core.Bluesky.BlueskyTags.FromName"/>), so a rename changes it; empty is none.
    /// </summary>
    public string? BlueskyTag { get; set; }

    /// <summary>The account that made it. Null once that account is gone.</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
