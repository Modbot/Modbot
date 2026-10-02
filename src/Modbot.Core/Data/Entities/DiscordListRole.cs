namespace Modbot.Core.Data.Entities;

/// <summary>
/// One saved list paired with one Discord role: everybody in the list who is in the server holds
/// the role. The table is <c>discord_list_role</c> (roles from lists design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only the role somebody paired is ever touched</strong>, and only ever taken from somebody
/// it was given to by this pairing (<see cref="DiscordListRoleGiven"/>). A role given by hand stays,
/// whatever the list says.
/// </para>
/// <para>
/// One pairing per Discord role. Two lists giving one role would have one taking away what the
/// other gives, every minute, so the database refuses it.
/// </para>
/// </remarks>
public class DiscordListRole
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The saved list. A list a pairing uses cannot be deleted.</summary>
    public Guid ListId { get; set; }

    /// <summary>The Discord role's id. Text, never parsed.</summary>
    public string DiscordRoleId { get; set; } = string.Empty;

    /// <summary>
    /// What the role was called when the pairing was saved, so the audit log can say "Regular"
    /// after the role is gone. The live name is shown from the server's role list.
    /// </summary>
    public string? DiscordRoleName { get; set; }

    /// <summary>Off keeps the pairing and what it gave, and stops Modbot changing anything for it.</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Why the last pass changed nothing, or the last change Discord refused, as a sentence.</summary>
    public string? Problem { get; set; }

    /// <summary>
    /// Since when the pairing has been stopped because a pass would take the role from too many
    /// people at once, or null while it is not (roles from lists design §5).
    /// </summary>
    public DateTimeOffset? StoppedAt { get; set; }

    /// <summary>How many people the stopped pass would have taken the role from.</summary>
    public int? StoppedTaking { get; set; }

    /// <summary>
    /// How many removals somebody looked at and allowed with Apply. Passes carry on past the brake
    /// while no more than this would be taken; cleared once a pass is under the brake again.
    /// </summary>
    public int? RemovalsAllowed { get; set; }

    /// <summary>
    /// How many gives somebody looked at and allowed with Apply after the give brake stopped the
    /// pairing (design §5). Counted off as roles are given; cleared once a pass is under it again.
    /// </summary>
    public int? GivesAllowed { get; set; }
}

/// <summary>
/// A Discord role a list pairing gave somebody, and has not taken back. The table is
/// <c>discord_list_role_given</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This row is the only thing that lets a pairing take a role away</strong> (roles from
/// lists design §3). Somebody who held the role before, or was given it by hand, has no row, and
/// leaving the list takes nothing from them.
/// </para>
/// <para>
/// While the row stands the role is not given again, so a moderator who takes it off somebody by
/// hand is not overruled a minute later. It goes when the person leaves the list or the server, or
/// is erased.
/// </para>
/// </remarks>
public class DiscordListRoleGiven
{
    public Guid ListRoleId { get; set; }

    /// <summary>The Discord account the role was given to.</summary>
    public string DiscordUserId { get; set; } = string.Empty;

    /// <summary>The VRChat account linked to it when the role was given, if any. Kept so an erase finds the row.</summary>
    public string? VRChatUserId { get; set; }

    public DateTimeOffset GivenAt { get; set; }
}
