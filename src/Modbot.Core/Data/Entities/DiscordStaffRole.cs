namespace Modbot.Core.Data.Entities;

/// <summary>Which way a staff role mapping works (staff roles from Discord design §3).</summary>
/// <remarks>Stored as text so the database, the API and the web app all read the same word.</remarks>
public static class StaffRoleDirections
{
    /// <summary>Discord's role decides the Modbot role. Nothing in Discord is ever changed.</summary>
    public const string Discord = "discord";

    /// <summary>As <see cref="Discord"/>, and a change to the Modbot role in Modbot is made in Discord too.</summary>
    public const string Both = "both";

    public static bool IsKnown(string? value) => value is Discord or Both;
}

/// <summary>
/// One Discord role that gives one Modbot role. The table is <c>discord_staff_role</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only what somebody maps syncs</strong> (design §2). There are no rows until a person
/// makes one, and a Modbot role or a Discord role that no row names is never looked at.
/// </para>
/// <para>
/// Several Discord roles may give the same Modbot role, and holding any one of them is enough. One
/// Discord role gives one Modbot role, which the database holds to. A both-ways row must be the only
/// one for its Modbot role, because giving the Modbot role has to say which Discord role to give;
/// that is checked when it is saved.
/// </para>
/// </remarks>
public class DiscordStaffRole
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The Discord role's id. Text, never parsed.</summary>
    public string DiscordRoleId { get; set; } = string.Empty;

    /// <summary>What the Discord role was called when the row was last saved, for history.</summary>
    public string? DiscordRoleName { get; set; }

    /// <summary>The Modbot role it gives.</summary>
    public Guid RoleId { get; set; }

    public ModbotRole Role { get; set; } = null!;

    /// <summary>One of <see cref="StaffRoleDirections"/>.</summary>
    public string Direction { get; set; } = StaffRoleDirections.Discord;

    /// <summary>The account that made it, or null once that account is gone.</summary>
    public Guid? CreatedById { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The last change in Discord this row asked for that was refused, as a sentence.</summary>
    public string? Problem { get; set; }
}

/// <summary>
/// For one account and one both-ways mapping, whether the two sides last agreed that the account
/// holds the role. The table is <c>discord_staff_role_state</c>.
/// </summary>
/// <remarks>
/// What keeps a both-ways mapping from bouncing (design §3.1): each side is compared with this
/// rather than with the other side, so only the side that changed is copied, and the update that
/// comes back from Discord after the bot's own change matches it and is nothing to do.
/// </remarks>
public class DiscordStaffRoleState
{
    public Guid MappingId { get; set; }

    public Guid UserId { get; set; }

    public bool Held { get; set; }

    /// <summary>When the two sides last agreed.</summary>
    public DateTimeOffset AgreedAt { get; set; }
}
