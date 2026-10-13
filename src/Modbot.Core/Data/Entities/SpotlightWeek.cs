namespace Modbot.Core.Data.Entities;

/// <summary>How the member for the week is picked (spotlight design §3).</summary>
public static class SpotlightModes
{
    /// <summary>Modbot picks one of the members who opted in.</summary>
    public const string Random = "random";

    /// <summary>Staff pick one of the members who opted in, from the Marketing page.</summary>
    public const string Staff = "staff";

    public static bool IsKnown(string? mode) => mode is Random or Staff;
}

/// <summary>
/// One week's spotlight: the draft Modbot made and the member it features. The table is
/// <c>spotlight_week</c>; it is also how Modbot knows whom it featured lately (spotlight design §4).
/// </summary>
public class SpotlightWeek
{
    public Guid Id { get; set; }

    /// <summary>The Monday of the week it is for, in the spotlight's time zone. One row per week.</summary>
    public DateOnly Week { get; set; }

    /// <summary>
    /// The member featured, a Discord user id. Null until staff pick one, and again after a purge of
    /// that member. Opaque text, never parsed.
    /// </summary>
    public string? DiscordUserId { get; set; }

    /// <summary>The Marketing draft. Null once the post is deleted.</summary>
    public Guid? PostId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When a member was picked: at creation in random mode, later when staff pick.</summary>
    public DateTimeOffset? PickedAt { get; set; }
}
