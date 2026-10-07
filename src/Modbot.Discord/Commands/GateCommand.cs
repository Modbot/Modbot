using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Users;
using Modbot.Discord.Cards;
using Modbot.Discord.Gate;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Commands;

/// <summary>
/// <c>/gate</c>: the join gate from Discord (Discord commands design §3.7 and §4, step 4). Who is
/// waiting, let one in, hold new joiners, lift the hold.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The gate's own code.</strong> Let in, Hold and Lift hold are
/// <see cref="JoinGate.LetInAsync"/>, <see cref="JoinGate.HoldAsync"/> and
/// <see cref="JoinGate.LiftHoldAsync"/>, the ones the web app and the buttons under a join spike
/// alert call, so the facts, the lock and the limits are theirs. The caller is already known:
/// <see cref="DiscordCommandHandler"/> has found the Modbot account and checked Manage the join gate,
/// and writes the <c>modbot.discord.command</c> fact when this returns.
/// </para>
/// <para>
/// <strong>No confirmation before Hold</strong>, as on the alert's button; the reply carries
/// <strong>Lift hold</strong> (decision 12). Let in needs none either.
/// </para>
/// <para>
/// <strong><c>waiting</c> shows what the web app shows.</strong> The list names people at the gate,
/// which the web app shows only with See members, so <c>/gate waiting</c> asks for that as well. It
/// lists no linked VRChat account and no age check: those need See profiles on the web.
/// </para>
/// </remarks>
public sealed class GateCommand
{
    public const string NotOnMessage = "The join gate is not on.";
    public const string NobodyWaitingMessage = "Nobody is waiting at the join gate.";
    public const string PickOneMessage = "Pick a Discord member.";

    /// <summary>A button label Discord accepts: 80 characters, of which "Let in " takes eight.</summary>
    private const int NameInLabel = 40;

    private readonly ModbotContext _db;
    private readonly JoinGate _gate;

    public GateCommand(ModbotContext db, JoinGate gate)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(gate);

