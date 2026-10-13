using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Users;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/ageverified</c>: the 18+ check, typed in a ticket. It does what the profile's button does
/// (<see cref="IAgeCheck"/>), then deletes the ticket if the server switched that on.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The reply goes first.</strong> A reply into a channel that is then deleted would fail,
/// so the answer is sent, then the ticket is deleted. The handler is told the command already
/// answered (<see cref="StaffCommandAnswer.Replied"/>) and sends nothing more. A ticket that
/// cannot be deleted gets a second line saying so.
/// </para>
/// <para>
/// <strong>Only a ticket is deleted.</strong> A Modbot ticket thread, or a channel inside the
/// category the server picked (<see cref="IDiscordAgeActions.DeleteTicketAsync"/>); the command run
/// anywhere else leaves the channel alone and says so. The caller is already known: the handler has
/// found the Modbot account and checked Edit 18+ verified.
/// </para>
/// </remarks>
public sealed class AgeVerifiedCommand(ModbotContext db, IDiscordAgeActions actions, IAgeCheck? check = null)
{
    public const string PickOneMessage = "Pick a Discord member.";
    public const string PickMethodMessage = "Pick badge or photo ID.";

    public async Task<StaffCommandAnswer> RunAsync(DiscordCommandCall call, ModbotUser user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(user);

        if (check is null)
            return new StaffCommandAnswer(DiscordReply.Say(Interactions.StaffInteractionHandler.NotSetUpMessage), "refused");

        var member = call.Option(DiscordCommands.MemberOption)?.Trim() ?? string.Empty;

        if (member.Length == 0)
            return new StaffCommandAnswer(DiscordReply.Say(PickOneMessage), "invalid");

        var method = call.Option(DiscordCommands.AgeMethodOption)?.Trim();

        if (!AgeCheckMethods.IsKnown(method))
            return new StaffCommandAnswer(DiscordReply.Say(PickMethodMessage), "invalid", DiscordTarget: member);

        var answer = await check.RunAsync(new AgeCheckRequest(user.Id, method!, member, call.Option(DiscordCommands.VRChatOption)), ct)
            .ConfigureAwait(false);

        var reply = DiscordReply.Say(CardText.EscapeText(answer.Text));

        if (!answer.Done)
            return new StaffCommandAnswer(reply, "refused", DiscordTarget: member);

        var deleteTicket = await db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.AgeCheckDeleteTicket)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (!deleteTicket || call.ChannelId is not { Length: > 0 } channelId)
            return new StaffCommandAnswer(reply, "answered", DiscordTarget: member);

        // The reply first: it would fail once the channel is gone.
        await call.ReplyAsync(DiscordReply.Say(CardText.EscapeText(answer.Text + "\nDeleting this ticket.")), ct).ConfigureAwait(false);

        var deleted = await actions.DeleteTicketAsync(channelId, call.DiscordUserId, ct).ConfigureAwait(false);

        if (!deleted.Done)
        {
            await call.ReplyAsync(
                    DiscordReply.Say(CardText.EscapeText($"The ticket was not deleted: {deleted.Error}")),
                    ct)
                .ConfigureAwait(false);
        }

        return new StaffCommandAnswer(reply, "answered", DiscordTarget: member, Replied: true);
    }
}
