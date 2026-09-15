namespace Modbot.Core.Data.Entities;

/// <summary>Which side a member proved first, as stored in <see cref="DiscordAccountLink.StartedFrom"/>.</summary>
public static class LinkStartedFrom
{
    public const string Discord = "discord";
    public const string VRChat = "vrchat";
}

/// <summary>Who ended a link, as stored in <see cref="DiscordAccountLink.UnlinkedBy"/>.</summary>
public static class LinkEndedBy
{
    /// <summary>The member, from the link page.</summary>
    public const string Member = "member";

    /// <summary>A moderator, from the person popup.</summary>
    public const string Moderator = "moderator";

    /// <summary>The same Discord account linked a different VRChat account.</summary>
    public const string Replaced = "replaced";
}

/// <summary>
/// One Discord account and one VRChat account recorded as the same person, after both were proved
/// (Discord account linking design §2). The table is <c>discord_account_link</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Rows are never deleted.</strong> An ended link keeps its row with
/// <see cref="UnlinkedAt"/> set (M5 §2.4): unlinking stops the roles, not the history. At most one
/// row per Discord account and one per VRChat account may be active, which partial unique indexes
/// enforce.
/// </para>
/// <para>
/// <see cref="LinkedRoleId"/> and <see cref="EighteenPlusRoleId"/> are the roles Modbot gave and
/// believes the member still holds -- not the roles they should hold. The role job compares them
/// with what should be held and changes the difference, so a row with a role recorded and the link
/// ended is exactly "a role still to take away".
/// </para>
/// </remarks>
public class DiscordAccountLink
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Discord's id for the account. Text, never parsed.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>The Discord username when the link was saved.</summary>
    public string DiscordUsername { get; set; } = string.Empty;

    /// <summary>The VRChat user id. Never validated (foundation §3.1.1).</summary>
    public string VRChatUserId { get; set; } = string.Empty;

    /// <summary>The VRChat display name when the link was saved.</summary>
    public string? VRChatDisplayName { get; set; }

    /// <summary>One of <see cref="LinkStartedFrom"/>.</summary>
    public string StartedFrom { get; set; } = LinkStartedFrom.Discord;

    public DateTimeOffset LinkedAt { get; set; }

    /// <summary>Null while the link is active.</summary>
    public DateTimeOffset? UnlinkedAt { get; set; }

    /// <summary>One of <see cref="LinkEndedBy"/>, once ended.</summary>
    public string? UnlinkedBy { get; set; }

    /// <summary>The Modbot account that ended it, when a moderator did.</summary>
    public Guid? UnlinkedByUserId { get; set; }

    /// <summary>The linked role Modbot gave and believes the member still holds.</summary>
    public string? LinkedRoleId { get; set; }

    /// <summary>The 18+ role Modbot gave and believes the member still holds.</summary>
    public string? EighteenPlusRoleId { get; set; }

    /// <summary>
    /// When Discord last said this member is not in the server. The role job leaves the row alone
    /// for a day, or until the member joins.
    /// </summary>
    public DateTimeOffset? NotInServerAt { get; set; }

    /// <summary>The last role change Discord refused, as a sentence. Cleared by the next one that works.</summary>
    public string? RoleError { get; set; }

    public bool IsActive => UnlinkedAt is null;
}

/// <summary>
/// A VRChat bio code waiting to be checked, for one signed-in Discord account. The table is
/// <c>discord_link_code</c>.
/// </summary>
/// <remarks>
/// Keyed by the Discord account so the check limits count per account: a code handed out before
/// Discord sign-in lives in the page's cookie and costs nothing until it arrives here.
/// </remarks>
public class DiscordLinkCode
{
    public string DiscordUserId { get; set; } = string.Empty;

    public string VRChatUserId { get; set; } = string.Empty;

    /// <summary>The <c>modbot-XXXXXX</c> code to find in the bio.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>One of <see cref="LinkStartedFrom"/>.</summary>
    public string StartedFrom { get; set; } = LinkStartedFrom.Discord;

    public DateTimeOffset ExpiresAt { get; set; }

    public int Checks { get; set; }

    public DateTimeOffset? LastCheckAt { get; set; }
}
