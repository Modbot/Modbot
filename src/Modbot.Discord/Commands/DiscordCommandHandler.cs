using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.Discord.Bot;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Commands;

/// <summary>
/// Answers one slash command: finds the caller's Modbot account, checks the permission, runs the
/// command, and records that it happened.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The Discord user id is the only key.</strong> Whoever holds the Discord account that a
/// Modbot account lists as its Discord user id is treated as that Modbot account. That is the
/// same trust the reset-link sender already places in the field (accounts and access design
/// §4.2), and it is why the field is set by the account holder or an administrator and never
/// guessed.
/// </para>
/// <para>
/// <strong>Unlinked callers learn nothing.</strong> Not whether a name exists, not whether the bot
/// is healthy: one sentence telling them to link, and that is all. Every command, answered or
/// refused, is a <c>modbot.discord.command</c> fact -- who looked at whom through Discord is an
/// access record, like opening evidence.
/// </para>
/// </remarks>
public sealed class DiscordCommandHandler
{
    /// <summary>Modbot's own violet, for a reply that is neither good nor bad news (brand design 2026-09-16).</summary>
    private const uint Violet = CardColour.Violet;

    public const string NotLinkedMessage = "Link your Discord account in Modbot first.";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly IModbotClock _clock;
    private readonly DiscordBotStatus _status;
    private readonly LookupQuery _lookup;
    private readonly CardPictures _pictures;

    public DiscordCommandHandler(
        ModbotContext db,
        IFactWriter facts,
        IModbotClock clock,
        DiscordBotStatus status,
        LookupQuery lookup,
        CardPictures? pictures = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(lookup);

        _db = db;
        _facts = facts;
        _clock = clock;
        _status = status;
        _lookup = lookup;
        _pictures = pictures ?? new CardPictures();
    }

    public async Task<DiscordReply> HandleAsync(DiscordCommandCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (DiscordCommands.IsForEveryone(call.CommandName))
        {
            var linkReply = await LinkAsync(ct).ConfigureAwait(false);
            await RecordAsync(call, null, "answered", null, ct).ConfigureAwait(false);
            return linkReply;
        }

        var user = await _db.Users.AsNoTracking()
            .Include(u => u.Roles).ThenInclude(r => r.Role)
            .FirstOrDefaultAsync(u => u.DiscordUserId == call.DiscordUserId, ct)
            .ConfigureAwait(false);

        DiscordReply reply;
        string outcome;
        string? target = null;

        if (user is null)
        {
            outcome = "not-linked";
            reply = DiscordReply.Say(NotLinkedMessage);
        }
        else if (user.IsDisabled)
        {
            outcome = "disabled";
            reply = DiscordReply.Say("Your Modbot account is disabled.");
        }
        else if (DiscordCommands.Requires(call.CommandName) is not { } required)
        {
            outcome = "unknown-command";
            reply = DiscordReply.Say("Modbot does not know that command.");
        }
        else if (!DiscordCommands.Allows(user.EffectivePermissions, required))
        {
            outcome = "no-permission";
            reply = DiscordReply.Say(
                $"You need the \"{DiscordCommands.Label(required)}\" permission in Modbot to use /{call.CommandName}.");
        }
        else
        {
            outcome = "answered";
            (reply, target) = call.CommandName switch
            {
                DiscordCommands.Lookup => await LookupAsync(call, ct).ConfigureAwait(false),
                DiscordCommands.Recent => (await RecentAsync(call, ct).ConfigureAwait(false), null),
                _ => (await StatusAsync(ct).ConfigureAwait(false), null),
            };
        }

        await RecordAsync(call, user, outcome, target, ct).ConfigureAwait(false);
        return reply;
    }

    private async Task<(DiscordReply Reply, string? Target)> LookupAsync(DiscordCommandCall call, CancellationToken ct)
    {
        var query = call.Option(DiscordCommands.LookupUserOption) ?? string.Empty;
        var matches = await _lookup.FindAsync(query, ct).ConfigureAwait(false);
        var (style, showPictures) = await StyleAsync(ct).ConfigureAwait(false);

        if (matches.Count == 0)
        {
            return (DiscordReply.Say(
                $"Nobody in Modbot's records matches \"{ModerationEventEmbed.Fit(ModerationEventEmbed.Escape(query), 80)}\". "
                + "Modbot only knows people it has seen in the group's history."), null);
        }

        if (matches.Count > 1)
        {
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"Several people match \"{ModerationEventEmbed.Fit(ModerationEventEmbed.Escape(query), 80)}\". Run the command again with the id:");

            // The one list of people that still carries ids: the moderator is being asked to run
            // the command again with one, so here the id is the answer rather than decoration.
            foreach (var match in matches)
            {
                sb.Append('\n').Append("• ")
                    .Append(CardLink.Person(match.DisplayName, match.UserId, style.PublicAddress))
                    .Append(" — `")
                    .Append(match.UserId.Replace("`", string.Empty, StringComparison.Ordinal))
                    .Append('`');
            }

            return (DiscordReply.Say(sb.ToString()), null);
        }

