using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Giveaways;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Giveaways;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Giveaways;

/// <summary>
/// Turns reactions on a giveaway's post into entries, and taking one off into a withdrawal
/// (giveaways design §4.2).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The rules are checked when the reaction arrives and again at the draw.</strong> Somebody
/// can qualify on Monday and not on Friday, and neither reading on its own is the honest one:
/// checking only on entry lets somebody who has since been banned win, and checking only at the
/// draw means a person who did not qualify gets no word of it until it is too late to do anything.
/// So the answer on entry is recorded here and the draw asks again.
/// </para>
/// <para>
/// A person who does not qualify still enters. Their entry is kept with the reason beside it, and
/// the snapshot shows them as such — dropping them silently would leave somebody watching a
/// giveaway they were never in (giveaways design §4.3).
/// </para>
/// <para>
/// Anybody may react. Modbot does not have to know who they are: a reaction from somebody with no
/// member row is an entry, and a rule that needs VRChat data simply fails for them, visibly.
/// </para>
/// </remarks>
public sealed class GiveawayReactions
{
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly GiveawayRuleChecker _checker;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly ILogger _log;

    public GiveawayReactions(
        ModbotContext db,
        IModbotClock clock,
        GiveawayRuleChecker checker,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _checker = checker;
        _facts = facts;
        _partitions = partitions;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <summary>Somebody reacted. Returns whether it counted as an entry.</summary>
    public async Task<bool> AddedAsync(DiscordReactionSnapshot reaction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reaction);

        if (await GiveawayForAsync(reaction, ct) is not { } giveaway)
            return false;

        var now = _clock.UtcNow;

        var entry = await _db.GiveawayEntries
            .FirstOrDefaultAsync(e => e.GiveawayId == giveaway.Id && e.DiscordUserId == reaction.UserId, ct);

        var rules = GiveawayRules.ReadStored(giveaway.Rules);
        var answer = await _checker.CheckOneAsync(rules, reaction.UserId, ct);

        if (entry is null)
        {
            entry = new GiveawayEntry
            {
                GiveawayId = giveaway.Id,
                DiscordUserId = reaction.UserId,
                EnteredAt = now,
            };

            _db.GiveawayEntries.Add(entry);
        }

        // Reacting again after taking it off is the same person coming back, not a second entry.
        entry.WithdrawnAt = null;
        entry.QualifiedOnEntry = answer.Met;
        entry.KeptOut = answer.Met ? null : GiveawayKeptOut.Rules;

        await _db.SaveChangesAsync(ct);

        await RecordAsync(
            FactType.GiveawayEntered,
            giveaway,
            reaction.UserId,
            now,
            new JsonObject
            {
                ["qualified"] = answer.Met,
                ["because"] = answer.Because,
            },
            ct);

        return true;
    }

    /// <summary>Somebody took their reaction off. Returns whether an entry was withdrawn.</summary>
    public async Task<bool> RemovedAsync(DiscordReactionSnapshot reaction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reaction);

        if (await GiveawayForAsync(reaction, ct) is not { } giveaway)
            return false;

        var entry = await _db.GiveawayEntries
            .FirstOrDefaultAsync(e => e.GiveawayId == giveaway.Id && e.DiscordUserId == reaction.UserId, ct);

        if (entry is null || entry.WithdrawnAt is not null)
            return false;

        var now = _clock.UtcNow;
        entry.WithdrawnAt = now;

        await _db.SaveChangesAsync(ct);
        await RecordAsync(FactType.GiveawayWithdrawn, giveaway, reaction.UserId, now, new JsonObject(), ct);

        return true;
    }

    /// <summary>
    /// The giveaway this reaction is on, when it is one that counts.
    /// </summary>
    /// <remarks>
    /// A reaction counts only on the post Modbot made, with the emoji the giveaway names, while it
    /// is open. Every other reaction in the server — a different emoji on the same post, a reaction
    /// after closing, the bot's own reaction that gives people something to click — is somebody
    /// using Discord, and Modbot has no business writing a fact about it.
    /// </remarks>
    private async Task<Giveaway?> GiveawayForAsync(DiscordReactionSnapshot reaction, CancellationToken ct)
    {
        if (reaction.IsBot)
            return null;

        var post = await _db.GiveawayPosts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.MessageId == reaction.MessageId, ct);

        if (post is null)
            return null;

        var giveaway = await _db.Giveaways
            .FirstOrDefaultAsync(g => g.Id == post.GiveawayId && g.DeletedAt == null, ct);

        if (giveaway is null
            || giveaway.State != GiveawayStates.Open
            || giveaway.EntryWay != GiveawayEntryWays.React)
        {
            return null;
        }

        if (!string.Equals(giveaway.Emoji.Trim(), reaction.Emoji.Trim(), StringComparison.Ordinal))
            return null;

        return giveaway;
    }

    /// <summary>
    /// Records an entry or a withdrawal about the person, not about the giveaway.
    /// </summary>
    /// <remarks>
    /// The subject is the Discord account on purpose. An entry is a thing somebody did, it belongs
    /// in their history, and a purge has to be able to find it — which it can only do by subject
    /// (giveaways design §6.3).
    /// </remarks>
    private async Task RecordAsync(
        string type, Giveaway giveaway, string discordUserId, DateTimeOffset now, JsonObject data, CancellationToken ct)
    {
        await _partitions.EnsureForAsync(now, ct);

        data["giveawayId"] = giveaway.Id.ToString();
        data["name"] = giveaway.Name;

        await _facts.WriteAsync(
            new FactRecord
            {
                Type = type,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = discordUserId,
                Source = FactSource.Discord,
                Data = data,
            },
            ct);

        _log.Debug("Giveaway {Giveaway}: {Type} by {UserId}", giveaway.Id, type, discordUserId);
    }
}
