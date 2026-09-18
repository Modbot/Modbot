using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Linking;

/// <summary>How a new member was asked to link, as recorded in the prompt fact.</summary>
public static class LinkPromptVia
{
    public const string DirectMessage = "dm";
    public const string BackupChannel = "channel";
    public const string None = "none";
}

/// <summary>
/// Asks a member who just joined the Discord server to link their VRChat account: a DM, or a
/// mention in the backup channel when their DMs are closed (Discord account linking design §8).
/// </summary>
/// <remarks>
/// <para>
/// The button opens the link page built from the public address, never from anything Discord
/// sent: the same rule every link Modbot sends follows (accounts and access design §4.2).
/// </para>
/// <para>
/// A member who is already linked is not asked. Leaving the server took their roles with it, so
/// the roles Modbot recorded as given are forgotten and the role job gives them back.
/// </para>
/// </remarks>
public sealed class LinkPrompt
{
    public const string ButtonLabel = "Link VRChat account";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly DiscordLinkSignal _signal;

    public LinkPrompt(
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

    /// <summary>Handles one member joining. Returns how they were asked, or null when they were not.</summary>
    public async Task<string?> HandleAsync(IDiscordGateway gateway, DiscordMemberJoin member, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(member);

        if (member.IsBot)
            return null;

        // Back in the server: whatever roles Modbot gave them went when they left. Ended links too,
        // so the job does not try to take away a role they no longer hold.
        var rows = await _db.DiscordAccountLinks
            .Where(l => l.DiscordUserId == member.UserId
                        && (l.UnlinkedAt == null || l.LinkedRoleId != null || l.EighteenPlusRoleId != null || l.NotInServerAt != null))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            row.LinkedRoleId = null;
            row.EighteenPlusRoleId = null;
            row.NotInServerAt = null;
        }

        if (rows.Count > 0)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            _signal.Changed();
        }

        var settings = await _db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, ct)
            .ConfigureAwait(false);

        if (settings is not { DiscordLinkPromptNewMembers: true })
            return null;

        if (rows.Any(r => r.IsActive))
            return null;

        var page = DiscordInvite.LinkPageFor(settings.PublicAddress);

        if (page is null
            || string.IsNullOrWhiteSpace(settings.DiscordOAuthClientId)
            || settings.DiscordOAuthClientSecretEncrypted is null)
        {
            await RecordAsync(member, LinkPromptVia.None, "Linking is not set up: the OAuth client and the public address are needed.", ct)
                .ConfigureAwait(false);
            return LinkPromptVia.None;
        }

        var serverName = await _db.DiscordServers.AsNoTracking()
            .Where(s => s.GuildId == member.GuildId)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var buttons = new[] { new DiscordLinkButton(ButtonLabel, page) };

        var dm = await gateway.SendDirectMessageAsync(member.UserId, DirectMessageText(serverName), buttons, ct)
            .ConfigureAwait(false);

        if (dm.Sent)
        {
            await RecordAsync(member, LinkPromptVia.DirectMessage, null, ct).ConfigureAwait(false);
            return LinkPromptVia.DirectMessage;
        }

        var channel = settings.DiscordLinkBackupChannelId?.Trim();

        if (!dm.DirectMessagesClosed || string.IsNullOrEmpty(channel))
        {
            await RecordAsync(member, LinkPromptVia.None, dm.Error, ct).ConfigureAwait(false);
            return LinkPromptVia.None;
        }

        var mention = await gateway.MentionAsync(channel, member.UserId, BackupChannelText, buttons, ct)
            .ConfigureAwait(false);

        if (mention.Sent)
        {
            await RecordAsync(member, LinkPromptVia.BackupChannel, dm.Error, ct).ConfigureAwait(false);
            return LinkPromptVia.BackupChannel;
        }

        await RecordAsync(member, LinkPromptVia.None, mention.Error, ct).ConfigureAwait(false);
        return LinkPromptVia.None;
    }

    public const string BackupChannelText = "your DMs are closed. Link your VRChat account here.";

    /// <remarks>
    /// The server's name is a name, not a sentence, so it is escaped the way every other name on a
    /// card is: a server called <c>**everyone**</c> must not make the greeting shout, and one with
    /// a <c>]</c> in it must not break out of anything.
    /// </remarks>
    public static string DirectMessageText(string? serverName)
        => string.IsNullOrWhiteSpace(serverName)
            ? "Welcome! Link your VRChat account."
            : $"Welcome to **{CardText.Fit(CardText.EscapeName(serverName), CardText.MaxNameLength)}**! Link your VRChat account.";

    private async Task RecordAsync(DiscordMemberJoin member, string via, string? error, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        var data = new JsonObject
        {
            ["via"] = via,
            ["discordUsername"] = member.Username,
        };

        if (error is not null)
            data["error"] = error;

        await _facts.WriteAsync(new FactRecord
            {
                Type = FactType.DiscordLinkPrompted,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = member.UserId,
                Source = FactSource.Modbot,
                Data = data,
            }, ct)
            .ConfigureAwait(false);
    }
}
