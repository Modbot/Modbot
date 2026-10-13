namespace Modbot.Core.Data.Entities;

/// <summary>What a ticket type may hold, in one place for the page, the save and the panel.</summary>
public static class DiscordTicketLimits
{
    /// <summary>A button's label. Discord ends one at 80.</summary>
    public const int MostNameLength = 80;

    /// <summary>The opening message in the ticket. Discord ends a message at 2,000.</summary>
    public const int MostOpeningLength = 1500;

    /// <summary>The words above the buttons on the panel.</summary>
    public const int MostPanelTextLength = 1500;

    /// <summary>Discord holds five buttons in a row; the panel uses one row.</summary>
    public const int MostTypes = 5;
}

/// <summary>Where a ticket is.</summary>
public static class DiscordTicketStates
{
    /// <summary>The thread is there and the member can write in it.</summary>
    public const string Open = "open";

    /// <summary>Locked and archived, with the transcript kept if a channel was set.</summary>
    public const string Closed = "closed";

    /// <summary>The thread is gone.</summary>
    public const string Deleted = "deleted";
}

/// <summary>
/// One kind of ticket members can open from a panel: its own button label, staff role and opening
/// message. The table is <c>discord_ticket_type</c>.
/// </summary>
public class DiscordTicketType
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The words on the panel's button, and in the ticket's name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The Discord role that is asked into every ticket of this type, and may close and delete them.
    /// Text, never parsed. Null means only people who may manage threads.
    /// </summary>
    public string? StaffRoleId { get; set; }

    /// <summary>The first message in the ticket, under the member's name.</summary>
    public string OpeningMessage { get; set; } = string.Empty;

    /// <summary>
    /// Members may post photos or documents that must not be kept, such as an ID for an age check.
    /// The transcript of such a ticket holds text only.
    /// </summary>
    public bool HoldsPrivateImages { get; set; }

    /// <summary>The order buttons appear in, lowest first.</summary>
    public int Position { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One ticket a member opened: a private thread in Discord and who it is with. The table is
/// <c>discord_ticket</c>.
/// </summary>
/// <remarks>
/// A member has one open ticket of each type at a time; the database refuses a second. The type's
/// name is copied here so history still reads right after the type is renamed or removed.
/// </remarks>
public class DiscordTicket
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The type it was opened under. Not a reference: removing a type keeps its tickets.</summary>
    public Guid TypeId { get; set; }

    public string TypeName { get; set; } = string.Empty;

    /// <summary>The Discord account that opened it.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    public string? DiscordUserName { get; set; }

    /// <summary>The thread's id. Text, never parsed.</summary>
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>The channel the panel was in, which the thread sits under.</summary>
    public string ChannelId { get; set; } = string.Empty;

    /// <summary>One of <see cref="DiscordTicketStates"/>.</summary>
    public string State { get; set; } = DiscordTicketStates.Open;

    /// <summary>Copied from the type when the ticket was opened, so the rule holds if the type changes later.</summary>
    public bool HoldsPrivateImages { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>The Discord account that closed it, or null while open.</summary>
    public string? ClosedByDiscordUserId { get; set; }

    public DateTimeOffset? DeletedAt { get; set; }
}
