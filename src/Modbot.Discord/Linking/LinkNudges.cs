using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Npgsql;

namespace Modbot.Discord.Linking;

/// <summary>What one turn of the link reminder came to.</summary>
public enum LinkNudgeStep
{
    /// <summary>The reminder is not running.</summary>
    Off,

    /// <summary>One member was sent the reminder.</summary>
    Sent,

    /// <summary>One member could not be reached, and will not be asked again.</summary>
    NotReached,

    /// <summary>Nothing was sent this turn and the next turn tries again.</summary>
    Retry,

    /// <summary>Everyone was dealt with and the reminder stopped itself.</summary>
    Finished,
}

/// <summary>
/// Sends the one-time reminder to link a VRChat account, to Discord members who have not (account
/// links). Staff start it; it sends one direct message per turn, and the service that calls it takes
/// a turn every <see cref="LinkNudgeQueue.Gap"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Once per person, ever.</strong> A member is claimed with a row in <c>discord_link_nudge</c>
/// before the message goes, and the row stays whatever Discord answered, so starting the job again
/// asks nobody twice. Only a failure that says nothing about the member (Discord being down) gives the
/// claim back.
/// </para>
/// <para>
/// Only members who were already in the server when staff started it are reminded: a newcomer is
/// asked by the join prompt or the welcome, and the job ends when the members it began with are done.
/// </para>
/// </remarks>
public sealed class LinkNudges
{
    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;

    public LinkNudges(ModbotContext db, IFactWriter facts, EventPartitionMaintainer partitions, IModbotClock clock)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
    }

    /// <summary>The reminder's text. The server's name is a name, not a sentence, so it is escaped as every other name on a card is.</summary>
    public static string MessageText(string? serverName)
        => string.IsNullOrWhiteSpace(serverName)
            ? "Link your VRChat account with the button below."
            : $"Link your VRChat account to **{CardText.Fit(CardText.EscapeName(serverName), CardText.MaxNameLength)}** with the button below.";

    /// <summary>One turn: reminds the next member, if the reminder is running.</summary>
    public async Task<LinkNudgeStep> SendNextAsync(IDiscordGateway gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var settings = await _db.Settings.FirstOrDefaultAsync(s => s.Id == 1, ct).ConfigureAwait(false);

        if (settings?.DiscordLinkNudgeStartedAt is not { } startedAt)
            return LinkNudgeStep.Off;

        var page = DiscordInvite.LinkPageWhenSetUp(settings);

        // Linking was taken apart since it was started: stop rather than send people to a page that cannot work.
        if (page is null || string.IsNullOrWhiteSpace(settings.DiscordGuildId))
        {
            settings.DiscordLinkNudgeStartedAt = null;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return LinkNudgeStep.Finished;
        }

        var next = await LinkNudgeQueue.Waiting(_db, settings.DiscordGuildId, startedAt)
            .OrderBy(m => m.UserId)
            .Select(m => new { m.UserId, m.Username })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (next is null)
        {
            settings.DiscordLinkNudgeStartedAt = null;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return LinkNudgeStep.Finished;
        }

        var row = new DiscordLinkNudge
        {
            DiscordUserId = next.UserId,
            State = DiscordLinkNudgeStates.Sending,
            CreatedAt = _clock.UtcNow,
        };

        // Claimed first: never twice, however the job is started or stopped.
        try
        {
            _db.DiscordLinkNudges.Add(row);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            _db.ChangeTracker.Clear();
            return LinkNudgeStep.Retry;
        }

        var serverName = await _db.DiscordServers.AsNoTracking()
            .Where(s => s.GuildId == settings.DiscordGuildId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var outcome = await gateway
            .SendDirectMessageAsync(next.UserId, MessageText(serverName), [new DiscordLinkButton(LinkPrompt.ButtonLabel, page)], ct)
            .ConfigureAwait(false);

        // A refusal that is about Discord, not about the member, gives the claim back for the next turn.
        if (!outcome.Sent && !outcome.DirectMessagesClosed && !outcome.Permanent && !outcome.NotFound && !outcome.Unclear)
        {
            _db.DiscordLinkNudges.Remove(row);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return LinkNudgeStep.Retry;
        }

        var reached = outcome.Sent || outcome.Unclear;
        row.State = reached ? DiscordLinkNudgeStates.Sent : DiscordLinkNudgeStates.NotReached;
        row.SentAt = reached ? _clock.UtcNow : null;
        row.Error = reached ? null : Short(outcome.Error ?? "Discord refused the message.");
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        await RecordAsync(next.UserId, next.Username, reached, row.Error, ct).ConfigureAwait(false);

        return reached ? LinkNudgeStep.Sent : LinkNudgeStep.NotReached;
    }

    private static string Short(string text) => text.Length <= 300 ? text : text[..300];

    private async Task RecordAsync(string userId, string username, bool reached, string? error, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        var data = new JsonObject
        {
            ["via"] = reached ? LinkPromptVia.DirectMessage : LinkPromptVia.None,
            ["discordUsername"] = username,
            ["reminder"] = true,
        };

        if (error is not null)
            data["error"] = error;

        await _facts.WriteAsync(new FactRecord
            {
                Type = FactType.DiscordLinkPrompted,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = userId,
                Source = FactSource.Modbot,
                Data = data,
            }, ct)
            .ConfigureAwait(false);
    }
}
