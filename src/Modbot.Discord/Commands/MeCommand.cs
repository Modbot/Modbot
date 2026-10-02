using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Analytics.Reviews;
using Modbot.Core.Configuration;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;
using Npgsql;
using Serilog;

namespace Modbot.Discord.Commands;

/// <summary>
/// What <c>/me</c> knows about the member who ran it, before it is turned into a card.
/// </summary>
/// <param name="GroupName">The group's name, for the card's title. Null when none is stored.</param>
/// <param name="DiscordName">The member's Discord username.</param>
/// <param name="VRChatName">The linked VRChat account's name, or null when there is no link.</param>
/// <param name="MemberSince">When they joined the group, when they are a member and a link says who they are.</param>
/// <param name="InGroup">Whether the linked account is in the group. Null when there is no link or no group is set.</param>
/// <param name="Roles">The names of the Discord roles Modbot gave them and they still hold.</param>
/// <param name="Banned">Banned from the group or the Discord server, as Modbot's ban lists have it.</param>
/// <param name="LinkPage">The link page, when there is no link and linking is set up.</param>
/// <param name="GetsEventInvites">They asked for event invites and have not stopped them.</param>
public sealed record MeView(
    string? GroupName,
    string DiscordName,
    string? VRChatName,
    DateTimeOffset? MemberSince,
    bool? InGroup,
    IReadOnlyList<string> Roles,
    bool Banned,
    string? LinkPage,
    bool GetsEventInvites = false);

/// <summary>
/// The member-facing <c>/me</c> command and its two buttons (Discord /me design, 2026-09-30).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only ever about the person who ran it.</strong> The caller's Discord id is the only key:
/// it finds their link, the link finds their VRChat account, and every read below is of a row
/// whose subject is that person -- their link, their group membership, their ban entries, the
/// roles Modbot gave them. Nothing that names anybody else is read: not who they were in an
/// instance with, not who reported them, not what a moderator wrote, not flags or evidence.
/// </para>
/// <para>
/// <strong>Stored data only.</strong> No request goes to VRChat. The Discord username comes with
/// the interaction itself.
/// </para>
/// <para>
/// <strong>Asking to delete is a request, never a deletion.</strong> It opens a review on the
/// Reviews page, records a fact about the person, and posts one line to the alerts channel when
/// one is set. One open request per Discord account (the open-review index enforces it), and at
/// most <see cref="DailyCap"/> in any 24 hours across the server, so a flood cannot bury the
/// Reviews page.
/// </para>
/// </remarks>
public sealed class MeCommand
{
    public const string KeepsButton = DiscordActionButton.Prefix + "me:keeps";
    public const string DeleteButton = DiscordActionButton.Prefix + "me:delete";
    public const string InvitesOnButton = DiscordActionButton.Prefix + "me:invites-on";
    public const string InvitesOffButton = DiscordActionButton.Prefix + "me:invites-off";

    public const string KeepsLabel = "What Modbot keeps";
    public const string DeleteLabel = "Ask to delete my data";
    public const string LinkLabel = "Link your VRChat account";
    public const string InvitesOnLabel = "Get event invites";
    public const string InvitesOffLabel = "Stop event invites";

    public const string InvitesOnMessage = "You will get invites to the group's events.";
    public const string InvitesOffMessage = "You will not get invites to the group's events.";

    /// <summary>Deletion requests the whole server may open in any 24 hours.</summary>
    public const int DailyCap = 20;

    public const string OffMessage = "/me is turned off on this server.";
    public const string TooFastMessage = "Slow down. Try again in a minute.";
    public const string SentMessage = "Sent to the group's staff. Case files are kept.";
    public const string AlreadyAskedMessage = "You have already asked. The group's staff have it.";
    public const string CapMessage = "Too many requests. Try again later.";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly ReviewFacts _reviews;
    private readonly IModbotClock _clock;
    private readonly MemberCommandLimits _limits;
    private readonly DemoMode? _demo;
    private readonly ILogger _log;

    public MeCommand(
        ModbotContext db,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        ReviewFacts reviews,
        IModbotClock clock,
        MemberCommandLimits limits,
        DemoMode? demo = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(reviews);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limits);

        _db = db;
        _facts = facts;
        _partitions = partitions;
        _reviews = reviews;
        _clock = clock;
        _limits = limits;
        _demo = demo;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <summary>
    /// Whether <c>/me</c> answers: the operator switched it on, and this is not a demo. The bot is
    /// never started in a demo, so the second half only matters if that ever changes.
    /// </summary>
    public async Task<bool> IsOnAsync(CancellationToken ct)
    {
        if (_demo is { Decided: true, IsOn: true })
            return false;

        return await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordMeCommand)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Counts one use by this account, and says whether it is allowed.</summary>
    public bool TryUse(string discordUserId) => _limits.TryUse(discordUserId, _clock.UtcNow);

