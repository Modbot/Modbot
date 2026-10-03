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
/// <strong>Only a Discord account the person proved is them.</strong> Whoever holds the Discord
/// account a Modbot account proved, by signing in to Discord from its account page, is treated as
/// that Modbot account (<see cref="StaffDiscord"/>, accounts and access design §4.6). An id that was
/// typed in before proving existed counts until <see cref="StaffDiscord.TypedIdsEnd"/>, and only
/// while no other account typed the same one.
/// </para>
/// <para>
/// <strong>Unlinked callers learn nothing.</strong> Not whether a name exists, not whether the bot
/// is healthy: one sentence telling them to link, and that is all. The exceptions are
/// <c>/link</c> and <c>/me</c>, which are for every member and only ever about the caller
/// (<see cref="MeCommand"/>), and <c>/verify</c>, which is how a staff member proves their
/// Discord account in the first place (<see cref="VerifyCommand"/>). Every command, answered or
/// refused, is a <c>modbot.discord.command</c> fact -- who looked at whom through Discord is an
/// access record, like opening evidence.
/// </para>
/// </remarks>
public sealed class DiscordCommandHandler
{
    /// <summary>Modbot's own violet, for a reply that is neither good nor bad news (brand design 2026-09-16).</summary>
    private const uint Violet = CardColour.Violet;

    public const string NotLinkedMessage = "Connect Discord on your account page in Modbot first.";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly IModbotClock _clock;
    private readonly DiscordBotStatus _status;
    private readonly LookupQuery _lookup;
    private readonly CardPictures _pictures;
    private readonly MeCommand? _me;
    private readonly VerifyCommand? _verify;