        _db = db;
        _gate = gate;
    }

    public async Task<StaffCommandAnswer> RunAsync(
        DiscordCommandCall call, ModbotUser user, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(user);

        return call.Subcommand switch
        {
            DiscordCommands.GateWaiting => await WaitingAsync(user, ct).ConfigureAwait(false),
            DiscordCommands.GateLetIn => await LetInAsync(call, user, gateway, ct).ConfigureAwait(false),
            DiscordCommands.GateHold => await HoldAsync(user, ct).ConfigureAwait(false),
            DiscordCommands.GateLift => await LiftAsync(user, ct).ConfigureAwait(false),
            _ => new StaffCommandAnswer(DiscordReply.Say("Pick waiting, let-in, hold or lift."), "invalid"),
        };
    }

    // ── waiting ──────────────────────────────────────────────────────────────────────────────

    private async Task<StaffCommandAnswer> WaitingAsync(ModbotUser user, CancellationToken ct)
    {
        // The web app shows who is at the gate only with See members.
        if (!DiscordCommands.Allows(user.EffectivePermissions, ModbotPermissions.ViewMembers))
        {
            return new StaffCommandAnswer(
                DiscordReply.Say(
                    $"You need the \"{DiscordCommands.Label(ModbotPermissions.ViewMembers)}\" permission in Modbot to use /gate {DiscordCommands.GateWaiting}."),
                "no-permission");
        }

        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.DiscordGuildId, s.DiscordGateMode })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (settings?.DiscordGateMode != DiscordGateModes.On || settings.DiscordGuildId is not { Length: > 0 } guild)
            return new StaffCommandAnswer(DiscordReply.Say(NotOnMessage), "answered");

        // The web app's list: everybody at the gate, oldest joiner first.
        var total = await _db.DiscordGateEntries.AsNoTracking()
            .CountAsync(e => e.GuildId == guild && e.ClosedAt == null, ct)
            .ConfigureAwait(false);

        var rows = await _db.DiscordGateEntries.AsNoTracking()
            .Where(e => e.GuildId == guild && e.ClosedAt == null)
            .OrderBy(e => e.JoinedAt)
            .Take(DiscordCommands.GateWaitingMost)
            .Select(e => new
            {
                e.DiscordUserId,
                e.DiscordUsername,
                e.JoinedAt,
                e.AgreedAt,
                DisplayName = _db.DiscordMembers
                    .Where(m => m.GuildId == e.GuildId && m.UserId == e.DiscordUserId)
                    .Select(m => m.DisplayName)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return new StaffCommandAnswer(DiscordReply.Say(NobodyWaitingMessage), "answered");

        var text = new StringBuilder();
        var buttons = new List<DiscordActionButton>();

        foreach (var row in rows)
        {
            var name = string.IsNullOrWhiteSpace(row.DisplayName) ? row.DiscordUsername : row.DisplayName;

            if (text.Length > 0)
                text.Append('\n');

            text.Append("**").Append(CardText.EscapeName(CardText.Plain(name, 80))).Append("**");

            if (!string.Equals(name, row.DiscordUsername, StringComparison.Ordinal) && row.DiscordUsername.Length > 0)
                text.Append(" (").Append(CardText.EscapeName(CardText.Plain(row.DiscordUsername, 40))).Append(')');

            text.Append(" · joined ").Append(DiscordTime.Relative(row.JoinedAt));

            if (row.AgreedAt is not null)
                text.Append(" · agreed");

            buttons.Add(new DiscordActionButton(
                $"{JoinGateButtons.LetInLabel} {CardText.Plain(name, NameInLabel)}",
                JoinGateButtons.LetInFor(row.DiscordUserId)));
        }

        var left = total - rows.Count;

        if (left > 0)
            text.Append("\nand ").Append(left.ToString(CultureInfo.InvariantCulture)).Append(" more");

        return new StaffCommandAnswer(new DiscordReply(text.ToString(), [], Actions: buttons), "answered");
    }

    // ── let-in, hold, lift ───────────────────────────────────────────────────────────────────

    private async Task<StaffCommandAnswer> LetInAsync(
        DiscordCommandCall call, ModbotUser user, IDiscordGateway? gateway, CancellationToken ct)
    {
        var member = call.Option(DiscordCommands.MemberOption)?.Trim() ?? string.Empty;

        if (member.Length == 0)
            return new StaffCommandAnswer(DiscordReply.Say(PickOneMessage), "invalid");

        var outcome = gateway is null
            ? JoinGateOutcome.Offline
            : await _gate.LetInAsync(gateway, member, user.Id, ct).ConfigureAwait(false);

        return outcome.Done
            ? new StaffCommandAnswer(JoinGate.LetInReply(member), "answered", DiscordTarget: member)
            : new StaffCommandAnswer(DiscordReply.Say(outcome.Error ?? "That did not work."), "refused", DiscordTarget: member);
    }

    private async Task<StaffCommandAnswer> HoldAsync(ModbotUser user, CancellationToken ct)
    {
        var outcome = await _gate.HoldAsync(user.Id, ct).ConfigureAwait(false);

        return outcome.Done
            ? new StaffCommandAnswer(await _gate.HeldReplyAsync(ct).ConfigureAwait(false), "answered")
            : new StaffCommandAnswer(DiscordReply.Say(outcome.Error ?? "That did not work."), "refused");
    }

    private async Task<StaffCommandAnswer> LiftAsync(ModbotUser user, CancellationToken ct)
    {
        var outcome = await _gate.LiftHoldAsync(user.Id, ct).ConfigureAwait(false);

        return outcome.Done
            ? new StaffCommandAnswer(DiscordReply.Say(JoinGate.HoldLifted), "answered")
            : new StaffCommandAnswer(DiscordReply.Say(outcome.Error ?? "That did not work."), "refused");
    }
}
