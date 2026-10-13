using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.AgeChecks;

/// <summary>
/// <see cref="IDiscordAgeActions"/> through the bot's live session: the roles an 18+ check gives and
/// takes, and the ticket it may delete.
/// </summary>
/// <remarks>
/// <strong>Only two kinds of channel are ever deleted</strong>: a Modbot ticket thread (a ticket
/// Modbot opened and has not yet deleted), or a channel directly inside the category the server
/// picked. Any other channel is left alone, and the answer says so. The gateway checks the category
/// rule again itself, so a wrong id here still cannot delete something else.
/// </remarks>
/// <param name="readyGateway">The bot's session while it is ready, or null.</param>
/// <param name="scopes">For the ticket rows and the chosen category.</param>
/// <param name="clock">Stamps a ticket as deleted.</param>
public sealed class DiscordAgeActions(Func<IDiscordGateway?> readyGateway, IServiceScopeFactory scopes, IModbotClock clock)
    : IDiscordAgeActions
{
    /// <summary>The reason Discord's audit log shows for what an 18+ check does.</summary>
    public const string Reason = "Modbot: 18+ check";

    /// <summary>What a channel that is neither a ticket nor in the category is told.</summary>
    public const string NotATicketMessage =
        "Modbot only deletes its own ticket threads, or a channel inside the chosen ticket category, so this channel was left alone.";

    public async Task<DiscordAgeOutcome> ChangeRoleAsync(
        string guildId, string userId, string roleId, bool add, CancellationToken ct = default)
    {
        if (readyGateway() is not { } gateway)
            return DiscordAgeOutcome.Offline;

        // The gateway notes a role it gives as Modbot's own, so the permission watch stays quiet.
        var outcome = await gateway.ChangeRoleAsync(guildId, userId, roleId, add, Reason, ct).ConfigureAwait(false);

        if (outcome.Done)
            return DiscordAgeOutcome.Ok;

        if (outcome.NotInServer)
            return DiscordAgeOutcome.Left;

        return outcome.RoleGone
            ? DiscordAgeOutcome.Failed("That role is no longer in the Discord server.")
            : DiscordAgeOutcome.Failed(outcome.Error ?? "Discord refused.");
    }

    public async Task<DiscordAgeOutcome> DeleteTicketAsync(string channelId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        if (readyGateway() is not { } gateway)
            return DiscordAgeOutcome.Offline;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        var ticket = await db.DiscordTickets
            .FirstOrDefaultAsync(t => t.ThreadId == channelId && t.State != DiscordTicketStates.Deleted, ct)
            .ConfigureAwait(false);

        if (ticket is not null)
        {
            var gone = await gateway.DeleteTicketThreadAsync(channelId, Reason, ct).ConfigureAwait(false);

            // A thread that is already gone is what was asked for; any other failure leaves it as it was.
            if (!gone.Sent && !gone.NotFound)
                return DiscordAgeOutcome.Failed(gone.Error ?? "Discord refused.");

            ticket.State = DiscordTicketStates.Deleted;
            ticket.DeletedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            return DiscordAgeOutcome.Ok;
        }

        var categoryId = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.AgeCheckTicketCategoryId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (categoryId is not { Length: > 0 })
            return new DiscordAgeOutcome(false, NotATicketMessage, NotAllowed: true);

        var deleted = await gateway.DeleteCategoryChannelAsync(channelId, categoryId, Reason, ct).ConfigureAwait(false);

        if (deleted.Sent || deleted.NotFound)
            return DiscordAgeOutcome.Ok;

        return deleted.Error == DiscordPostOutcome.NotInCategory
            ? new DiscordAgeOutcome(false, NotATicketMessage, NotAllowed: true)
            : DiscordAgeOutcome.Failed(deleted.Error ?? "Discord refused.");
    }
}
