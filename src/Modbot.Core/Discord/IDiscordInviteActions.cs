namespace Modbot.Core.Discord;

/// <summary>What reading the server's invites from Discord came to.</summary>
/// <param name="BotOffline">The bot has no session, so nothing was tried.</param>
/// <param name="MissingPermission">Discord refused because the bot lacks the permission, named in <see cref="Error"/>.</param>
public sealed record InviteSiteOutcome(bool Done, string? Error, bool BotOffline = false, bool MissingPermission = false)
{
    public static InviteSiteOutcome Offline { get; } = new(false, "The Discord bot is not connected.", BotOffline: true);

    public static InviteSiteOutcome Ok { get; } = new(true, null);

    public static InviteSiteOutcome Failed(string error, bool missingPermission = false) => new(false, error, MissingPermission: missingPermission);
}

/// <summary>One invite made from a name. <see cref="Code"/> is null when Discord refused it.</summary>
public sealed record InviteMade(string Name, string? Code, string? Error);

/// <summary>What making invites from a list of names came to.</summary>
public sealed record InviteCreateOutcome(InviteSiteOutcome Site, IReadOnlyList<InviteMade> Made);

/// <summary>
/// What the Modbot page does with the Discord server's invites: read them, and make one for each
/// name on a list.
/// </summary>
/// <remarks>
/// In Core so the API can ask without depending on the bot, as it does through
/// <see cref="IDiscordRoleMenuActions"/>. Each is made at once, so the person sees whether it worked.
/// </remarks>
public interface IDiscordInviteActions
{
    /// <summary>
    /// Reads the server's invites and vanity link, and stores the ones Modbot has not seen. The use
    /// counts of invites it already knows are left alone, since they are what the next join is
    /// compared with. Needs Manage Server.
    /// </summary>
    Task<InviteSiteOutcome> ReadAsync(CancellationToken ct = default);

    /// <summary>
    /// Reads the server's invites and takes Discord's own use counts as the ones Modbot has accounted
    /// for, as a new session does. Used when tracking is turned on, since counts kept from before
    /// are out of date and whoever joined meanwhile cannot be placed. Needs Manage Server.
    /// </summary>
    Task<InviteSiteOutcome> StartCountingAsync(CancellationToken ct = default);

    /// <summary>
    /// Makes one invite to <paramref name="channelId"/> for each name, with no limit on uses or time,
    /// and stores each with its name. Needs Create Invite in the channel.
    /// </summary>
    Task<InviteCreateOutcome> CreateAsync(string channelId, IReadOnlyList<string> names, CancellationToken ct = default);
}

/// <summary>A process with no Discord bot. Every action answers that the bot is not connected.</summary>
public sealed class NoDiscordInviteActions : IDiscordInviteActions
{
    public Task<InviteSiteOutcome> ReadAsync(CancellationToken ct = default)
        => Task.FromResult(InviteSiteOutcome.Offline);

    public Task<InviteSiteOutcome> StartCountingAsync(CancellationToken ct = default)
        => Task.FromResult(InviteSiteOutcome.Offline);

    public Task<InviteCreateOutcome> CreateAsync(string channelId, IReadOnlyList<string> names, CancellationToken ct = default)
        => Task.FromResult(new InviteCreateOutcome(InviteSiteOutcome.Offline, []));
}