    public DiscordCommandHandler(
        ModbotContext db,
        IFactWriter facts,
        IModbotClock clock,
        DiscordBotStatus status,
        LookupQuery lookup,
        CardPictures? pictures = null,
        MeCommand? me = null,
        VerifyCommand? verify = null)
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
        _me = me;
        _verify = verify;
    }

    public async Task<DiscordReply> HandleAsync(DiscordCommandCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (call.CommandName == DiscordCommands.Me)
            return await MeAsync(call, ct).ConfigureAwait(false);

        if (call.CommandName == DiscordCommands.Help)
            return await HelpAsync(call, ct).ConfigureAwait(false);

        if (call.CommandName == DiscordCommands.Verify)
            return await VerifyAsync(call, ct).ConfigureAwait(false);

        if (DiscordCommands.IsForEveryone(call.CommandName))
        {
            var linkReply = await LinkAsync(ct).ConfigureAwait(false);
            await RecordAsync(call, null, "answered", null, ct).ConfigureAwait(false);
            return linkReply;
        }

        var user = await AccountAsync(call.DiscordUserId, ct).ConfigureAwait(false);

        DiscordReply reply;
        string outcome;
        string? target = null;
        string? discordTarget = null;

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
            (reply, target, discordTarget) = call.CommandName switch
            {
                DiscordCommands.Lookup => await LookupAsync(call, LookupSight.Of(user.EffectivePermissions), ct).ConfigureAwait(false),
                DiscordCommands.Recent => (await RecentAsync(call, ct).ConfigureAwait(false), null, null),
                _ => (await StatusAsync(ct).ConfigureAwait(false), null, null),
            };
        }

        await RecordAsync(call, user, outcome, target, ct, discordTarget).ConfigureAwait(false);
        return reply;
    }

    /// <summary>
    /// The Modbot account that proved the caller's Discord account is theirs
    /// (<see cref="StaffDiscord"/>), with its roles for the permission check, or null. The one
    /// rule for every command, <c>/help</c> and the suggestions alike.
    /// </summary>
    private Task<ModbotUser?> AccountAsync(string discordUserId, CancellationToken ct)
        => StaffDiscord.AccountForAsync(_db, discordUserId, _clock.UtcNow, ct);

    /// <summary>
    /// <c>/help</c>: the commands the caller can use, each with what it does, and no more.
    /// </summary>
    /// <remarks>
    /// Any member may ask, and an unlinked one is told about <c>/link</c>, <c>/me</c> and
    /// <c>/help</c> only: the staff commands would refuse them, and listing them would say the
    /// server has staff tools to try. A linked moderator is told about the staff commands their
    /// Modbot permissions let them run, so the list is the same answer each command would give.
    /// </remarks>
    private async Task<DiscordReply> HelpAsync(DiscordCommandCall call, CancellationToken ct)
    {
        var meOn = _me is not null && await _me.IsOnAsync(ct).ConfigureAwait(false);
        var user = await AccountAsync(call.DiscordUserId, ct).ConfigureAwait(false);
        var held = user is { IsDisabled: false } ? user.EffectivePermissions : (ModbotPermissions?)null;

        var lines = DiscordCommands.For(meOn)
            .Where(c => DiscordCommands.IsForEveryone(c.Name)
                        || (held is { } permissions
                            && DiscordCommands.Requires(c.Name) is { } required
                            && DiscordCommands.Allows(permissions, required)))
            .Select(HelpLine);

        await RecordAsync(call, user, "answered", null, ct).ConfigureAwait(false);
        return DiscordReply.Say(string.Join('\n', lines));
    }

    /// <summary><c>/lookup user: discord:</c> — Look up a person in Modbot's records.</summary>
    private static string HelpLine(DiscordCommandDefinition command)
    {
        var usage = new StringBuilder("`/").Append(command.Name);

        foreach (var option in command.Options)
            usage.Append(' ').Append(option.Name).Append(':');

        return usage.Append("` — ").Append(command.Description).ToString();
    }

    /// <summary>
    /// Suggestions under <c>/lookup user:</c> while a name is typed. Nothing for anybody the
    /// command itself would refuse, so the list cannot be used to read the records around it.
    /// </summary>
    /// <remarks>
    /// Not recorded as a <c>modbot.discord.command</c> fact: Discord asks on every key press, and
    /// a list of names is not a look at anybody. The <c>/lookup</c> that follows is recorded.
    /// </remarks>
    public async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(DiscordSuggestionAsk ask, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ask);

        if (ask.CommandName != DiscordCommands.Lookup || ask.OptionName != DiscordCommands.LookupUserOption)
            return [];

        var user = await AccountAsync(ask.DiscordUserId, ct).ConfigureAwait(false);

        if (user is null
            || user.IsDisabled
            || !DiscordCommands.Allows(user.EffectivePermissions, ModbotPermissions.ViewProfile))
        {
            return [];
        }

        return await _lookup.SuggestAsync(ask.Typed, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>/me</c>: any member, about themselves only (Discord /me design). Answered only while the
    /// operator has it switched on; a call that arrives after it was switched off, before Discord
    /// dropped the command, is told so. A call over the per-person limit is refused and not
    /// recorded, so holding a key down cannot fill the audit log.
    /// </summary>
    private async Task<DiscordReply> MeAsync(DiscordCommandCall call, CancellationToken ct)
    {
        if (_me is null || !await _me.IsOnAsync(ct).ConfigureAwait(false))
        {
            await RecordAsync(call, null, "off", null, ct).ConfigureAwait(false);
            return DiscordReply.Say(MeCommand.OffMessage);
        }

        if (!_me.TryUse(call.DiscordUserId))
            return DiscordReply.Say(MeCommand.TooFastMessage);

        var reply = await _me.AnswerAsync(call.DiscordUserId, call.DiscordUsername, ct).ConfigureAwait(false);
        await RecordAsync(call, null, "answered", null, ct).ConfigureAwait(false);
        return reply;
    }

    /// <summary>
    /// <c>/verify code:</c>: proves the caller's Discord account is the Modbot account that was
    /// shown the code (<see cref="VerifyCommand"/>). Recorded like every command, with the account
    /// as the actor once it is connected and never with the code; a call over the per-person limit
    /// is refused and not recorded, as with <c>/me</c>.
    /// </summary>
    private async Task<DiscordReply> VerifyAsync(DiscordCommandCall call, CancellationToken ct)
    {
        if (_verify is null)
        {
            await RecordAsync(call, null, "unknown-command", null, ct).ConfigureAwait(false);
            return DiscordReply.Say("Modbot does not know that command.");
        }

        var answer = await _verify
            .RunAsync(call.DiscordUserId, call.DiscordUsername, call.Option(DiscordCommands.VerifyCodeOption), ct)
            .ConfigureAwait(false);

        if (answer.Outcome != VerifyOutcome.TooFast)
        {
            var outcome = answer.Outcome switch
            {
                VerifyOutcome.Connected => "connected",
                VerifyOutcome.Taken => "taken",
                _ => "wrong-code",
            };

            await RecordAsync(call, answer.Account, outcome, null, ct).ConfigureAwait(false);
        }

        return DiscordReply.Say(answer.Message);
    }

    /// <summary>
    /// A press on one of the bot's buttons. Today only the ones under <c>/me</c> exist -- what
    /// Modbot keeps, asking to delete, and getting or stopping event invites -- and they follow the
    /// same switch and the same per-person limit as the command.
    /// </summary>
    /// <param name="gateway">The session, for the line a deletion request posts to the alerts channel.</param>
    public async Task<DiscordReply> HandleButtonAsync(DiscordButtonPress press, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        if (press.ButtonId is not (MeCommand.KeepsButton or MeCommand.DeleteButton
            or MeCommand.InvitesOnButton or MeCommand.InvitesOffButton))
        {
            return DiscordReply.Say("Modbot does not know that button.");
        }

        // Stopping event invites always works, even from an old /me reply after the switch went
        // off: nobody should be left unable to stop something they asked for.
        var stopping = press.ButtonId == MeCommand.InvitesOffButton;

        if (_me is null || (!stopping && !await _me.IsOnAsync(ct).ConfigureAwait(false)))
            return DiscordReply.Say(MeCommand.OffMessage);

        if (!_me.TryUse(press.DiscordUserId))
            return DiscordReply.Say(MeCommand.TooFastMessage);

        return press.ButtonId switch
        {
            MeCommand.KeepsButton => MeCommand.KeepsReply(),
            MeCommand.InvitesOnButton => await _me.SetEventInvitesAsync(press.DiscordUserId, wants: true, ct).ConfigureAwait(false),
            MeCommand.InvitesOffButton => await _me.SetEventInvitesAsync(press.DiscordUserId, wants: false, ct).ConfigureAwait(false),
            _ => await _me.AskToDeleteAsync(press.DiscordUserId, press.DiscordUsername, gateway, ct).ConfigureAwait(false),
        };
    }

    /// <summary>
    /// <c>/lookup</c>: one person, named by a VRChat name or id typed into <c>user</c>, or by a
    /// Discord member picked in <c>discord</c>. Either way the answer is the whole person: the
    /// other account through the link, and the history of both.
    /// </summary>
    /// <returns>
    /// The reply, and the accounts it was about for the command fact: the VRChat id under
    /// <c>target</c> as before, and the Discord id beside it.
    /// </returns>
    private async Task<(DiscordReply Reply, string? Target, string? DiscordTarget)> LookupAsync(
        DiscordCommandCall call, LookupSight sight, CancellationToken ct)
    {
        var query = call.Option(DiscordCommands.LookupUserOption)?.Trim() ?? string.Empty;
        var discordId = call.Option(DiscordCommands.LookupDiscordOption)?.Trim() ?? string.Empty;
        var (style, showPictures) = await StyleAsync(ct).ConfigureAwait(false);

        if (query.Length > 0 && discordId.Length > 0)
            return (DiscordReply.Say("Pick a VRChat name or a Discord member, not both."), null, null);

        if (query.Length == 0 && discordId.Length == 0)
            return (DiscordReply.Say("Pick a VRChat name or a Discord member."), null, null);

        string? vrchatId = null;

        if (query.Length > 0)
        {
            var matches = await _lookup.FindAsync(query, ct).ConfigureAwait(false);

            if (matches.Count == 0)
            {
                return (DiscordReply.Say(
                    $"Nobody in Modbot's records matches \"{ModerationEventEmbed.Fit(ModerationEventEmbed.Escape(query), 80)}\". "
                    + "Modbot only knows people it has seen in the group's history."), null, null);
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

                return (DiscordReply.Say(sb.ToString()), null, null);
            }

            vrchatId = matches[0].UserId;
        }

        var summary = await _lookup
            .SummarizeAsync(vrchatId, discordId.Length > 0 ? discordId : null, sight, ct)
            .ConfigureAwait(false);

        if (summary is null)
            return (DiscordReply.Say(NoDiscordRecordsMessage), null, null);

        var pictures = _pictures.ForMessage(showPictures);
        var picture = await PictureAsync(summary, pictures, ct).ConfigureAwait(false);

        return (
            DiscordReply.Card(ProfileCard(summary, style, picture), pictures.Files),
            summary.UserId,
            summary.Discord?.UserId);
    }

    /// <summary>
    /// The pictures on a <c>/lookup</c> card. Three on one card, which is what the slots are for:
    /// the face beside the name, the banner across the bottom, and the group they represent above
    /// the lot. Somebody known only on Discord has Discord's own picture, which Discord loads itself.
    /// </summary>
    /// <remarks>Shared with the right-click lookup, so both draw the same card.</remarks>
    public static async Task<CardPicture> PictureAsync(PersonSummary summary, CardPictureMessage pictures, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(pictures);

        if (summary.UserId is null)
            return new CardPicture(Thumbnail: summary.Discord?.AvatarUrl);

        var profile = summary.Profile;

        return new CardPicture(
            Thumbnail: await pictures.AddAsync(ProfilePictures.Best(profile), ct).ConfigureAwait(false),
            Image: await pictures.AddAsync(profile?.BannerUrl, ct).ConfigureAwait(false),
            AuthorIcon: await pictures.AddAsync(profile?.RepresentedGroupIconUrl, ct).ConfigureAwait(false));
    }

    /// <summary>What <c>/lookup</c> says about a Discord member Modbot has nothing on and no link for.</summary>
    public const string NoDiscordRecordsMessage = "Modbot has no records of that Discord member.";

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
    /// <strong>Both accounts, one card.</strong> The Discord account they linked sits in a field of
    /// its own, and the history under it is both accounts' together: Discord bans, kicks and
    /// timeouts beside the group's, the flags Modbot's rules raised, and -- for a caller allowed to
    /// read them -- notes and join requests. Somebody known only on Discord gets the same card,
    /// headed by their Discord name and picture.
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
        var discord = summary.Discord;
        var onVRChat = summary.UserId is not null;

        var title = !string.IsNullOrWhiteSpace(profile?.DisplayName) ? profile!.DisplayName!
            : summary.UserId
              ?? (string.IsNullOrWhiteSpace(discord?.Name) ? discord?.UserId : discord!.Name)
              ?? string.Empty;

        var description = onVRChat && profile is null
            ? "Modbot has seen this id in the group's history but has not read the profile yet."
            : null;

        var fields = new List<DiscordEmbedField>();

        if (onVRChat)
        {
            var eighteenPlus = profile is { Is18PlusVerified: true }
                ? "Yes"
                  + (profile.Is18PlusVerifiedAt is { } since ? $", since {DiscordTime.Day(since)}" : string.Empty)
                  + (profile.Is18PlusVerifiedSource == AgeVerificationSource.Manual ? " (marked by a moderator)" : string.Empty)
                : "Not seen as verified";

            var refreshed = profile?.LastRefreshedAt is { } at
                ? DiscordTime.Relative(at)
                : "Never";

            fields.Add(new DiscordEmbedField("18+ verified", eighteenPlus, Inline: true));
            fields.Add(new DiscordEmbedField("Profile last refreshed", refreshed, Inline: true));
            fields.Add(new DiscordEmbedField(
                "Discord",
                discord is null
                    ? "Not linked"
                    : CardLink.DiscordPerson(discord.Name, discord.UserId, style.PublicAddress)
                      + (discord.InServer ? string.Empty : " (not in the server)"),
                Inline: true));
        }
        else
        {
            fields.Add(new DiscordEmbedField("VRChat", "Not linked", Inline: true));

            if (discord is { InServer: false })
                fields.Add(new DiscordEmbedField("Discord", "Not in the server", Inline: true));
        }

        fields.Add(new DiscordEmbedField("Ban status", BanStatus(summary)));

        if (onVRChat)
        {
            fields.Add(new DiscordEmbedField(
                "Bans · kicks · warns", $"{summary.Bans} · {summary.Kicks} · {summary.Warns}", Inline: true));
        }

        if (discord is not null)
        {
            fields.Add(new DiscordEmbedField(
                "Discord bans · kicks · timeouts",
                $"{summary.DiscordBans} · {summary.DiscordKicks} · {summary.Timeouts}",
                Inline: true));
        }

        fields.Add(new DiscordEmbedField("Flags", Number(summary.Flags), Inline: true));

        if (summary.Notes is { } notes)
            fields.Add(new DiscordEmbedField("Notes", Number(notes), Inline: true));

        if (summary.JoinRequests is { } requests)
            fields.Add(new DiscordEmbedField("Join requests", Number(requests), Inline: true));

        var recent = summary.Recent.Count == 0
            ? "None recorded"
            : WholeLines(summary.Recent.Select(e => Line(e, style)), 1024);

        fields.Add(new DiscordEmbedField("Recent moderation events", recent));

        var banned = summary.IsBanned || discord is { Banned: true };

        return new DiscordEmbedContent(
            CardText.Plain(title, 256),
            description,
            banned ? CardColour.Red : Violet,
            fields,
            null,
            onVRChat
                ? CardLink.UrlFor(CardSubject.Person, summary.UserId!, style.PublicAddress)
                : CardLink.UrlFor(CardSubject.DiscordPerson, discord!.UserId, style.PublicAddress),
            style.GroupFooter,
            ThumbnailUrl: picture.Thumbnail,
            ImageUrl: picture.Image,
            FooterIconUrl: style.FooterIconUrl,
            AuthorName: profile?.RepresentedGroupName is { Length: > 0 } group
                ? CardText.Plain(group, 256)
                : null,
            AuthorIconUrl: picture.AuthorIcon);
    }

    /// <summary>
    /// Whether they are banned, one line per account: the group's as the Bans page reads it, then
    /// Discord's with the reason Discord holds, or a timeout still running.
    /// </summary>
    private static string BanStatus(PersonSummary summary)
    {
        var lines = new List<string>(2);

        if (summary.UserId is not null)
        {
            lines.Add(summary.IsBanned
                ? $"Banned {DiscordTime.Relative(summary.LastBannedAt!.Value)}"
                : summary.LastUnbannedAt is { } unbanned
                    ? $"Not banned (unbanned {DiscordTime.Relative(unbanned)})"
                    : "Not banned");
        }

        if (summary.Discord is { } discord)
        {
            if (discord.Banned)
            {
                var line = discord.BannedAt is { } at
                    ? $"Banned on Discord {DiscordTime.Relative(at)}"
                    : "Banned on Discord";

                if (!string.IsNullOrWhiteSpace(discord.BanReason))
                    line += ": " + CardText.Fit(CardText.EscapeText(discord.BanReason.Trim()), 300);

                lines.Add(line);
            }
            else if (discord.TimedOutUntil is { } until)
            {
                lines.Add($"Timed out on Discord until {DiscordTime.Absolute(until)} ({DiscordTime.Relative(until)})");
            }
            else
            {
                lines.Add("Not banned on Discord");
            }
        }

        return CardText.Fit(string.Join('\n', lines), 1024);
    }

    private static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private async Task<DiscordReply> RecentAsync(DiscordCommandCall call, CancellationToken ct)
    {
        var count = DiscordCommands.RecentDefault;
        if (int.TryParse(call.Option(DiscordCommands.RecentCountOption), NumberStyles.Integer, CultureInfo.InvariantCulture, out var asked))
            count = Math.Clamp(asked, 1, DiscordCommands.RecentMax);

        var events = await _lookup.RecentAsync(count, ct).ConfigureAwait(false);

        if (events.Count == 0)
            return DiscordReply.Say("No moderation events are recorded yet.");

        var (style, _) = await StyleAsync(ct).ConfigureAwait(false);
        var description = WholeLines(events.Select(e => Line(e, style)), 4096);

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
                $"Modbot's Discord bot is connected (since {DiscordTime.Relative(since)}) with {snapshot.CommandsRegistered} commands registered.",
            DiscordBotState.Connected => $"Modbot's Discord bot is connected with {snapshot.CommandsRegistered} commands registered.",
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
    /// <para>
    /// The same rule as a card. A list of twenty of these used to carry forty ids, which made the
    /// reply four times as tall for nothing a moderator was going to read.
    /// </para>
    /// <para>
    /// A Discord account is linked to its Discord view and a Modbot account to the person behind
    /// it, the same as on a card. The words that say why -- the reason a Discord moderator typed,
    /// a flag's reason, a note's own words -- follow, cut short, because the line is the only
    /// place in a list they can go.
    /// </para>
    /// </remarks>
    private static string Line(ModerationEventView e, CardStyle style)
    {
        var line = $"{DiscordTime.Absolute(e.OccurredAt)} **{ModerationEventEmbed.LabelFor(e.Type)}** — "
            + CardLink.Who(e.SubjectPlatform, e.SubjectName, e.SubjectId, style.PublicAddress);

        if (e.ActorId is not null)
            line += " by " + CardLink.Who(e.ActorPlatform, e.ActorName, e.ActorId, style.PublicAddress);

        var why = e.Type == FactType.NoteAdded ? e.What.Text : e.What.Own.Reason;

        if (!string.IsNullOrWhiteSpace(why))
            line += ": “" + CardText.Fit(CardText.EscapeText(why.Trim()), LineWords) + "”";

        return line;
    }

    /// <summary>The most of somebody's words one line in a list shows.</summary>
    private const int LineWords = 80;

    /// <summary>
    /// As many whole lines as fit, then an ellipsis line when some were left off.
    /// </summary>
    /// <remarks>
    /// Every line carries links, and a list cut at a character count can stop inside one, which
    /// puts half an address on screen. Lines are kept or left whole instead. A first line too long
    /// for the space on its own -- none Modbot writes comes close -- is cut as before.
    /// </remarks>
    public static string WholeLines(IEnumerable<string> lines, int max)
    {
        var all = lines.ToList();
        var text = new StringBuilder();
        const string More = "\n…";

        for (var i = 0; i < all.Count; i++)
        {
            var line = all[i];
            var needed = (text.Length == 0 ? 0 : 1) + line.Length;

            // Room is kept for the ellipsis line only while there are lines after this one.
            var room = i == all.Count - 1 ? max : max - More.Length;

            if (text.Length + needed > room)
            {
                if (text.Length == 0)
                    return CardText.Fit(line, max);

                return text.Append(More).ToString();
            }

            if (text.Length > 0)
                text.Append('\n');

            text.Append(line);
        }

        return text.ToString();
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

    /// <param name="target">The VRChat account a command was about.</param>
    /// <param name="discordTarget">The Discord account a command was about.</param>
    private async Task RecordAsync(
        DiscordCommandCall call, ModbotUser? user, string outcome, string? target, CancellationToken ct, string? discordTarget = null)
    {
        var data = new JsonObject
        {
            ["command"] = call.CommandName,
            ["outcome"] = outcome,
        };

        if (target is not null)
            data["target"] = target;

        if (discordTarget is not null)
            data["targetDiscord"] = discordTarget;

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
