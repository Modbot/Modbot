namespace Modbot.Core.Data.Entities;

/// <summary>What invite tracking is limited to, in one place for the page, the save and the create.</summary>
public static class DiscordInviteLimits
{
    /// <summary>The longest name staff give an invite ("Partner: Some Server").</summary>
    public const int MostNameLength = 80;

    /// <summary>The most invites made in one go from a list of names.</summary>
    public const int MostCreatedAtOnce = 20;

    /// <summary>The most days one report covers.</summary>
    public const int MostDays = 365;
}

/// <summary>
/// An invite link to the Discord server that Modbot counts joins for, or the server's vanity link.
/// The table is <c>discord_invite</c>.
/// </summary>
/// <remarks>
/// Discord tells which invite a person used only as a count that goes up, so Modbot keeps the count
/// it last accounted for (<see cref="Uses"/>) and, when somebody joins, finds the invite whose count
/// went up.
/// </remarks>
public class DiscordInviteLink
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The code in the link (<c>discord.gg/code</c>). Text, never parsed.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// What staff call the source of the joins ("Linktree", "Partner: X"), or null while the invite
    /// has no name. Invites with the same name are one source.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>The server's vanity link. It is its own source.</summary>
    public bool IsVanity { get; set; }

    /// <summary>The number of uses Modbot has accounted for, which is not always Discord's own number.</summary>
    public int Uses { get; set; }

    /// <summary>How many uses the invite allows, or 0 for no limit. A used-up invite is deleted by Discord.</summary>
    public int MaxUses { get; set; }

    /// <summary>The channel the invite leads to. Text, never parsed.</summary>
    public string? ChannelId { get; set; }

    /// <summary>The Discord account that made the invite. Text, never parsed.</summary>
    public string? InviterId { get; set; }

    /// <summary>Modbot made the invite from a list of names.</summary>
    public bool MadeByModbot { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>
    /// When the invite runs out of time, or null for one that never does. An invite that goes missing
    /// before this was deleted or used up, not expired.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>When Modbot found the invite deleted, expired or used up, or null while it is live.</summary>
    public DateTimeOffset? GoneAt { get; set; }
}

/// <summary>
/// One person joining the Discord server, and the invite they used when Modbot could tell. The
/// table is <c>discord_invite_join</c>.
/// </summary>
public class DiscordInviteJoin
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The Discord account that joined. Text, never parsed.</summary>
    public string UserId { get; set; } = string.Empty;

    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>The invite used, or null when Modbot could not tell which.</summary>
    public Guid? InviteId { get; set; }
}
