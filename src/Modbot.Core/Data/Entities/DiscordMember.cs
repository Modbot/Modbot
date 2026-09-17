namespace Modbot.Core.Data.Entities;

/// <summary>
/// One member of the Discord server as last seen. The table is <c>discord_member</c>.
/// </summary>
/// <remarks>
/// <para>
/// Current state, like <see cref="GroupMember"/> is for the VRChat group: the history -- joins,
/// leaves, role changes, bans -- is in the fact log. This row is what those facts are worked out
/// against: an update from Discord carries only the member as they are now, so the roles, nickname
/// and timeout stored here are what "before" was. It is also what the bot compares the member list
/// with after it was offline, to find who joined or left meanwhile.
/// </para>
/// <para>
/// A member who leaves keeps the row with <see cref="LeftAt"/> set, so a rejoin is recognised and a
/// person who left can still be named.
/// </para>
/// </remarks>
public class DiscordMember
{
    public string GuildId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    /// <summary>Nickname, else global name, else username: the name the server shows. Untrusted text.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The name they chose for all of Discord, or null when they have none.</summary>
    public string? GlobalName { get; set; }

    /// <summary>Their nickname in this server, or null.</summary>
    public string? Nickname { get; set; }

    // ── The four names as search compares them (NameNormalizer.Searchable): plain letters, lower
    // case, no marks or decoration. Kept in step by SearchableNamesInterceptor on every save and
    // filled for older rows by the name catch-up. Null while the name is null. ──────────────────

    public string? UsernameSearchable { get; set; }

    public string? DisplayNameSearchable { get; set; }

    public string? GlobalNameSearchable { get; set; }

    public string? NicknameSearchable { get; set; }

    /// <summary>The picture the server shows for them: their server picture, else their own, else Discord's default.</summary>
    public string? AvatarUrl { get; set; }

    public bool IsBot { get; set; }

    /// <summary>Still to pass the server's membership screening.</summary>
    public bool IsPending { get; set; }

    /// <summary>When they started boosting the server, or null.</summary>
    public DateTimeOffset? BoostingSince { get; set; }

    /// <summary>When Discord says they joined, for their current membership.</summary>
    public DateTimeOffset? JoinedAt { get; set; }

    /// <summary>When Modbot first saw them in the server.</summary>
    public DateTimeOffset FirstSeenAt { get; set; }

    /// <summary>When they left, or null while they are a member.</summary>
    public DateTimeOffset? LeftAt { get; set; }

    /// <summary>Their role ids, as a <c>jsonb</c> array.</summary>
    public string Roles { get; set; } = "[]";

    public DateTimeOffset? TimedOutUntil { get; set; }

    /// <summary>The voice channel they are connected to, or null.</summary>
    public string? VoiceChannelId { get; set; }

    /// <summary>When they connected to <see cref="VoiceChannelId"/>, as Modbot knows it.</summary>
    public DateTimeOffset? VoiceSince { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
