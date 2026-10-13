using Modbot.Core.Discord;

namespace Modbot.Api.Tests.Fakes;

/// <summary>
/// What the 18+ check asked Discord to do, recorded, with a role that can be made to fail. Nothing
/// reaches Discord.
/// </summary>
public sealed class FakeDiscordAgeActions : IDiscordAgeActions
{
    /// <summary>Every role change asked for: guild, member, role, and whether it was given.</summary>
    public List<(string GuildId, string UserId, string RoleId, bool Add)> RoleChanges { get; } = [];

    /// <summary>Roles whose change is refused, with what Discord said.</summary>
    public Dictionary<string, DiscordAgeOutcome> Refusals { get; } = new(StringComparer.Ordinal);

    public Task<DiscordAgeOutcome> ChangeRoleAsync(
        string guildId, string userId, string roleId, bool add, CancellationToken ct = default)
    {
        RoleChanges.Add((guildId, userId, roleId, add));

        return Task.FromResult(Refusals.TryGetValue(roleId, out var refusal) ? refusal : DiscordAgeOutcome.Ok);
    }

    public Task<DiscordAgeOutcome> DeleteTicketAsync(string channelId, string actorDiscordUserId, CancellationToken ct = default)
        => Task.FromResult(DiscordAgeOutcome.Ok);
}
