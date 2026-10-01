namespace Modbot.Core.Data.Entities;

/// <summary>
/// A list of worlds kept in Modbot, for an event to pick its world from (world lists design). The
/// table is <c>world_list</c>.
/// </summary>
/// <remarks>
/// Nothing here is about a member: a name, some worlds and the players each is for. A purge has
/// nothing to find in it.
/// </remarks>
public class WorldList
{
    public const int MaxNameLength = 100;

    /// <summary>How many worlds one list may hold. A size for a form, not a VRChat limit.</summary>
    public const int MaxWorlds = 500;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The account that made it. Null once that account is gone.</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One world in a list, and the fewest and most players its game is for. The table is
/// <c>world_list_item</c>.
/// </summary>
/// <remarks>
/// The world id is opaque text, never checked for shape (foundation §3.1.1). The player numbers are
/// what the list's maker says the game needs, never a capacity Modbot holds anybody to (§3.1).
/// </remarks>
public class WorldListItem
{
    public Guid ListId { get; set; }

    public string WorldId { get; set; } = string.Empty;

    /// <summary>The fewest players the game is for. Null fits any number.</summary>
    public int? MinPlayers { get; set; }

    /// <summary>The most players the game is for. Null fits any number.</summary>
    public int? MaxPlayers { get; set; }

    /// <summary>Where it sits on the page: the order it was added in.</summary>
    public int Position { get; set; }
}

/// <summary>
/// One list's shuffle for one event (world lists design §4): this round's order and which of it has
/// been played. The table is <c>world_list_shuffle</c>, keyed by list and event.
/// </summary>
/// <remarks>
/// In the database rather than in memory, so a restart carries on with the same round and a world is
/// never played twice in one.
/// </remarks>
public class WorldListShuffle
{
    public Guid ListId { get; set; }

    public Guid EventId { get; set; }

    /// <summary>This round's worlds, in the order they come up.</summary>
    public List<string> Order { get; set; } = [];

    /// <summary>The worlds of this round already played.</summary>
    public List<string> Played { get; set; } = [];

    /// <summary>The last world played, so a new round does not start with it.</summary>
    public string? LastPlayed { get; set; }

    /// <summary>How many rounds have been shuffled, the first being 1.</summary>
    public int Round { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The words stored in <see cref="WorldPick.Kind"/>.</summary>
public static class WorldPickKinds
{
    /// <summary>The world for one date of an event, picked when that date became the current one.</summary>
    public const string Date = "date";

    /// <summary>A world picked during an event with Next game.</summary>
    public const string Game = "game";
}

/// <summary>
/// A world picked from a list for an event: for a date, or as the next game during it. The table is
/// <c>world_pick</c>.
/// </summary>
/// <remarks>
/// <para>
/// A date has at most one <see cref="WorldPickKinds.Date"/> pick that has not been put back, held by a
/// unique index, which is what locks a date's world (world lists design §5).
/// </para>
/// <para>
/// A pick that was replaced -- Pick again, Pick another, the event changed to another list -- is kept
/// with <see cref="PutBackAt"/> set, so the facts and the page can say what was there before.
/// </para>
/// </remarks>
public class WorldPick
{
    public Guid Id { get; set; }

    public Guid EventId { get; set; }

    /// <summary>The start of the date it was picked for.</summary>
    public DateTimeOffset OccurrenceStartsAt { get; set; }

    /// <summary>One of <see cref="WorldPickKinds"/>.</summary>
    public string Kind { get; set; } = WorldPickKinds.Date;

    /// <summary>The list it was picked from. Kept after the list is deleted, for the history.</summary>
    public Guid ListId { get; set; }

    public string WorldId { get; set; } = string.Empty;

    /// <summary>How many people the player range was matched against. Null when it was ignored.</summary>
    public int? People { get; set; }

    public DateTimeOffset PickedAt { get; set; }

    /// <summary>The account that picked it. Null when Modbot picked a date by itself.</summary>
    public Guid? PickedByUserId { get; set; }

    /// <summary>When it was replaced and its world put back in the shuffle. Null while it stands.</summary>
    public DateTimeOffset? PutBackAt { get; set; }
}
