namespace Modbot.Core.Data.Entities;

/// <summary>
/// One ban in the Discord server, as the bot last saw it. The table is <c>discord_ban</c>.
/// </summary>
/// <remarks>
/// <para>
/// Current state, like <see cref="GroupBan"/> is for the VRChat group: the history -- who banned,
/// when, and every unban -- is in the fact log. This row is what the Discord side of the Bans page
/// lists (Discord page review, 2026-09-27 decisions).
/// </para>
/// <para>
/// The whole list is read from Discord when the bot signs in and once a day, which needs Ban
/// Members; a ban or unban seen on the gateway changes its one row between reads, with no request.
/// A lifted ban keeps its row with <see cref="LiftedAt"/> set, as a VRChat one does.
/// </para>
/// </remarks>
public class DiscordBan
{
    public string GuildId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    /// <summary>Their Discord username, when a read or the member list gave one.</summary>
    public string? Username { get; set; }

    /// <summary>The name to show: the member's display name if they were a member, else their global name, else the username. Untrusted text.</summary>
    public string? DisplayName { get; set; }

    public string? AvatarUrl { get; set; }

    /// <summary>The reason the moderator gave, from the ban list or the audit log. Untrusted text.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// When the ban was issued, when Modbot saw it happen or the audit log said. Null for a ban that
    /// was already there when the list was read: Discord's list carries no date.
    /// </summary>
    public DateTimeOffset? BannedAt { get; set; }

    /// <summary>When Modbot first saw this ban, in a read or as it happened.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When the ban was lifted, or null while it stands. Cleared if they are banned again.</summary>
    public DateTimeOffset? LiftedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