        var person = matches[0];
        var summary = await _lookup.SummarizeAsync(person.UserId, ct).ConfigureAwait(false);
        var profile = summary.Profile;

        // Three pictures on one card, which is what the slots are for: the face beside the name,
        // the banner across the bottom, and the group they represent above the lot.
        var pictures = _pictures.ForMessage(showPictures);
        var picture = new CardPicture(
            Thumbnail: await pictures.AddAsync(ProfilePictures.Best(profile), ct).ConfigureAwait(false),
            Image: await pictures.AddAsync(profile?.BannerUrl, ct).ConfigureAwait(false),
            AuthorIcon: await pictures.AddAsync(profile?.RepresentedGroupIconUrl, ct).ConfigureAwait(false));

        return (DiscordReply.Card(ProfileCard(summary, style, picture), pictures.Files), person.UserId);
    }

    public static DiscordEmbedContent ProfileCard(PersonSummary summary, string? publicAddress)
        => ProfileCard(summary, new CardStyle(publicAddress, FooterIconUrl: BrandIcon.For(publicAddress)), CardPicture.None);

    /// <summary>
    /// The <c>/lookup</c> card: the one card that is about a person rather than about something
    /// that happened to them. Public so its wording is testable without a database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The person is the headline, so their name is the title, their picture is the thumbnail
    /// beside it and their banner is the picture across the bottom -- the profile as VRChat shows
    /// it. The group they represent, when they represent one, sits above the title with its icon,
    /// which is the only place a card is about a group today.
    /// </para>
    /// <para>
    /// <strong>The id is not on the card.</strong> The title links to them in Modbot, which is
    /// where a moderator gets the id with a control that copies it; printing it here put an opaque
    /// forty characters above every reply for the one reader in a hundred who wanted it.
    /// </para>
    /// </remarks>
    public static DiscordEmbedContent ProfileCard(PersonSummary summary, CardStyle style, CardPicture picture)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(style);

        var profile = summary.Profile;
        var title = string.IsNullOrWhiteSpace(profile?.DisplayName)
            ? summary.UserId
            : profile!.DisplayName!;

        var description = profile is null
            ? "Modbot has seen this id in the group's history but has not read the profile yet."
            : null;

        var eighteenPlus = profile is { Is18PlusVerified: true }
            ? "Yes"
              + (profile.Is18PlusVerifiedAt is { } since ? $", since {DiscordTime.Day(since)}" : string.Empty)
              + (profile.Is18PlusVerifiedSource == AgeVerificationSource.Manual ? " (marked by a moderator)" : string.Empty)
            : "Not seen as verified";

        var refreshed = profile?.LastRefreshedAt is { } at
            ? DiscordTime.Relative(at)
            : "Never";

        var banStatus = summary.IsBanned
            ? $"Banned {DiscordTime.Relative(summary.LastBannedAt!.Value)}"
            : summary.LastUnbannedAt is { } unbanned
                ? $"Not banned (unbanned {DiscordTime.Relative(unbanned)})"
                : "Not banned";

        var recent = summary.Recent.Count == 0
            ? "None recorded"
            : CardText.Fit(string.Join('\n', summary.Recent.Select(e => Line(e, style))), 1024);

        return new DiscordEmbedContent(
            CardText.Plain(title, 256),
            description,
            summary.IsBanned ? CardColour.Red : Violet,
            [
                new DiscordEmbedField("18+ verified", eighteenPlus, Inline: true),
                new DiscordEmbedField("Profile last refreshed", refreshed, Inline: true),
                new DiscordEmbedField("Ban status", banStatus),
                new DiscordEmbedField("Bans · kicks · warns", $"{summary.Bans} · {summary.Kicks} · {summary.Warns}", Inline: true),
                new DiscordEmbedField("Recent moderation events", recent),
            ],
            null,
            CardLink.UrlFor(CardSubject.Person, summary.UserId, style.PublicAddress),
            style.GroupFooter,
            ThumbnailUrl: picture.Thumbnail,
            ImageUrl: picture.Image,
            FooterIconUrl: style.FooterIconUrl,
            AuthorName: profile?.RepresentedGroupName is { Length: > 0 } group
                ? CardText.Plain(group, 256)
                : null,
            AuthorIconUrl: picture.AuthorIcon);
    }

    private async Task<DiscordReply> RecentAsync(DiscordCommandCall call, CancellationToken ct)
    {
        var count = DiscordCommands.RecentDefault;
        if (int.TryParse(call.Option(DiscordCommands.RecentCountOption), NumberStyles.Integer, CultureInfo.InvariantCulture, out var asked))
            count = Math.Clamp(asked, 1, DiscordCommands.RecentMax);

        var events = await _lookup.RecentAsync(count, ct).ConfigureAwait(false);

        if (events.Count == 0)
            return DiscordReply.Say("No moderation events are recorded yet.");

        var (style, _) = await StyleAsync(ct).ConfigureAwait(false);
        var description = CardText.Fit(string.Join('\n', events.Select(e => Line(e, style))), 4096);

        return DiscordReply.Card(new DiscordEmbedContent(
            events.Count == 1 ? "The latest moderation event" : $"The latest {events.Count} moderation events",
            description,
            Violet,
            [],
            null,
            null,
            style.GroupFooter,
            FooterIconUrl: style.FooterIconUrl));
    }

    private async Task<DiscordReply> StatusAsync(CancellationToken ct)
    {
        var snapshot = _status.Snapshot();
        var publicAddress = await PublicAddressAsync(ct).ConfigureAwait(false);

        var sb = new StringBuilder();

        sb.Append(snapshot.State switch
        {
            DiscordBotState.Connected when snapshot.ConnectedSince is { } since =>
                $"Modbot's Discord bot is connected (since {DiscordTime.Relative(since)}) with {snapshot.CommandsRegistered} slash commands registered.",
            DiscordBotState.Connected => $"Modbot's Discord bot is connected with {snapshot.CommandsRegistered} slash commands registered.",
            DiscordBotState.Disconnected => "Modbot's Discord bot has lost its connection and is reconnecting.",
            _ => "Modbot's Discord bot is answering, so it is connected.",
        });

        sb.Append('\n').Append(snapshot.LogChannelConfigured
            ? "Events are being posted to Discord channels."
            : "No channel is set to receive events.");

        sb.Append('\n').Append(publicAddress is null
            ? "Web app: no public address is set in Modbot's settings yet."
            : $"Web app: {publicAddress}");

        return DiscordReply.Say(sb.ToString());
    }

    /// <summary>
    /// One event as a line in a list: the time, what happened, and who -- each name a link.
    /// </summary>
    /// <remarks>
    /// The same rule as a card. A list of twenty of these used to carry forty ids, which made the
    /// reply four times as tall for nothing a moderator was going to read.
    /// </remarks>
    private static string Line(ModerationEventView e, CardStyle style)
    {
        var line = $"{DiscordTime.Absolute(e.OccurredAt)} **{ModerationEventEmbed.LabelFor(e.Type)}** — "
            + CardLink.Person(e.SubjectName, e.SubjectId, style.PublicAddress);

        if (e.ActorId is not null)
            line += " by " + CardLink.Person(e.ActorName, e.ActorId, style.PublicAddress);

        return line;
    }

    /// <summary>
    /// What the replies in this pass share, and whether pictures may be fetched for them.
    /// </summary>
    private async Task<(CardStyle Style, bool ShowPictures)> StyleAsync(CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.PublicAddress, s.ManagedGroupName, s.VRChatImagesProxied })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return (
            new CardStyle(
                settings?.PublicAddress,
                settings?.ManagedGroupName,
                BrandIcon.For(settings?.PublicAddress)),
            settings?.VRChatImagesProxied ?? true);
    }

    /// <summary>
    /// The <c>/link</c> answer: a button to the link page, built from the public address only. Says
    /// so when linking is not set up rather than sending somebody to a page that cannot work.
    /// </summary>
    private async Task<DiscordReply> LinkAsync(CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.PublicAddress, s.DiscordOAuthClientId, s.DiscordOAuthClientSecretEncrypted })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var page = Core.Discord.DiscordInvite.LinkPageFor(settings?.PublicAddress);

        if (page is null
            || string.IsNullOrWhiteSpace(settings!.DiscordOAuthClientId)
            || settings.DiscordOAuthClientSecretEncrypted is null)
        {
            return DiscordReply.Say("Account linking is not set up on this server.");
        }

        return new DiscordReply(
            "Link your VRChat account.",
            [],
            [new DiscordLinkButton(Linking.LinkPrompt.ButtonLabel, page)]);
    }

    private async Task<string?> PublicAddressAsync(CancellationToken ct)

        => await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.PublicAddress)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    private async Task RecordAsync(DiscordCommandCall call, ModbotUser? user, string outcome, string? target, CancellationToken ct)
    {
        var data = new JsonObject
        {
            ["command"] = call.CommandName,
            ["outcome"] = outcome,
        };

        if (target is not null)
            data["target"] = target;

        await _facts.WriteAsync(new FactRecord
            {
                Type = FactType.DiscordCommandRun,
                OccurredAt = _clock.UtcNow,
                SubjectPlatform = FactPlatform.Discord,
                SubjectId = call.DiscordUserId,
                ActorPlatform = user is null ? null : FactPlatform.Modbot,
                ActorId = user?.Id.ToString(),
                Source = FactSource.Discord,
                Data = data,
            }, ct)
            .ConfigureAwait(false);
    }
}
