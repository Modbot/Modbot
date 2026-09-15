using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Users;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;

namespace Modbot.Api.Features.DiscordLink;

/// <summary>What saving a link did.</summary>
/// <param name="Refusal">Why it was not saved, in words, or null when it was.</param>
public sealed record LinkSaveResult(DiscordAccountLink? Link, string? Refusal)
{
    public bool Saved => Refusal is null;
}

/// <summary>
/// Saves and ends links between Discord and VRChat accounts, with their facts, in one transaction
/// each (Discord account linking design §3.3, §7, §9).
/// </summary>
/// <remarks>
/// Roles are not given or taken here. Saving or ending a link changes what the member should hold,
/// and the role job in the bot compares that with what they do hold; this wakes it.
/// </remarks>
public sealed class DiscordAccountLinks
{
    public const string AlreadyLinkedElsewhere = "That VRChat account is already linked to another Discord account.";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly DiscordLinkSignal _signal;

    public DiscordAccountLinks(
        ModbotContext db,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        DiscordLinkSignal signal)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(signal);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
        _signal = signal;
    }

    /// <summary>The active link for a Discord account, or null.</summary>
    public Task<DiscordAccountLink?> ActiveForDiscordAsync(string discordUserId, CancellationToken ct)
        => _db.DiscordAccountLinks.FirstOrDefaultAsync(l => l.DiscordUserId == discordUserId && l.UnlinkedAt == null, ct);

    /// <summary>
    /// Records that both accounts were proved. Refuses a VRChat account another Discord account holds;
    /// replaces this Discord account's link to a different VRChat account.
    /// </summary>
    public async Task<LinkSaveResult> LinkAsync(
        DiscordIdentity discord,
        string vrchatUserId,
        string? vrchatDisplayName,
        string startedFrom,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discord);
        ArgumentException.ThrowIfNullOrWhiteSpace(vrchatUserId);

        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        var takenElsewhere = await _db.DiscordAccountLinks.AnyAsync(
            l => l.VRChatUserId == vrchatUserId && l.UnlinkedAt == null && l.DiscordUserId != discord.UserId, ct);

        if (takenElsewhere)
            return new LinkSaveResult(null, AlreadyLinkedElsewhere);

        var existing = await ActiveForDiscordAsync(discord.UserId, ct);

        if (existing is not null && existing.VRChatUserId == vrchatUserId)
        {
            // Proved again: no new link to record. The roles are handed out afresh, though -- a
            // member who left the server and came back lost them, and without the Server Members
            // intent Modbot never saw it. Giving a role somebody already holds changes nothing.
            existing.DiscordUsername = discord.Username;
            existing.VRChatDisplayName = vrchatDisplayName ?? existing.VRChatDisplayName;
            existing.LinkedRoleId = null;
            existing.EighteenPlusRoleId = null;
            existing.NotInServerAt = null;
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            _signal.Changed();
            return new LinkSaveResult(existing, null);
        }

        var link = new DiscordAccountLink
        {
            DiscordUserId = discord.UserId,
            DiscordUsername = discord.Username,
            VRChatUserId = vrchatUserId,
            VRChatDisplayName = vrchatDisplayName,
            StartedFrom = startedFrom,
            LinkedAt = now,
        };

        if (existing is not null)
        {
            // Same Discord member, same roles: they move to the new row rather than being taken
            // away and given back. The role job corrects the 18+ role if the new account differs.
            link.LinkedRoleId = existing.LinkedRoleId;
            link.EighteenPlusRoleId = existing.EighteenPlusRoleId;

            existing.UnlinkedAt = now;
            existing.UnlinkedBy = LinkEndedBy.Replaced;
            existing.LinkedRoleId = null;
            existing.EighteenPlusRoleId = null;

            // Saved before the insert: the unique index allows one active row per account.
            await _db.SaveChangesAsync(ct);
            await RecordRemovedAsync(existing, actorDiscordUserId: discord.UserId, moderator: null, now, ct);
        }

        _db.DiscordAccountLinks.Add(link);
        await _db.SaveChangesAsync(ct);

        await WriteAsync(new FactRecord
        {
            Type = FactType.DiscordLinkCreated,
            OccurredAt = now,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = vrchatUserId,
            ActorPlatform = FactPlatform.Discord,
            ActorId = discord.UserId,
            Source = FactSource.Manual,
            Data = new JsonObject
            {
                ["discordUserId"] = discord.UserId,
                ["discordUsername"] = discord.Username,
                ["actorDisplayName"] = discord.Username,
                ["vrchatDisplayName"] = vrchatDisplayName,
                ["startedFrom"] = startedFrom,
                ["replaced"] = existing?.VRChatUserId,
            },
        }, ct);

        await transaction.CommitAsync(ct);
        _signal.Changed();

        return new LinkSaveResult(link, null);
    }

    /// <summary>Ends a link. The row stays; the role job takes away the roles Modbot gave.</summary>
    /// <param name="moderator">The Modbot account ending it, or null when the member did.</param>
    public async Task UnlinkAsync(DiscordAccountLink link, Actor? moderator, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(link);

        if (!link.IsActive)
            return;

        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct);

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        link.UnlinkedAt = now;
        link.UnlinkedBy = moderator is null ? LinkEndedBy.Member : LinkEndedBy.Moderator;
        link.UnlinkedByUserId = moderator?.Id;
        await _db.SaveChangesAsync(ct);

        await RecordRemovedAsync(link, moderator is null ? link.DiscordUserId : null, moderator, now, ct);

        await transaction.CommitAsync(ct);
        _signal.Changed();
    }

    private Task RecordRemovedAsync(
        DiscordAccountLink link, string? actorDiscordUserId, Actor? moderator, DateTimeOffset now, CancellationToken ct)
    {
        var data = new JsonObject
        {
            ["discordUserId"] = link.DiscordUserId,
            ["discordUsername"] = link.DiscordUsername,
            ["by"] = link.UnlinkedBy,
        };

        if (moderator is { } m)
            data["actorDisplayName"] = m.Username;
        else
            data["actorDisplayName"] = link.DiscordUsername;

        return WriteAsync(new FactRecord
        {
            Type = FactType.DiscordLinkRemoved,
            OccurredAt = now,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = link.VRChatUserId,
            ActorPlatform = moderator is null ? FactPlatform.Discord : FactPlatform.Modbot,
            ActorId = moderator?.Id.ToString() ?? actorDiscordUserId,
            Source = FactSource.Manual,
            Data = data,
        }, ct);
    }

    private async Task WriteAsync(FactRecord fact, CancellationToken ct) => await _facts.WriteAsync(fact, ct);
}
