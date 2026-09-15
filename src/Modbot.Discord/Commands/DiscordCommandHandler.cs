using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Discord.Bot;
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
    public const string NotLinkedMessage = "Link your Discord account in Modbot first.";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly IModbotClock _clock;
    private readonly DiscordBotStatus _status;
    private readonly LookupQuery _lookup;

    public DiscordCommandHandler(
        ModbotContext db,
        IFactWriter facts,
        IModbotClock clock,
        DiscordBotStatus status,
        LookupQuery lookup)
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
    }

    public async Task<DiscordReply> HandleAsync(DiscordCommandCall call, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

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

            foreach (var match in matches)
                sb.Append('\n').Append("• ").Append(ModerationEventEmbed.Person(match.DisplayName, match.UserId));

            return (DiscordReply.Say(sb.ToString()), null);
        }

        var person = matches[0];
        var summary = await _lookup.SummarizeAsync(person.UserId, ct).ConfigureAwait(false);
        var publicAddress = await PublicAddressAsync(ct).ConfigureAwait(false);

        return (DiscordReply.Card(ProfileCard(summary, publicAddress)), person.UserId);
    }

    /// <summary>The <c>/lookup</c> card. Public so its wording is testable without a database.</summary>
    public static DiscordEmbedContent ProfileCard(PersonSummary summary, string? publicAddress)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var profile = summary.Profile;
        var title = string.IsNullOrWhiteSpace(profile?.DisplayName)
            ? summary.UserId
            : ModerationEventEmbed.Escape(profile!.DisplayName!);

        var description = "`" + summary.UserId.Replace("`", string.Empty, StringComparison.Ordinal) + "`";
        if (profile is null)
            description += "\nModbot has seen this id in the group's history but has not fetched the profile yet.";

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
            : ModerationEventEmbed.Fit(string.Join('\n', summary.Recent.Select(Line)), 1024);

        return new DiscordEmbedContent(
            ModerationEventEmbed.Fit(title, 256),
            description,
            summary.IsBanned ? 0xC0392Bu : 0x5865F2u,
            [
                new DiscordEmbedField("18+ verified", eighteenPlus, Inline: true),
                new DiscordEmbedField("Profile last refreshed", refreshed, Inline: true),
                new DiscordEmbedField("Ban status", banStatus),
                new DiscordEmbedField("Bans · kicks · warns", $"{summary.Bans} · {summary.Kicks} · {summary.Warns}", Inline: true),
                new DiscordEmbedField("Recent moderation events", recent),
            ],
            null,
            PersonLink.For(publicAddress, summary.UserId),
            "From Modbot's stored records. Nothing was fetched from VRChat for this reply.");
    }

    private async Task<DiscordReply> RecentAsync(DiscordCommandCall call, CancellationToken ct)
    {
        var count = DiscordCommands.RecentDefault;
        if (int.TryParse(call.Option(DiscordCommands.RecentCountOption), NumberStyles.Integer, CultureInfo.InvariantCulture, out var asked))
            count = Math.Clamp(asked, 1, DiscordCommands.RecentMax);

        var events = await _lookup.RecentAsync(count, ct).ConfigureAwait(false);

        if (events.Count == 0)
            return DiscordReply.Say("No moderation events are recorded yet.");

        var description = ModerationEventEmbed.Fit(string.Join('\n', events.Select(Line)), 4096);

        return DiscordReply.Card(new DiscordEmbedContent(
            events.Count == 1 ? "The latest moderation event" : $"The latest {events.Count} moderation events",
            description,
            0x5865F2,
            [],
            null,
            null,
            "From Modbot's audit log. Newest first."));
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

    private static string Line(ModerationEventView e)
    {
        var line = $"{DiscordTime.Absolute(e.OccurredAt)} **{ModerationEventEmbed.LabelFor(e.Type)}** — {ModerationEventEmbed.Person(e.SubjectName, e.SubjectId)}";

        if (e.ActorId is not null)
            line += $" by {ModerationEventEmbed.Person(e.ActorName, e.ActorId)}";

        return line;
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
