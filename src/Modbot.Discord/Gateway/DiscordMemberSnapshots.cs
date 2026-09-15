namespace Modbot.Discord.Gateway;

/// <summary>One member of the server as the bot sees them now.</summary>
/// <param name="DisplayName">The name shown in the server: nickname, else global name, else username. Untrusted text.</param>
/// <param name="Nickname">The server nickname, or null for none.</param>
/// <param name="JoinedAt">When Discord says they joined the server, when it says.</param>
/// <param name="RoleIds">Their roles, @everyone left out.</param>
/// <param name="TimedOutUntil">When a timeout ends, or null when they are not timed out.</param>
/// <param name="GlobalName">The name they chose for all of Discord, or null.</param>
/// <param name="AvatarUrl">The picture the server shows for them.</param>
/// <param name="IsPending">Still to pass membership screening.</param>
/// <param name="BoostingSince">When they started boosting the server, or null.</param>
public sealed record DiscordMemberSnapshot(
    string UserId,
    string Username,
    string DisplayName,
    string? Nickname,
    bool IsBot,
    DateTimeOffset? JoinedAt,
    IReadOnlyList<string> RoleIds,
    DateTimeOffset? TimedOutUntil,
    string? GlobalName = null,
    string? AvatarUrl = null,
    bool IsPending = false,
    DateTimeOffset? BoostingSince = null);

/// <summary>Somebody connected to a voice channel right now.</summary>
public sealed record DiscordVoiceState(string UserId, string ChannelId);

/// <summary>A role given or taken away in one audit log entry.</summary>
public sealed record DiscordRoleChange(string RoleId, string Name, bool Added);

/// <summary>
/// What an audit log entry was about, in Modbot's words. Only the kinds Modbot records are read;
/// everything else in the audit log is left out.
/// </summary>
public static class DiscordAuditKinds
{
    public const string Ban = "ban";
    public const string Unban = "unban";
    public const string Kick = "kick";

    /// <summary>A timeout set or changed; <see cref="DiscordAuditEntry.Until"/> says to when.</summary>
    public const string Timeout = "timeout";

    public const string TimeoutRemoved = "timeout-removed";

    /// <summary>Roles given or taken away; <see cref="DiscordAuditEntry.Roles"/> says which.</summary>
    public const string Roles = "roles";

    /// <summary>A moderator deleted somebody else's messages in one channel. The target is the author.</summary>
    public const string MessagesDeleted = "messages-deleted";

    /// <summary>A moderator deleted many messages at once in one channel. No author is named.</summary>
    public const string MessagesBulkDeleted = "messages-bulk-deleted";

    public const string ChannelCreated = "channel-created";
    public const string ChannelChanged = "channel-changed";
    public const string ChannelDeleted = "channel-deleted";
    public const string RoleCreated = "role-created";
    public const string RoleChanged = "role-changed";
    public const string RoleDeleted = "role-deleted";
}

/// <summary>
/// One entry of the server's audit log: something somebody did, and who.
/// </summary>
/// <param name="Id">Discord's id for the entry. Entries come oldest first; the last is where the next read starts.</param>
/// <param name="Kind">One of <see cref="DiscordAuditKinds"/>.</param>
/// <param name="ActorId">Who did it.</param>
/// <param name="TargetId">Who or what it was done to: a member, a channel or a role.</param>
/// <param name="Reason">The reason the moderator gave, when they gave one. Untrusted text.</param>
/// <param name="ChannelId">For deleted messages, the channel they were in.</param>
/// <param name="Name">For a channel or role, its name.</param>
/// <param name="Count">For deleted messages, how many.</param>
/// <param name="Until">For a timeout, when it ends.</param>
/// <param name="Roles">For a role change, which roles.</param>
public sealed record DiscordAuditEntry(
    string Id,
    DateTimeOffset At,
    string Kind,
    string? ActorId,
    string? TargetId,
    string? Reason = null,
    string? ChannelId = null,
    string? Name = null,
    int? Count = null,
    DateTimeOffset? Until = null,
    IReadOnlyList<DiscordRoleChange>? Roles = null);

/// <summary>
/// A read of the audit log.
/// </summary>
/// <param name="Entries">Oldest first. Only the kinds in <see cref="DiscordAuditKinds"/>.</param>
/// <param name="NewestId">
/// The newest entry read, of any kind: where the next read starts. Null when nothing new was there.
/// </param>
/// <param name="NoAccess">The bot lacks View Audit Log.</param>
public sealed record DiscordAuditPage(
    IReadOnlyList<DiscordAuditEntry> Entries,
    string? NewestId,
    bool NoAccess = false,
    string? Error = null);
