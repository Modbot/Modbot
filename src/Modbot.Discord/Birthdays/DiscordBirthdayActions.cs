using Modbot.Core.Discord;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Birthdays;

/// <summary>
/// <see cref="IDiscordBirthdayActions"/> through the bot's live session: the daily birthday post and
/// the birthday role.
/// </summary>
/// <param name="readyGateway">The bot's session while it is ready, or null.</param>
public sealed class DiscordBirthdayActions(Func<IDiscordGateway?> readyGateway) : IDiscordBirthdayActions
{
    public async Task<DiscordBirthdayOutcome> PostAsync(string channelId, string text, CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return DiscordBirthdayOutcome.Offline;

        // No role is named, so nobody is pinged: the mentions in the text show the members' names.
        var outcome = await gateway.SendPostAsync(channelId, text, roleId: null, pictures: null, ct).ConfigureAwait(false);

        if (outcome.Sent)
            return DiscordBirthdayOutcome.Ok;

        // A refusal that is not permanent is a rate limit: nothing was made. Anything unclear may
        // already be in the channel, so it is not sent again.
        return DiscordBirthdayOutcome.Failed(
            outcome.Error ?? "Discord refused.",
            tryAgain: !outcome.Permanent && !outcome.Unclear);
    }

    public async Task<DiscordBirthdayOutcome> ChangeRoleAsync(
        string guildId, string userId, string roleId, bool add, CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return DiscordBirthdayOutcome.Offline;

        // The gateway notes a role it gives as Modbot's own, so the permission watch stays quiet.
        var outcome = await gateway
            .ChangeRoleAsync(guildId, userId, roleId, add, add ? "Modbot: birthday" : "Modbot: birthday is over", ct)
            .ConfigureAwait(false);

        if (outcome.Done)
            return DiscordBirthdayOutcome.Ok;

        return outcome.NotInServer
            ? DiscordBirthdayOutcome.Left
            : DiscordBirthdayOutcome.Failed(outcome.Error ?? "Discord refused.");
    }
}
