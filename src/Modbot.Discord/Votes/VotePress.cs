using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Votes;

/// <summary>
/// What happens when somebody presses a vote's button or picks from its drop-down list: their vote
/// is saved, or changed if they had voted already. The answer is private to them.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The message is only a handle.</strong> The press names a vote and an option by id; the
/// vote, whether it is open, who may vote and what its options are come from Modbot's own tables
/// every time.
/// </para>
/// <para>
/// <strong>One vote for each person.</strong> The table holds one row for each person on a vote
/// (a unique key), so a second press changes the row and two quick presses cannot count twice.
/// </para>
/// <para>
/// <strong>Counts on the message.</strong> On a vote that shows counts, a new or changed pick asks
/// the <see cref="VoteRefresher"/> to rewrite the message; it batches the asks.
/// </para>
/// </remarks>
public sealed class VotePress(ModbotContext db, IModbotClock clock, VoteRefresher? refresher = null)
{
    public const string GoneMessage = "That vote is gone.";
    public const string ClosedMessage = "This vote is closed.";
    public const string NoOptionMessage = "That option is not on the vote any more.";
    public const string NoRolesMessage = "Modbot could not read your roles. Try again.";

    public async Task<DiscordReply> HandleAsync(DiscordButtonPress press, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        if (!VoteMessage.TryRead(press.ButtonId, out var voteId, out var buttonOption))
            return DiscordReply.Say("That vote does not work any more.");

        var vote = await db.Votes.AsNoTracking()
            .Include(v => v.Options)
            .FirstOrDefaultAsync(v => v.Id == voteId, ct)
            .ConfigureAwait(false);

        if (vote is null)
            return DiscordReply.Say(GoneMessage);

        if (vote.Status != VoteStatuses.Open)
            return DiscordReply.Say(ClosedMessage);

        if (vote.RoleId is { } roleId)
        {
            if (press.MemberRoleIds is not { } held)
                return DiscordReply.Say(NoRolesMessage);

            if (!held.Contains(roleId, StringComparer.Ordinal))
                return DiscordReply.Say(await OnlyForAsync(roleId, ct).ConfigureAwait(false));
        }

        var optionId = buttonOption
            ?? (press.Values.Count > 0 && Guid.TryParseExact(press.Values[0], "N", out var picked) ? picked : (Guid?)null);

        var option = optionId is { } id ? vote.Options.FirstOrDefault(o => o.Id == id) : null;

        if (option is null)
            return DiscordReply.Say(NoOptionMessage);

        // Two presses at once from one person can both find no row; the unique key lets one win,
        // and the other starts again and finds it.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var (said, changed) = await SaveAsync(vote, option, press.DiscordUserId, ct).ConfigureAwait(false);

                if (changed && vote.ShowCounts)
                    refresher?.Request(vote.Id);

                return DiscordReply.Say(said);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<(string Said, bool Changed)> SaveAsync(Vote vote, VoteOption option, string discordUserId, CancellationToken ct)
    {
        // The vote row is held shared until the pick is saved: closing writes that row, so it waits
        // for presses already past this check, and a press that starts after the close has
        // committed sees "closed". The count in the closed message is then every pick there is.
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var status = (await db.Database
            .SqlQuery<string>($"SELECT status AS \"Value\" FROM vote WHERE id = {vote.Id} FOR SHARE")
            .ToListAsync(ct)
            .ConfigureAwait(false))
            .FirstOrDefault();

        if (status is null)
            return (GoneMessage, false);

        if (status != VoteStatuses.Open)
            return (ClosedMessage, false);

        var result = await SaveLockedAsync(vote, option, discordUserId, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    private async Task<(string Said, bool Changed)> SaveLockedAsync(Vote vote, VoteOption option, string discordUserId, CancellationToken ct)
    {
        var pick = await db.VotePicks
            .FirstOrDefaultAsync(p => p.VoteId == vote.Id && p.DiscordUserId == discordUserId, ct)
            .ConfigureAwait(false);

        if (pick is null)
        {
            db.VotePicks.Add(new VotePick
            {
                VoteId = vote.Id,
                OptionId = option.Id,
                DiscordUserId = discordUserId,
                VotedAt = clock.UtcNow,
            });

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return ($"You voted for “{option.Label}”.", true);
        }

        if (pick.OptionId == option.Id)
            return ($"You already voted for “{option.Label}”.", false);

        var was = vote.Options.FirstOrDefault(o => o.Id == pick.OptionId)?.Label;

        pick.OptionId = option.Id;
        pick.VotedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return (was is null
            ? $"You voted for “{option.Label}”."
            : $"Changed your vote from “{was}” to “{option.Label}”.", true);
    }

    private async Task<string> OnlyForAsync(string roleId, CancellationToken ct)
    {
        var name = await db.DiscordRoles.AsNoTracking()
            .Where(r => r.RoleId == roleId && r.RemovedAt == null)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return string.IsNullOrEmpty(name)
            ? "Only people with a certain role can vote on this."
            : $"Only people with the “{name}” role can vote on this.";
    }
}
