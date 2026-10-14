using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Discord;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Invites;

/// <summary>
/// <see cref="IDiscordInviteActions"/> through the bot's live session: the Modbot page's read of the
/// server's invites and its making of one invite for each name on a list.
/// </summary>
/// <param name="readyGateway">The bot's session while it is ready, or null.</param>
/// <param name="scopes">Opens the database for the one call.</param>
public sealed class DiscordInviteActions(Func<IDiscordGateway?> readyGateway, IServiceScopeFactory scopes) : IDiscordInviteActions
{
    public Task<InviteSiteOutcome> ReadAsync(CancellationToken ct = default) => ReadAsync(resetCounts: false, ct);

    public Task<InviteSiteOutcome> StartCountingAsync(CancellationToken ct = default) => ReadAsync(resetCounts: true, ct);

    private async Task<InviteSiteOutcome> ReadAsync(bool resetCounts, CancellationToken ct)
    {
        if (readyGateway() is not { } gateway)
            return InviteSiteOutcome.Offline;

        using var scope = scopes.CreateScope();
        if (await GuildAsync(scope, ct).ConfigureAwait(false) is not { } guildId)
            return InviteSiteOutcome.Failed("No Discord server is set.");

        var read = await gateway.ReadInvitesAsync(guildId, ct).ConfigureAwait(false);
        if (!read.Ok)
            return InviteSiteOutcome.Failed(read.Error!, read.MissingPermission);

        await scope.ServiceProvider.GetRequiredService<InviteTracker>()
            .ReadAsync(read.Invites, resetCounts, ct)
            .ConfigureAwait(false);

        return InviteSiteOutcome.Ok;
    }

    public async Task<InviteCreateOutcome> CreateAsync(string channelId, IReadOnlyList<string> names, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(names);

        if (readyGateway() is not { } gateway)
            return new InviteCreateOutcome(InviteSiteOutcome.Offline, []);

        using var scope = scopes.CreateScope();
        if (await GuildAsync(scope, ct).ConfigureAwait(false) is not { } guildId)
            return new InviteCreateOutcome(InviteSiteOutcome.Failed("No Discord server is set."), []);

        var tracker = scope.ServiceProvider.GetRequiredService<InviteTracker>();
        var made = new List<InviteMade>();
        InviteSiteOutcome site = InviteSiteOutcome.Ok;

        foreach (var name in names)
        {
            var outcome = await gateway
                .CreateInviteAsync(guildId, channelId, $"Modbot: invite for {name}", ct)
                .ConfigureAwait(false);

            if (outcome.Invite is not { } invite)
            {
                made.Add(new InviteMade(name, null, outcome.Error));

                // Without the permission every other name would fail the same way.
                if (outcome.MissingPermission)
                {
                    site = InviteSiteOutcome.Failed(outcome.Error!, missingPermission: true);
                    break;
                }

                continue;
            }

            await tracker.NamedAsync(invite, name, ct).ConfigureAwait(false);
            made.Add(new InviteMade(name, invite.Code, null));
        }

        return new InviteCreateOutcome(site, made);
    }

    private static async Task<string?> GuildAsync(IServiceScope scope, CancellationToken ct)
        => await scope.ServiceProvider.GetRequiredService<ModbotContext>().Settings
            .AsNoTracking()
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
}
