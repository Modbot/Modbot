using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Discord.Gateway;

namespace Modbot.Discord.Sync;

/// <summary>
/// Bans and unbans a person's linked Discord account when somebody bans or unbans them through
/// Modbot (<see cref="ILinkedDiscordBans"/>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>It does not depend on the ban sync switches.</strong> Those decide whether a ban made
/// <em>somewhere else</em> is copied. This is about a ban made in Modbot itself, where the person
/// pressing the button is asking Modbot to act on the person, not on one of their accounts. A
/// person with no linked Discord account, or a group with no Discord server, has nothing to act on
/// and is left alone without a word.
/// </para>
/// <para>
/// <strong>It writes the copy record first</strong>, before the call goes out, exactly as ban sync
/// does, so what comes back round is recognised and dropped: Discord reports the ban as a ban by
/// Modbot's own bot, and the group's audit log shows the VRChat half as a ban by Modbot's own
/// account. See <see cref="CopyRecords"/> and <see cref="BanSync"/>.
/// </para>
/// <para>
/// <strong>A failure here is shown and recorded, and undoes nothing.</strong> The VRChat action has
/// already happened by the time this is asked; a bot without Ban Members is a setup problem for the
/// operator, not a reason to unsay a ban. The failure is written as <see cref="FactType.CopyFailed"/>
/// and handed back so the moderator sees it.
/// </para>
/// </remarks>
public sealed class LinkedDiscordBans : ILinkedDiscordBans
{
    /// <summary>Discord keeps at most this many characters of a reason in its audit log.</summary>
    public const int MaxReasonLength = 512;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly CopyRecords _copies;
    private readonly Func<IDiscordGateway?> _gateway;

    /// <param name="gateway">
    /// The bot's session when it is ready, or null. A function, so each ban asks for the session as
    /// it is at that moment rather than as it was when this was made.
    /// </param>
    public LinkedDiscordBans(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        CopyRecords copies,
        Func<IDiscordGateway?> gateway)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(gateway);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _copies = copies;
        _gateway = gateway;
    }

    public Task<LinkedDiscordOutcome> BanAsync(
        string vrchatUserId, string by, string? why, long? causedByFactId, CancellationToken ct = default)
        => ChangeAsync(banning: true, vrchatUserId, by, why, causedByFactId, ct);

    public Task<LinkedDiscordOutcome> UnbanAsync(
        string vrchatUserId, string by, string? why, long? causedByFactId, CancellationToken ct = default)
        => ChangeAsync(banning: false, vrchatUserId, by, why, causedByFactId, ct);

    private async Task<LinkedDiscordOutcome> ChangeAsync(
        bool banning, string vrchatUserId, string by, string? why, long? causedByFactId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vrchatUserId);

        var guildId = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // No Discord server set up: nothing to act in, and nothing to say about it.
        if (string.IsNullOrWhiteSpace(guildId))
            return LinkedDiscordOutcome.Skipped;

        var link = await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => l.UnlinkedAt == null && l.VRChatUserId == vrchatUserId)
            .Select(l => new { l.DiscordUserId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // Most people banned from a group have no Discord account tied to them.
        if (link is null)
            return LinkedDiscordOutcome.Skipped;

        var reason = Reason(banning, by, why);
        var kind = banning ? CopyKinds.ModbotBan : CopyKinds.ModbotUnban;

        // Written before the call, never after: this row is what stops the ban Modbot is about to
        // send from coming back round as a reason to ban again.
        var record = await _copies.StartAsync(
                CopyDirections.ToDiscord, kind, link.DiscordUserId, vrchatUserId, null, causedByFactId, ct)
            .ConfigureAwait(false);

        var done = false;
        string? error;
        var nothingHappened = false;

        var gateway = _gateway();

        if (gateway is null)
        {
            error = "The Discord bot is not connected.";
        }
        else
        {
            var outcome = banning
                ? await gateway.BanAsync(guildId, link.DiscordUserId, reason, BanSync.KeepsMessages, ct).ConfigureAwait(false)
                : await gateway.UnbanAsync(guildId, link.DiscordUserId, reason, ct).ConfigureAwait(false);

            done = outcome.Done;
            error = outcome.Error;
            nothingHappened = outcome.NothingToDo;
        }

        await _copies.FinishAsync(record, done, error, nothingHappened, ct).ConfigureAwait(false);

        var data = new JsonObject
        {
            ["direction"] = CopyDirections.ToDiscord,
            ["kind"] = banning ? CopyKinds.Ban : CopyKinds.Unban,
            ["vrchatUserId"] = vrchatUserId,
            ["discordUserId"] = link.DiscordUserId,
            ["reason"] = reason,
            ["causedBy"] = causedByFactId,
            ["by"] = by,
            ["description"] = banning
                ? $"Banned in Modbot by {by}, so banned in Discord too."
                : $"Unbanned in Modbot by {by}, so unbanned in Discord too.",
        };

        if (!done)
        {
            data["error"] = error;
            await WriteAsync(FactType.CopyFailed, link.DiscordUserId, data, ct).ConfigureAwait(false);
            return LinkedDiscordOutcome.Failed(error ?? "Discord did not say why.");
        }

        if (nothingHappened)
            return new LinkedDiscordOutcome(LinkedDiscordStatus.Unchanged);

        await WriteAsync(banning ? FactType.CopiedBan : FactType.CopiedUnban, link.DiscordUserId, data, ct)
            .ConfigureAwait(false);

        return new LinkedDiscordOutcome(LinkedDiscordStatus.Done);
    }

    /// <summary>What Discord's audit log says: who, and the reasons in words, kept to Discord's limit.</summary>
    internal static string Reason(bool banning, string by, string? why)
    {
        var text = $"Modbot: {(banning ? "banned" : "unbanned")} by {by}"
                   + (string.IsNullOrWhiteSpace(why) ? string.Empty : $": {why.Trim()}");

        return text.Length <= MaxReasonLength ? text : text[..(MaxReasonLength - 1)] + "…";
    }

    private async Task WriteAsync(string type, string discordUserId, JsonObject data, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        await _facts.WriteAsync(new FactRecord
        {
            Type = type,
            OccurredAt = now,
            SubjectPlatform = FactPlatform.Discord,
            SubjectId = discordUserId,
            Source = FactSource.Modbot,
            Data = data,
        }, ct).ConfigureAwait(false);
    }
}