    /// <summary>The <c>/me</c> card for the member who ran it.</summary>
    public async Task<DiscordReply> AnswerAsync(string discordUserId, string discordUsername, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);

        var view = await ReadAsync(discordUserId, discordUsername, ct).ConfigureAwait(false);
        return Reply(view);
    }

    /// <summary>
    /// Everything the card shows, read from rows about this one person. Public so a test can check
    /// what is read without going through Discord.
    /// </summary>
    public async Task<MeView> ReadAsync(string discordUserId, string discordUsername, CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new
            {
                s.ManagedGroupId,
                s.ManagedGroupName,
                s.DiscordGuildId,
                s.PublicAddress,
                s.DiscordOAuthClientId,
                s.DiscordOAuthClientSecretEncrypted,
            })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var groupId = settings?.ManagedGroupId;
        var guildId = settings?.DiscordGuildId?.Trim();

        // Only the current link counts: an ended one says who they were, not who they are.
        var link = await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => l.DiscordUserId == discordUserId && l.UnlinkedAt == null)
            .Select(l => new { l.VRChatUserId, l.VRChatDisplayName, l.LinkedRoleId, l.EighteenPlusRoleId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        string? vrchatName = null;
        DateTimeOffset? memberSince = null;
        bool? inGroup = null;
        var groupBanned = false;

        if (link is not null)
        {
            var stored = await _db.VRChatUsers.AsNoTracking()
                .Where(u => u.UserId == link.VRChatUserId)
                .Select(u => u.DisplayName)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            vrchatName = FirstName(stored, link.VRChatDisplayName) ?? link.VRChatUserId;

            if (!string.IsNullOrWhiteSpace(groupId))
            {
                var membership = await _db.GroupMembers.AsNoTracking()
                    .Where(m => m.GroupId == groupId && m.UserId == link.VRChatUserId && m.LeftAt == null)
                    .Select(m => new { m.JoinedAt, m.FirstSeenAt })
                    .FirstOrDefaultAsync(ct)
                    .ConfigureAwait(false);

                inGroup = membership is not null;
                memberSince = membership is null ? null : membership.JoinedAt ?? membership.FirstSeenAt;

                groupBanned = await _db.GroupBans.AsNoTracking()
                    .AnyAsync(b => b.GroupId == groupId && b.UserId == link.VRChatUserId && b.LiftedAt == null, ct)
                    .ConfigureAwait(false);
            }
        }

        var discordBanned = !string.IsNullOrWhiteSpace(guildId)
            && await _db.DiscordBans.AsNoTracking()
                .AnyAsync(b => b.GuildId == guildId && b.UserId == discordUserId && b.LiftedAt == null, ct)
                .ConfigureAwait(false);

        var roles = string.IsNullOrWhiteSpace(guildId)
            ? []
            : await RolesAsync(guildId, discordUserId, link?.LinkedRoleId, link?.EighteenPlusRoleId, ct).ConfigureAwait(false);

        var getsEventInvites = await _db.EventInviteChoices.AsNoTracking()
            .AnyAsync(c => c.DiscordUserId == discordUserId && c.Wants, ct)
            .ConfigureAwait(false);

        string? linkPage = null;
        if (link is null
            && !string.IsNullOrWhiteSpace(settings?.DiscordOAuthClientId)
            && settings.DiscordOAuthClientSecretEncrypted is not null)
        {
            linkPage = Core.Discord.DiscordInvite.LinkPageFor(settings.PublicAddress);
        }

        return new MeView(
            settings?.ManagedGroupName,
            string.IsNullOrWhiteSpace(discordUsername) ? discordUserId : discordUsername,
            vrchatName,
            memberSince,
            inGroup,
            roles,
            groupBanned || discordBanned,
            linkPage,
            getsEventInvites);
    }

    /// <summary>
    /// The Discord roles Modbot gave this account and it still holds, by name.
    /// </summary>
    /// <remarks>
    /// Three sources, the same three the role jobs use to decide what they may take back. The
    /// linked and 18+ roles recorded on the link are the ones Modbot gave and believes they still
    /// hold. A role from role sync counts when Modbot's own copy records say the newest thing it did
    /// with that role for this account was to give it, and a role from a list when the list's
    /// given-row stands; either only while the member list says they still have it. A role somebody
    /// gave them by hand is never listed.
    /// </remarks>
    private async Task<IReadOnlyList<string>> RolesAsync(
        string guildId, string discordUserId, string? linkedRoleId, string? eighteenPlusRoleId, CancellationToken ct)
    {
        var ids = new List<string>();

        foreach (var id in new[] { linkedRoleId, eighteenPlusRoleId })
        {
            if (!string.IsNullOrWhiteSpace(id) && !ids.Contains(id, StringComparer.Ordinal))
                ids.Add(id);
        }

        var records = await _db.CopiedActions.AsNoTracking()
            .Where(c => c.Direction == CopyDirections.ToDiscord
                        && c.Done == true
                        && (c.Kind == CopyKinds.RoleGiven || c.Kind == CopyKinds.RoleTaken)
                        && c.RoleId != null
                        && c.SubjectId == discordUserId)
            .Select(c => new { RoleId = c.RoleId!, c.Kind, c.StartedAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var given = records
            .GroupBy(c => c.RoleId, StringComparer.Ordinal)
            .Where(g => g.OrderByDescending(c => c.StartedAt).ThenBy(c => c.Kind == CopyKinds.RoleGiven).First().Kind == CopyKinds.RoleGiven)
            .Select(g => g.Key)
            .ToList();

        // And the roles a saved list gave them and has not taken back (roles from lists design §3).
        var fromLists = await _db.DiscordListRolesGiven.AsNoTracking()
            .Where(g => g.DiscordUserId == discordUserId)
            .Join(_db.DiscordListRoles.AsNoTracking(), g => g.ListRoleId, p => p.Id, (g, p) => p.DiscordRoleId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        given.AddRange(fromLists.Where(id => !given.Contains(id, StringComparer.Ordinal)));

        if (given.Count > 0)
        {
            var held = await _db.DiscordMembers.AsNoTracking()
                .Where(m => m.GuildId == guildId && m.UserId == discordUserId && m.LeftAt == null)
                .Select(m => m.Roles)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            var holding = RoleIds(held);

            foreach (var id in given)
            {
                if (holding.Contains(id) && !ids.Contains(id, StringComparer.Ordinal))
                    ids.Add(id);
            }
        }

        if (ids.Count == 0)
            return [];

        var names = await _db.DiscordRoles.AsNoTracking()
            .Where(r => r.GuildId == guildId && r.RemovedAt == null && ids.Contains(r.RoleId))
            .Select(r => new { r.RoleId, r.Name, r.Position })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Discord's own order, highest first, the way the member's profile lists them.
        return [.. names.OrderByDescending(r => r.Position).ThenBy(r => r.RoleId, StringComparer.Ordinal).Select(r => r.Name)];
    }

    /// <summary>
    /// The <c>/me</c> reply: the card, a link button when there is no link, the two buttons, and
    /// "Get event invites" or "Stop event invites", whichever changes what they have now.
    /// </summary>
    public static DiscordReply Reply(MeView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        return new DiscordReply(
            null,
            [Card(view)],
            view.LinkPage is { } page ? [new DiscordLinkButton(LinkLabel, page)] : null,
            null,
            [
                new DiscordActionButton(KeepsLabel, KeepsButton),
                new DiscordActionButton(DeleteLabel, DeleteButton),
                view.GetsEventInvites
                    ? new DiscordActionButton(InvitesOffLabel, InvitesOffButton)
                    : new DiscordActionButton(InvitesOnLabel, InvitesOnButton),
            ]);
    }

    /// <summary>
    /// "Get event invites" or "Stop event invites": the member's own choice, kept with when they
    /// made it and recorded as a fact (calendar auto-invite design §2.1). Invites to an event's
    /// instance go only to people who chose to get them.
    /// </summary>
    public async Task<DiscordReply> SetEventInvitesAsync(string discordUserId, bool wants, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);

        var now = _clock.UtcNow;

        // The VRChat account linked now, kept for the record and for a purge by VRChat id. Inviting
        // matches a VRChat id through the link as it stands then, not through this.
        var vrchat = await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => l.DiscordUserId == discordUserId && l.UnlinkedAt == null)
            .Select(l => l.VRChatUserId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        // Twice at most: a double press can make two first choices at once, and the second then
        // finds the row the first one wrote and is an ordinary update.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SaveChoiceAsync(discordUserId, vrchat, wants, now, ct).ConfigureAwait(false);
                break;
            }
            catch (DbUpdateException e) when (attempt == 0
                && e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                _db.ChangeTracker.Clear();
            }
        }

        return DiscordReply.Say(wants ? InvitesOnMessage : InvitesOffMessage);
    }

    /// <summary>The choice and its fact, in one transaction. A press that changes nothing records nothing.</summary>
    private async Task SaveChoiceAsync(string discordUserId, string? vrchat, bool wants, DateTimeOffset now, CancellationToken ct)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var choice = await _db.EventInviteChoices
            .FirstOrDefaultAsync(c => c.DiscordUserId == discordUserId, ct)
            .ConfigureAwait(false);

        var changed = choice is null || choice.Wants != wants;

        if (choice is null)
        {
            choice = new EventInviteChoice { DiscordUserId = discordUserId };
            _db.EventInviteChoices.Add(choice);
        }

        choice.Wants = wants;
        choice.VRChatUserId = vrchat;
        choice.ChangedAt = now;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (changed)
        {
            await _facts.WriteAsync(new FactRecord
                {
                    Type = wants ? FactType.EventInvitesWanted : FactType.EventInvitesStopped,
                    OccurredAt = now,
                    SubjectPlatform = FactPlatform.Discord,
                    SubjectId = discordUserId,
                    Source = FactSource.Discord,
                    Data = new JsonObject { ["via"] = "me" },
                }, ct)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The card. Labels and values only: who they are on each side, since when they are a member,
    /// the roles Modbot gave them, and whether they are in good standing.
    /// </summary>
    public static DiscordEmbedContent Card(MeView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        var fields = new List<DiscordEmbedField>
        {
            new("VRChat", view.VRChatName is { } name ? $"{Name(name)} (linked)" : "Not linked", Inline: true),
            new("Discord", Name(view.DiscordName), Inline: true),
        };

        if (view.InGroup is true && view.MemberSince is { } since)
            fields.Add(new DiscordEmbedField("Member since", DiscordTime.LongDay(since)));
        else if (view.InGroup is false)
            fields.Add(new DiscordEmbedField("Member since", "Not in the group"));

        fields.Add(new DiscordEmbedField(
            "Roles from Modbot",
            view.Roles.Count == 0 ? "None" : CardText.Fit(string.Join(", ", view.Roles.Select(Name)), 1024)));

        fields.Add(new DiscordEmbedField("Standing", view.Banned ? "Banned" : "Good"));
        fields.Add(new DiscordEmbedField("Event invites", view.GetsEventInvites ? "On" : "Off"));

        return new DiscordEmbedContent(
            string.IsNullOrWhiteSpace(view.GroupName) ? "Modbot" : CardText.Plain(view.GroupName, 256),
            null,
            view.Banned ? CardColour.Red : CardColour.Violet,
            fields,
            null,
            null,
            null);
    }

    /// <summary>The "What Modbot keeps" answer: the same fixed list for everybody.</summary>
    public static DiscordReply KeepsReply()
        => DiscordReply.Card(new DiscordEmbedContent(
            WhatModbotKeeps.Title,
            null,
            CardColour.Violet,
            [.. WhatModbotKeeps.Kinds.Select(k => new DiscordEmbedField(
                k.Heading,
                string.Join('\n', k.Lines.Select(line => "• " + line))))],
            null,
            null,
            null));

    /// <summary>
    /// "Ask to delete my data": records the request, opens a review and tells the alerts channel.
    /// Deletes nothing.
    /// </summary>
    /// <param name="gateway">The session to post the alerts line with, or null when there is none.</param>
    public async Task<DiscordReply> AskToDeleteAsync(
        string discordUserId, string discordUsername, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);

        var already = await _db.Reviews.AsNoTracking()
            .AnyAsync(r => r.Signal == ReviewSignal.DataDeletion
                           && r.ModeratorPlatform == FactPlatform.Discord
                           && r.ModeratorId == discordUserId
                           && r.State == ReviewState.Open, ct)
            .ConfigureAwait(false);

        if (already)
            return DiscordReply.Say(AlreadyAskedMessage);

        var now = _clock.UtcNow;

        var today = await _db.Reviews.AsNoTracking()
            .CountAsync(r => r.Signal == ReviewSignal.DataDeletion && r.OpenedAt > now.AddDays(-1), ct)
            .ConfigureAwait(false);

        if (today >= DailyCap)
            return DiscordReply.Say(CapMessage);

        var link = await _db.DiscordAccountLinks.AsNoTracking()
            .Where(l => l.DiscordUserId == discordUserId && l.UnlinkedAt == null)
            .Select(l => new { l.VRChatUserId, l.VRChatDisplayName })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        string? vrchatName = null;
        if (link is not null)
        {
            var stored = await _db.VRChatUsers.AsNoTracking()
                .Where(u => u.UserId == link.VRChatUserId)
                .Select(u => u.DisplayName)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            vrchatName = FirstName(stored, link.VRChatDisplayName);
        }

        var username = string.IsNullOrWhiteSpace(discordUsername) ? null : discordUsername;
        var who = vrchatName ?? username ?? discordUserId;

        var review = new Review
        {
            Id = Guid.CreateVersion7(now),
            ModeratorPlatform = FactPlatform.Discord,
            ModeratorId = discordUserId,
            Signal = ReviewSignal.DataDeletion,
            About = discordUserId,
            WindowStart = now,
            WindowEnd = now,
            Summary = CardText.Plain($"{who} asked for their data to be deleted.", 1000),
            Evidence = new JsonObject
            {
                ["discordUserId"] = discordUserId,
                ["discordUsername"] = username,
                ["vrchatUserId"] = link?.VRChatUserId,
                ["vrchatDisplayName"] = vrchatName,
                ["askedAt"] = now.ToString("O"),
            }.ToJsonString(),
            State = ReviewState.Open,
            OpenedAt = now,
            UpdatedAt = now,
        };

        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);

        try
        {
            // The review and both facts commit together: a request the staff cannot see, or one
            // with no record of it in the log, is the thing this exists to prevent.
            await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            _db.Reviews.Add(review);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await _reviews.OpenedAsync(review, ct).ConfigureAwait(false);

            var data = new JsonObject
            {
                ["reviewId"] = review.Id.ToString(),
                ["discordUsername"] = username,
                ["description"] = review.Summary,
            };

            if (link is not null)
            {
                data["vrchatUserId"] = link.VRChatUserId;
                data["vrchatDisplayName"] = vrchatName;
            }

            await _facts.WriteAsync(new FactRecord
                {
                    Type = FactType.DataDeletionAsked,
                    OccurredAt = now,
                    SubjectPlatform = FactPlatform.Discord,
                    SubjectId = discordUserId,
                    Source = FactSource.Discord,
                    Data = data,
                }, ct)
                .ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two presses at once: the other one opened it.
            _db.ChangeTracker.Clear();
            return DiscordReply.Say(AlreadyAskedMessage);
        }

        await TellStaffAsync(review, who, username, gateway, ct).ConfigureAwait(false);

        return DiscordReply.Say(SentMessage);
    }

    /// <summary>
    /// One line in the alerts channel, when one is set, with a button to the review. A failure of
    /// any kind is logged and nothing more: the review is already committed and on the Reviews
    /// page, so the member is still told it was sent.
    /// </summary>
    private async Task TellStaffAsync(Review review, string who, string? username, IDiscordGateway? gateway, CancellationToken ct)
    {
        if (gateway is not { State: DiscordGatewayState.Ready })
            return;

        try
        {
            await PostStaffLineAsync(review, who, username, gateway, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning(e, "Could not tell the alerts channel about a request to delete data");
        }
    }

    private async Task PostStaffLineAsync(Review review, string who, string? username, IDiscordGateway gateway, CancellationToken ct)
    {
        var channel = await _db.AlertSettings.AsNoTracking()
            .Where(a => a.Id == 1)
            .Select(a => a.DiscordChannelId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(channel))
            return;

        var publicAddress = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.PublicAddress)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var outcome = await gateway.PostAsync(
                channel,
                StaffLine(who, username),
                [],
                ReviewLink(review.Id, publicAddress) is { } url ? [new DiscordLinkButton("Open review", url)] : null,
                ct)
            .ConfigureAwait(false);

        if (!outcome.Sent)
            _log.Warning("Could not tell the alerts channel about a request to delete data: {Reason}", outcome.Error);
    }

    /// <summary>The line the staff see: who asked, by both names when they differ.</summary>
    public static string StaffLine(string who, string? username)
    {
        var sb = new StringBuilder();
        sb.Append("**").Append(Name(who)).Append("**");

        if (username is not null && !string.Equals(username, who, StringComparison.Ordinal))
            sb.Append(" (Discord: ").Append(Name(username)).Append(')');

        sb.Append(" asked the staff to delete their data.");
        return sb.ToString();
    }

    /// <summary>The Reviews page opened at this review, or null without a public address.</summary>
    public static string? ReviewLink(Guid reviewId, string? publicAddress)
        => string.IsNullOrWhiteSpace(publicAddress)
            ? null
            : $"{publicAddress.Trim().TrimEnd('/')}/reviews?review={reviewId}";

    private static string? FirstName(params string?[] names)
        => names.FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

    private static string Name(string name) => CardText.Fit(CardText.EscapeName(name), CardText.MaxNameLength);

    private static HashSet<string> RoleIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(json) ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
