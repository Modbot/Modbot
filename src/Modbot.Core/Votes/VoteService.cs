using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Core.Votes;

/// <summary>What somebody wants to ask: the question, where, the options, and who may answer.</summary>
/// <param name="RoleId">The Discord role a person must hold to vote, or null for anybody in the server.</param>
public sealed record VoteDraft(string Question, string ChannelId, IReadOnlyList<string> Options, string? RoleId);

/// <summary>What making a vote came to: the vote, or what was wrong in words for the person.</summary>
/// <param name="BotOffline">The vote was fine but the bot has no session, so nothing was posted.</param>
public sealed record VoteCreated(Vote? Vote, string? Problem, bool BotOffline = false)
{
    public bool Done => Vote is not null;
}

/// <summary>What closing a vote came to.</summary>
/// <param name="Vote">The vote, or null when there is none with that id.</param>
/// <param name="AlreadyClosed">It was closed before this asked.</param>
/// <param name="MessageProblem">
/// Why Discord's message still shows the buttons, or null when it was rewritten. The vote is closed
/// in Modbot either way: closing never waits on Discord.
/// </param>
public sealed record VoteClosed(Vote? Vote, bool AlreadyClosed, string? MessageProblem);

/// <summary>
/// Makes and closes votes: the one place the page and <c>/vote</c> both go through, so a vote means
/// the same wherever it was made.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Posted before it is saved.</strong> A vote's ids are made up front, so its message can
/// carry them. A vote whose message could not be posted is never saved, and a message whose vote
/// could not be saved is deleted again, so there is never a button that leads nowhere.
/// </para>
/// <para>
/// <strong>Closing never waits on Discord.</strong> The vote is closed in Modbot first, so a press
/// is refused from then on whatever the message shows; the message is then rewritten with the
/// result, and when that fails the caller is told why.
/// </para>
/// </remarks>
public sealed class VoteService(ModbotContext db, IModbotClock clock, IDiscordVoteActions discord)
{
    /// <summary>Splits the options a person typed, one to a line, dropping blank lines.</summary>
    public static IReadOnlyList<string> Lines(string? typed)
        => [.. (typed ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)];

    /// <summary>What is wrong with a draft, in words for the person, or null.</summary>
    public async Task<string?> ProblemAsync(VoteDraft draft, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Question.Length == 0)
            return "Write the question.";

        if (draft.Question.Length > VoteLimits.MostQuestionLength || draft.Question.Contains('\n', StringComparison.Ordinal))
            return $"The question is one line of at most {VoteLimits.MostQuestionLength} characters.";

        if (draft.ChannelId.Length == 0)
            return "Pick the channel.";

        if (draft.Options.Count < 2)
            return "Add at least two options.";

        if (draft.Options.Count > VoteLimits.MostOptions)
            return $"At most {VoteLimits.MostOptions} options.";

        if (draft.Options.Any(o => o.Length > VoteLimits.MostLabelLength))
            return $"An option is at most {VoteLimits.MostLabelLength} characters.";

        if (draft.Options.Select(o => o.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() != draft.Options.Count)
            return "Two options say the same thing.";

        if (draft.RoleId is { } roleId)
        {
            var guildId = await db.Settings.AsNoTracking()
                .Where(s => s.Id == 1)
                .Select(s => s.DiscordGuildId)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            var there = guildId is not null
                && await db.DiscordRoles.AsNoTracking()
                    .AnyAsync(r => r.GuildId == guildId && r.RoleId == roleId && r.RemovedAt == null, ct)
                    .ConfigureAwait(false);

            if (!there)
                return "That role is not in the Discord server.";
        }

        return null;
    }

    /// <summary>Checks the draft, posts the message and saves the vote.</summary>
    /// <param name="createdBy">The Modbot account that asked.</param>
    public async Task<VoteCreated> CreateAsync(VoteDraft draft, Guid createdBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var clean = draft with
        {
            Question = (draft.Question ?? string.Empty).Trim(),
            ChannelId = (draft.ChannelId ?? string.Empty).Trim(),
            Options = [.. (draft.Options ?? []).Select(o => (o ?? string.Empty).Trim()).Where(o => o.Length > 0)],
            RoleId = string.IsNullOrWhiteSpace(draft.RoleId) ? null : draft.RoleId.Trim(),
        };

        if (await ProblemAsync(clean, ct).ConfigureAwait(false) is { } problem)
            return new VoteCreated(null, problem);

        var vote = new Vote
        {
            Question = clean.Question,
            ChannelId = clean.ChannelId,
            RoleId = clean.RoleId,
            CreatedBy = createdBy,
            CreatedAt = clock.UtcNow,
        };

        vote.Options = [.. clean.Options.Select((label, i) => new VoteOption { VoteId = vote.Id, Label = label, Position = i })];

        var posted = await discord.PostAsync(vote, ct).ConfigureAwait(false);

        if (!posted.Done || posted.MessageId is null)
            return new VoteCreated(null, posted.Error ?? "Discord refused.", posted.BotOffline);

        vote.MessageId = posted.MessageId;

        try
        {
            db.Votes.Add(vote);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await discord.DeleteAsync(vote.ChannelId, vote.MessageId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new VoteCreated(vote, null);
    }

    /// <summary>The picks for each option of these votes, by option id; an option nobody picked is not in it.</summary>
    public async Task<Dictionary<Guid, int>> CountsAsync(IReadOnlyCollection<Guid> voteIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(voteIds);

        var counts = await db.VotePicks.AsNoTracking()
            .Where(p => voteIds.Contains(p.VoteId))
            .GroupBy(p => p.OptionId)
            .Select(g => new { OptionId = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return counts.ToDictionary(c => c.OptionId, c => c.Count);
    }

    /// <summary>Closes the vote and rewrites its message with the result.</summary>
    public async Task<VoteClosed> CloseAsync(Guid id, CancellationToken ct)
    {
        var vote = await db.Votes.Include(v => v.Options).FirstOrDefaultAsync(v => v.Id == id, ct).ConfigureAwait(false);

        if (vote is null)
            return new VoteClosed(null, false, null);

        if (vote.Status == VoteStatuses.Closed)
            return new VoteClosed(vote, true, null);

        vote.Status = VoteStatuses.Closed;
        vote.ClosedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var counts = await CountsAsync([vote.Id], ct).ConfigureAwait(false);
        var outcome = await discord.ShowResultAsync(vote, counts, ct).ConfigureAwait(false);

        return new VoteClosed(vote, false, outcome.Done ? null : outcome.Error ?? "Discord refused.");
    }
}
