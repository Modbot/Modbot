using System.Text.Json;

namespace Modbot.Core.Discord;

/// <summary>One role as a snapshot saved it. <see cref="Permissions"/> is the server-wide set.</summary>
public sealed record SnapshotRole(
    string Id,
    string Name,
    int Color,
    int Position,
    long Permissions,
    bool Hoisted,
    bool Mentionable,
    bool Managed,
    bool Everyone);

/// <summary>
/// What a channel's own settings say about one role: the permissions allowed and the ones denied.
/// Overwrites for single members are not saved (a member is a person, and a snapshot holds none).
/// </summary>
public sealed record SnapshotOverwrite(string RoleId, long Allow, long Deny);

/// <summary>One channel or category as a snapshot saved it.</summary>
public sealed record SnapshotChannel(
    string Id,
    string Name,
    string Type,
    string? CategoryId,
    int Position,
    bool Nsfw,
    IReadOnlyList<SnapshotOverwrite> Overwrites);

/// <summary>Everything one snapshot holds: the server's roles and channels with their permissions.</summary>
public sealed record ServerSnapshotContent(
    int Version,
    string GuildId,
    string GuildName,
    IReadOnlyList<SnapshotRole> Roles,
    IReadOnlyList<SnapshotChannel> Channels)
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads what <see cref="ToJson"/> wrote; null when the text is not a snapshot.</summary>
    public static ServerSnapshotContent? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ServerSnapshotContent>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The server as the bot sees it right now, with what the bot itself may do about it.
/// </summary>
/// <param name="BotCanManageRoles">Manage Roles, which changing a role's permissions or a channel's role overwrites needs.</param>
/// <param name="BotPermissions">The bot's server-wide permissions; it can only give a role what it holds itself.</param>
/// <param name="BotHighestRolePosition">The position of the bot's highest role; it can only change roles below it.</param>
public sealed record ServerStructure(
    ServerSnapshotContent Content,
    bool BotCanManageRoles,
    long BotPermissions,
    int BotHighestRolePosition);

/// <summary>What reading the server's structure from Discord came to.</summary>
public sealed record DiscordStructureOutcome(ServerStructure? Structure, string? Error, bool BotOffline = false)
{
    public static DiscordStructureOutcome Offline { get; } = new(null, "The Discord bot is not connected.", BotOffline: true);

    public static DiscordStructureOutcome Failed(string error) => new(null, error);
}

/// <summary>One change to a channel's permissions for a role: set it to this allow and deny.</summary>
public sealed record SnapshotOverwriteSet(string RoleId, long Allow, long Deny);

/// <summary>What a change to a role or a channel in Discord came to.</summary>
/// <param name="Applied">How many changes went through before it stopped; all of them when <see cref="Done"/>.</param>
/// <param name="MissingPermission">Discord refused because the bot lacks Manage Roles, or the role is above the bot's.</param>
public sealed record DiscordEditResult(bool Done, string? Error, int Applied = 0, bool BotOffline = false, bool MissingPermission = false)
{
    public static DiscordEditResult Offline { get; } = new(false, "The Discord bot is not connected.", BotOffline: true);

    public static DiscordEditResult Ok(int applied) => new(true, null, applied);

    public static DiscordEditResult Failed(string error, int applied = 0, bool missingPermission = false)
        => new(false, error, applied, MissingPermission: missingPermission);
}

/// <summary>
/// What the Modbot page does with the Discord server's roles and channels for snapshots: read them,
/// and put one role's or one channel's permissions back.
/// </summary>
/// <remarks>In Core so the API can ask without depending on the bot, as it does through <see cref="IDiscordInviteActions"/>.</remarks>
public interface IDiscordSnapshotActions
{
    /// <summary>Reads the roles and channels. Needs no permission: the bot already holds them.</summary>
    Task<DiscordStructureOutcome> ReadAsync(CancellationToken ct = default);

    /// <summary>Sets a role's server-wide permissions. Needs Manage Roles and a role below the bot's highest.</summary>
    Task<DiscordEditResult> SetRolePermissionsAsync(string roleId, long permissions, string reason, CancellationToken ct = default);

    /// <summary>
    /// Sets a channel's permissions for each role in <paramref name="set"/> and removes the channel's
    /// own settings for each role in <paramref name="removeRoleIds"/>. Needs Manage Roles.
    /// </summary>
    Task<DiscordEditResult> SetChannelOverwritesAsync(
        string channelId,
        IReadOnlyList<SnapshotOverwriteSet> set,
        IReadOnlyList<string> removeRoleIds,
        string reason,
        CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Every action answers that the bot is not connected.</summary>
public sealed class NoDiscordSnapshotActions : IDiscordSnapshotActions
{
    public Task<DiscordStructureOutcome> ReadAsync(CancellationToken ct = default)
        => Task.FromResult(DiscordStructureOutcome.Offline);

    public Task<DiscordEditResult> SetRolePermissionsAsync(string roleId, long permissions, string reason, CancellationToken ct = default)
        => Task.FromResult(DiscordEditResult.Offline);

    public Task<DiscordEditResult> SetChannelOverwritesAsync(
        string channelId,
        IReadOnlyList<SnapshotOverwriteSet> set,
        IReadOnlyList<string> removeRoleIds,
        string reason,
        CancellationToken ct = default)
        => Task.FromResult(DiscordEditResult.Offline);
}
