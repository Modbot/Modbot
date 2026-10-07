using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Core.Users;
using Modbot.Discord.Cards;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;
using Serilog;

namespace Modbot.Discord.Interactions;

/// <summary>
/// The staff side of the bot's right-click menus, card buttons and forms: who is pressing, whether
/// they may, and the form, confirmation or answer that follows (acting from Discord design).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Who is acting, every time.</strong> Each step resolves the presser through
/// <see cref="StaffDiscord"/> and checks the web app's permission for that step: the press that
/// opens a form, the form, and the confirmation. A role taken away between the press and the
/// confirmation is a refusal, not a ban.
/// </para>
/// <para>
/// <strong>The work is the web app's.</strong> Notes and actions go through <see cref="IStaffActions"/>,
/// which the API implements with the same services its endpoints use, so the facts, the case file
/// and the linked Discord ban are the ones a press in the web app makes.
/// </para>
/// <para>
/// Every handled step that finishes or is refused writes a <c>modbot.discord.command</c> fact, the
/// same record a slash command leaves, so the audit log says what came through Discord. A form
/// being opened is not a finished step and is not recorded.
/// </para>
/// <para>
/// Every answer is visible only to the person who pressed, except the line a card gets once an
/// action started from it is done (§6).
/// </para>
/// </remarks>
public sealed class StaffInteractionHandler
{
    public const string TooLateMessage = "That took too long. Try again.";
    public const string RunOutMessage = "That has run out. Press the button on the card again.";
    public const string NotSetUpMessage = "This Modbot is not set up to act from Discord.";
    public const string OwnAccountMessage = "That is Modbot's own account.";
    public const string TooManyMessage = "Too many things are waiting to be confirmed. Try again in a few minutes.";
    public const string NeedsVRChatMessage = "Link your VRChat account in Modbot first.";
    public const string NotYoursMessage = "Only the person who started this can confirm it.";
    public const string CancelledMessage = "Cancelled. Nothing was sent.";
    public const string NoteAddedMessage = "Note added.";
    public const string EmptyNoteMessage = "A note needs something in it.";
    public const string NoReasonsMessage = "Modbot's reason list has none for this. Add one in Modbot first.";
    public const string UnknownReasonMessage = "One of those reasons is not on the list.";
    public const string FailedMessage = "Something went wrong on Modbot's side. Check the person in Modbot before pressing again.";
    public const string NoLinkedVRChatMessage = "They are not linked to a VRChat account.";
    public const string NoLinkedDiscordMessage = "They have no linked Discord account.";
    public const string BanNeedsAReasonMessage = "A ban needs a reason.";
    public const string PickDaysMessage = "Pick how many days of their messages to delete.";

    /// <summary>The longest note, as the note service allows.</summary>
    public const int NoteLength = 2000;

    /// <summary>The longest note on an action, as the moderation service allows and a Discord text box takes.</summary>
    public const int ActionNoteLength = 4000;

    /// <summary>A Discord list offers at most this many.</summary>
    public const int MaxReasonsOffered = 25;

    public const string NoteField = "note";
    public const string ReasonsField = "reasons";

    /// <summary>The reason on the form for an action on the Discord server alone: free words, not the group's list.</summary>
    public const string WhyField = "why";

    /// <summary>"Delete their messages from" on a Discord ban's form: 0, 1 or 7 (days).</summary>
    public const string DeleteDaysField = "deletedays";

    /// <summary>
    /// The longest reason on the Discord server. Discord keeps 512 characters in its audit log, and
    /// "Modbot: banned by &lt;name&gt;: " goes in front of the reason.
    /// </summary>
    public const int DiscordWhyLength = 400;

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly IModbotClock _clock;
    private readonly LookupQuery _lookup;
    private readonly PendingStaffActions _pending;
    private readonly CardPictures _pictures;
    private readonly IStaffActions? _staff;
    private readonly StaffCommands _people;
    private readonly ILogger _log;

    public StaffInteractionHandler(
        ModbotContext db,
        IFactWriter facts,
        IModbotClock clock,
        LookupQuery lookup,
        PendingStaffActions pending,
        CardPictures? pictures = null,
        IStaffActions? staff = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(pending);

        _db = db;
        _facts = facts;
        _clock = clock;
        _lookup = lookup;
        _pending = pending;
        _pictures = pictures ?? new CardPictures();
        _staff = staff;

        // The one rule for naming a person on a command (/note, /watch, /ban, /kick), not a copy.
        _people = new StaffCommands(db, clock, lookup, staff);
        _log = Log.Logger.ForContext(LogArea.Name, LogArea.Discord);
    }

    // ── Right-click menus ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One of the right-click menus. Null means the answer was a form, already shown; anything else
    /// is the reply to send.
    /// </summary>
    /// <param name="gateway">The session, for the bot's own account id.</param>
    public async Task<DiscordReply?> HandleMenuAsync(DiscordCommandCall call, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (StaffMenus.Requires(call.CommandName) is not { } required)
            return DiscordReply.Say("Modbot does not know that command.");

        var target = call.TargetUser?.Id;
        var writes = call.CommandName != StaffMenus.LookUp;

        // A menu the operator switched off is no longer registered; this is for a run that came
        // before Discord dropped it (Discord commands design §3.8).
        if (!await CommandSwitchSetting.IsOnAsync(_db, call.CommandName, ct).ConfigureAwait(false))
        {
            await RecordAsync(call.DiscordUserId, null, call.CommandName, "off", target, ct).ConfigureAwait(false);
            return DiscordReply.Say(CommandSwitchSetting.OffMessage(call.CommandName, menu: true));
        }

        var (user, refusal, outcome) = await StaffAsync(call.DiscordUserId, required, writes, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            await RecordAsync(call.DiscordUserId, user, call.CommandName, outcome, target, ct).ConfigureAwait(false);
            return refusal;
        }

        return call.CommandName switch
        {
            StaffMenus.LookUp => await LookUpAsync(call, user!, gateway, ct).ConfigureAwait(false),
            _ => await OpenNoteFromMenuAsync(call, user!, gateway, ct).ConfigureAwait(false),
        };
    }

    private async Task<DiscordReply?> LookUpAsync(DiscordCommandCall call, ModbotUser user, IDiscordGateway? gateway, CancellationToken ct)
    {
        if (call.TargetUser is not { } target)
            return DiscordReply.Say("Discord did not say who that was.");

        if (IsBot(target.Id, gateway))
        {
            await RecordAsync(call.DiscordUserId, user, call.CommandName, "own-account", target.Id, ct).ConfigureAwait(false);
            return DiscordReply.Say(OwnAccountMessage);
        }

        var (style, showPictures) = await StyleAsync(ct).ConfigureAwait(false);
        bool Allows(ModbotPermissions p) => user.IsVRChatLinked && DiscordCommands.Allows(user.EffectivePermissions, p);

        // The whole person, exactly as /lookup discord:<member> shows them, with what the caller's
        // permissions let /lookup show (LookupSight): it follows the member's proved link to VRChat
        // itself, and notes and join requests appear only for callers the web app shows them to.
        var summary = await _lookup
            .SummarizeAsync(null, target.Id, LookupSight.Of(user.EffectivePermissions), ct)
            .ConfigureAwait(false);

        DiscordReply reply;

        if (summary is null)
        {
            // Nothing on record and no link: /lookup's own answer, with the note button so the first
            // note can still be written from here.
            reply = new DiscordReply(
                DiscordCommandHandler.NoDiscordRecordsMessage,
                [],
                Actions: CardButtons.ForLookup(onDiscord: true, target.Id, banned: false, Allows));
        }
        else
        {
            var pictures = _pictures.ForMessage(showPictures);
            var picture = await DiscordCommandHandler.PictureAsync(summary, pictures, ct).ConfigureAwait(false);

            // Linked: the buttons act on their VRChat account, as on a card about them; otherwise
            // only a note about the Discord account.
            var buttons = summary.UserId is { Length: > 0 } vrchatId
                ? CardButtons.ForLookup(onDiscord: false, vrchatId, summary.IsBanned, Allows)
                : CardButtons.ForLookup(onDiscord: true, target.Id, banned: false, Allows);

            reply = new DiscordReply(
                null,
                [DiscordCommandHandler.ProfileCard(summary, style, picture)],
                null,
                pictures.Files,
                buttons);
        }

        await RecordAsync(call.DiscordUserId, user, call.CommandName, "answered", target.Id, ct).ConfigureAwait(false);
        return reply;
    }

    private async Task<DiscordReply?> OpenNoteFromMenuAsync(DiscordCommandCall call, ModbotUser user, IDiscordGateway? gateway, CancellationToken ct)
    {
        if (call.TargetUser is not { } target)
            return DiscordReply.Say("Discord did not say who that was.");

        if (IsBot(target.Id, gateway))
        {
            await RecordAsync(call.DiscordUserId, user, call.CommandName, "own-account", target.Id, ct).ConfigureAwait(false);
            return DiscordReply.Say(OwnAccountMessage);
        }

        var formId = StaffMenus.NoteFormFor(onDiscord: true, target.Id);
        if (!StaffMenus.Fits(formId))
            return DiscordReply.Say("That id is too long for a Discord form.");

        return await ShowAsync(call.ShowFormAsync, NoteForm(formId), ct).ConfigureAwait(false);
    }

    // ── /ban and /kick ───────────────────────────────────────────────────────────────────────

    /// <summary>Whether this slash command is one of the two that open a form and wait for a confirmation.</summary>
    public static bool HandlesCommand(string commandName) => commandName is DiscordCommands.Ban or DiscordCommands.Kick;

    /// <summary>
    /// <c>/ban</c> or <c>/kick</c>: who is acting, who it is about, where it acts, and then the
    /// reasons form (Discord commands design §3.3, acting from Discord design §4). Null means the
    /// form was shown; anything else is the reply to send.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>What <c>/ban</c> means is the web app's.</strong> A VRChat person, or a Discord member
    /// linked to VRChat, is banned from the group, and Modbot's own rule bans their linked Discord
    /// account with them. A Discord member with no link is banned from the Discord server, which is
    /// a different permission ("Ban on Discord").
    /// </para>
    /// <para>
    /// <strong><c>/kick</c> goes by who is named</strong>: a Discord member is removed from the
    /// server, a VRChat person from the group. <c>from</c> picks the other place, or both, for
    /// somebody who has both accounts linked.
    /// </para>
    /// <para>
    /// <strong>Nothing is searched or named for a caller without See profiles</strong>
    /// (<see cref="StaffCommands.PersonAsync"/>): the VRChat option takes an exact id, every refusal
    /// is one sentence, and the confirmation shows the id they typed, never a VRChat name. A Discord
    /// member is named by their Discord name, which Discord itself showed in the picker.
    /// </para>
    /// </remarks>
    public async Task<DiscordReply?> HandleCommandAsync(DiscordCommandCall call, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (!HandlesCommand(call.CommandName))
            return DiscordReply.Say("Modbot does not know that command.");

        var action = call.CommandName == DiscordCommands.Ban ? StaffActionWords.Ban : StaffActionWords.Kick;

        // A command the operator switched off is no longer registered; this is for a run that came
        // before Discord dropped it (Discord commands design §3.8).
        if (!await CommandSwitchSetting.IsOnAsync(_db, call.CommandName, ct).ConfigureAwait(false))
        {
            await RecordAsync(call.DiscordUserId, null, call.CommandName, "off", null, ct).ConfigureAwait(false);
            return DiscordReply.Say(CommandSwitchSetting.OffMessage(call.CommandName, menu: false));
        }

        // An account, enabled, a VRChat link: the order design §3 gives for anything that writes.
        var (user, refusal, outcome) = await StaffAsync(call.DiscordUserId, ModbotPermissions.None, writes: true, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            await RecordAsync(call.DiscordUserId, user, call.CommandName, outcome, null, ct).ConfigureAwait(false);
            return refusal;
        }

        // Then the permission, for either place. Which one a run needs depends on who it is about,
        // and is checked once that is known.
        if (!DiscordCommands.CanUse(call.CommandName, user!.EffectivePermissions))
        {
            await RecordAsync(call.DiscordUserId, user, call.CommandName, "no-permission", null, ct).ConfigureAwait(false);
            return DiscordReply.Say(NeedsPermissionMessage(DiscordCommands.Requires(call.CommandName)!.Value, call.CommandName));
        }

        if (_staff is null)
            return DiscordReply.Say(NotSetUpMessage);

        var (person, problem) = await _people.PersonAsync(call, StaffCommands.SeesNames(user), ct).ConfigureAwait(false);
        if (problem is not null)
        {
            await RecordAsync(call.DiscordUserId, user, call.CommandName, problem.Outcome, null, ct).ConfigureAwait(false);
            return problem.Reply;
        }

        var (pending, no, noOutcome, noTarget, noDiscordTarget) = await PlanAsync(call, action, person!, user, gateway, ct).ConfigureAwait(false);
        if (no is not null)
        {
            await RecordAsync(call.DiscordUserId, user, call.CommandName, noOutcome, noTarget, ct, noDiscordTarget).ConfigureAwait(false);
            return no;
        }

        return await OpenFormAsync(call.ShowFormAsync, pending!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Where the command acts and on whom, checked against what the caller may do there, as a
    /// pending action ready for its form, or the reply that refuses it and what to record.
    /// </summary>
    private async Task<(PendingStaffAction? Pending, DiscordReply? Refusal, string Outcome, string? Target, string? DiscordTarget)> PlanAsync(
        DiscordCommandCall call, string action, StaffCommands.Person person, ModbotUser user, IDiscordGateway? gateway, CancellationToken ct)
    {
        var onDiscordFirst = person.Platform == FactPlatform.Discord;
        string? vrchatId;
        string? discordId;
        string name;

        if (onDiscordFirst)
        {
            discordId = person.Id;

            if (IsBot(discordId, gateway))
                return (null, DiscordReply.Say(OwnAccountMessage), "own-account", null, discordId);

            vrchatId = await _db.LinkedVRChatUserIdAsync(discordId, ct).ConfigureAwait(false);
            name = await DiscordNameAsync(discordId, ct).ConfigureAwait(false) ?? discordId;
        }
        else
        {
            vrchatId = person.Id;
            discordId = await _db.LinkedDiscordUserIdAsync(vrchatId, ct).ConfigureAwait(false);

            // Only what the caller may see: Modbot's name for them when they hold See profiles,
            // otherwise the id they typed.
            name = person.Name is { Length: > 0 } known ? known : vrchatId;
        }

        StaffActionWhere where;

        if (action == StaffActionWords.Ban)
        {
            // The web app's meaning: the group, with the linked Discord account banned beside it by
            // the moderation service; the Discord server alone only for somebody with no VRChat link.
            where = vrchatId is not null ? StaffActionWhere.VRChat : StaffActionWhere.Discord;
        }
        else
        {
            where = call.Option(DiscordCommands.KickFromOption) switch
            {
                DiscordCommands.FromDiscord => StaffActionWhere.Discord,
                DiscordCommands.FromVRChat => StaffActionWhere.VRChat,
                DiscordCommands.FromBoth => StaffActionWhere.Both,
                _ => onDiscordFirst ? StaffActionWhere.Discord : StaffActionWhere.VRChat,
            };
        }

        // The permission for each place it acts, the web app's own for each. Asked before anything
        // about what the person has linked, so nobody is told about an account in a place they may
        // not act in.
        var held = user.EffectivePermissions;

        foreach (var needed in new[]
                 {
                     where.HasFlag(StaffActionWhere.VRChat) ? StaffActionWords.Requires(action) : ModbotPermissions.None,
                     where.HasFlag(StaffActionWhere.Discord) ? StaffActionWords.RequiresOnDiscord(action) : ModbotPermissions.None,
                 })
        {
            if (!DiscordCommands.Allows(held, needed))
            {
                return (null, DiscordReply.Say(NeedsPermissionMessage(needed, call.CommandName)), "no-permission", vrchatId, discordId);
            }
        }

        if (where.HasFlag(StaffActionWhere.VRChat) && vrchatId is null)
            return (null, DiscordReply.Say(NoLinkedVRChatMessage), "invalid", null, discordId);

        if (where.HasFlag(StaffActionWhere.Discord) && discordId is null)
            return (null, DiscordReply.Say(NoLinkedDiscordMessage), "invalid", vrchatId, null);

        var member = Member(user);

        if (where.HasFlag(StaffActionWhere.Discord)
            && await _staff!.DiscordCheckAsync(action, discordId!, member, ct).ConfigureAwait(false) is { } off)
        {
            return (null, DiscordReply.Say(off), "refused", vrchatId, discordId);
        }

        var pending = new PendingStaffAction(
            PendingStaffActions.NewToken(),
            call.DiscordUserId,
            action,
            where == StaffActionWhere.Discord ? discordId! : vrchatId!,
            name,
            null,
            null,
            _clock.UtcNow,
            [],
            [],
            string.Empty,
            Ready: false,
            where,
            where.HasFlag(StaffActionWhere.Discord) ? discordId : null,
            DeleteMessageDays: 0,
            AlsoBansDiscord: action == StaffActionWords.Ban && where == StaffActionWhere.VRChat && discordId is not null);

        return (pending, null, "answered", null, null);
    }

    private static string NeedsPermissionMessage(ModbotPermissions permission, string command)
        => $"You need the \"{DiscordCommands.Label(permission)}\" permission in Modbot to use /{command}.";

    /// <summary>The name the Discord server shows for a member, as Modbot last saw it; null when it never has.</summary>
    private async Task<string?> DiscordNameAsync(string discordUserId, CancellationToken ct)
    {
        var shown = await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.UserId == discordUserId)
            .Select(m => m.DisplayName)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(shown) ? null : shown.Trim();
    }

    // ── Buttons ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A press on a note, action or confirmation button. Null means it was answered already, with a
    /// form or by rewriting the confirmation; anything else is the reply to send.
    /// </summary>
    public async Task<DiscordReply?> HandleButtonAsync(DiscordButtonPress press, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        var id = press.ButtonId;

        if (StaffMenus.TryReadPerson(id, StaffMenus.NoteButton, out var onDiscord, out var personId))
            return await OpenNoteFromButtonAsync(press, onDiscord, personId, gateway, ct).ConfigureAwait(false);

        if (StaffMenus.TryReadAction(id, out var action, out var vrchatUserId))
            return await StartActionAsync(press, action, vrchatUserId, ct).ConfigureAwait(false);

        if (StaffMenus.TokenAfter(id, StaffMenus.YesButton) is { } yes)
            return await ConfirmAsync(press, yes, gateway, ct).ConfigureAwait(false);

        if (StaffMenus.TokenAfter(id, StaffMenus.NoButton) is { } no)
        {
            if (_pending.Action(no, _clock.UtcNow) is { } held && held.DiscordUserId != press.DiscordUserId)
                return DiscordReply.Say(NotYoursMessage);

            _pending.Forget(no);
            await press.UpdateAsync(DiscordReply.Say(CancelledMessage), ct).ConfigureAwait(false);
            return null;
        }

        return DiscordReply.Say("Modbot does not know that button.");
    }

    private async Task<DiscordReply?> OpenNoteFromButtonAsync(
        DiscordButtonPress press, bool onDiscord, string personId, IDiscordGateway? gateway, CancellationToken ct)
    {
        var (user, refusal, outcome) = await StaffAsync(press.DiscordUserId, ModbotPermissions.WriteNotes, writes: true, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            await RecordAsync(press.DiscordUserId, user, "note", outcome, personId, ct).ConfigureAwait(false);
            return refusal;
        }

        if (onDiscord && IsBot(personId, gateway))
            return DiscordReply.Say(OwnAccountMessage);

        var formId = StaffMenus.NoteFormFor(onDiscord, personId);
        if (!StaffMenus.Fits(formId))
            return DiscordReply.Say("That id is too long for a Discord form.");

        return await ShowAsync(press.ShowFormAsync, NoteForm(formId), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Kick, Ban, Approve or Reject under a card or a lookup: the reasons form, or for Approve the
    /// confirmation straight away.
    /// </summary>
    private async Task<DiscordReply?> StartActionAsync(DiscordButtonPress press, string action, string vrchatUserId, CancellationToken ct)
    {
        var (user, refusal, outcome) = await StaffAsync(press.DiscordUserId, StaffActionWords.Requires(action), writes: true, ct)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            await RecordAsync(press.DiscordUserId, user, action, outcome, vrchatUserId, ct).ConfigureAwait(false);
            return refusal;
        }

        if (_staff is null)
            return DiscordReply.Say(NotSetUpMessage);

        var names = await DisplayNames.LoadAsync(_db, [vrchatUserId], ct).ConfigureAwait(false);
        var name = names.GetValueOrDefault(vrchatUserId) is { Length: > 0 } known ? known : vrchatUserId;

        var now = _clock.UtcNow;
        var pending = new PendingStaffAction(
            PendingStaffActions.NewToken(),
            press.DiscordUserId,
            action,
            vrchatUserId,
            name,
            press.CardChannelId,
            press.CardMessageId,
            now,
            [],
            [],
            string.Empty,
            Ready: false);

        if (action == StaffActionWords.Approve)
        {
            // Nothing to pick, so nothing to fill in: straight to the confirmation, once the
            // service has said it would send it.
            if (await _staff.CheckAsync(action, vrchatUserId, [], string.Empty, Member(user!), ct).ConfigureAwait(false) is { } no)
            {
                await RecordAsync(press.DiscordUserId, user, action, "refused", vrchatUserId, ct).ConfigureAwait(false);
                return DiscordReply.Say(no);
            }

            pending = pending with { Ready = true };
            if (!_pending.TryAdd(pending, now))
                return DiscordReply.Say(TooManyMessage);

            return Confirmation(pending);
        }

        return await OpenFormAsync(press.ShowFormAsync, pending, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Holds the pending action and shows its form: the group's reasons and a note for an action on
    /// the group, or a free reason (and for a ban, how many days of messages to delete) for an action
    /// on the Discord server alone. Null means the form was shown.
    /// </summary>
    private async Task<DiscordReply?> OpenFormAsync(
        Func<DiscordForm, CancellationToken, Task> show, PendingStaffAction pending, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var fields = new List<DiscordFormField>();

        if (pending.OnVRChat)
        {
            var reasons = await _staff!.ReasonsAsync(pending.Action, ct).ConfigureAwait(false);
            if (reasons.Required && reasons.Reasons.Count == 0)
                return DiscordReply.Say(NoReasonsMessage);

            if (!_pending.TryAdd(pending, now))
                return DiscordReply.Say(TooManyMessage);

            if (reasons.Reasons.Count > 0)
            {
                var offered = reasons.Reasons.Take(MaxReasonsOffered).ToList();

                fields.Add(new DiscordFormField(
                    ReasonsField,
                    "Reasons",
                    DiscordFormFieldKind.Choice,
                    reasons.Required,
                    Choices: [.. offered.Select(r => new DiscordChoice(r.Label, r.Id.ToString("N"), r.Description))],
                    MaxChoices: offered.Count));
            }

            fields.Add(new DiscordFormField(NoteField, "Note", DiscordFormFieldKind.LongText, Required: false, MaxLength: ActionNoteLength));
        }
        else
        {
            if (!_pending.TryAdd(pending, now))
                return DiscordReply.Say(TooManyMessage);

            var banning = pending.Action == StaffActionWords.Ban;

            fields.Add(new DiscordFormField(WhyField, "Reason", DiscordFormFieldKind.LongText, Required: banning, MaxLength: DiscordWhyLength));

            if (banning)
            {
                fields.Add(new DiscordFormField(
                    DeleteDaysField,
                    "Delete their messages from",
                    DiscordFormFieldKind.Choice,
                    Required: false,
                    Choices: [new DiscordChoice("None", "0"), new DiscordChoice("1 day", "1"), new DiscordChoice("7 days", "7")],
                    MaxChoices: 1));
            }
        }

        var name = pending.SubjectName;
        var form = new DiscordForm(StaffActionWords.FormTitle(pending.Action, name), StaffMenus.ActForm + pending.Token, fields);

        var shown = await ShowAsync(show, form, ct).ConfigureAwait(false);
        if (shown is not null)
            _pending.Forget(pending.Token);

        return shown;
    }

    /// <summary>
    /// The confirmation's yes. Checks who is pressing again, rewrites the confirmation so it cannot
    /// be pressed twice by eye, and runs the action with the confirmation's own key, so a second
    /// press that gets through anyway is answered with the first result (§4).
    /// </summary>
    private async Task<DiscordReply?> ConfirmAsync(DiscordButtonPress press, string token, IDiscordGateway? gateway, CancellationToken ct)
    {
        if (_pending.Action(token, _clock.UtcNow) is not { Ready: true } pending)
        {
            await press.UpdateAsync(DiscordReply.Say(RunOutMessage), ct).ConfigureAwait(false);
            return null;
        }

        if (pending.DiscordUserId != press.DiscordUserId)
            return DiscordReply.Say(NotYoursMessage);

        // The confirmation is rewritten first, before anything that reads the database: that is
        // the press's first answer, well inside Discord's time, so its buttons are gone at once
        // and every later answer edits the same message. Left until after the checks, a slow
        // database would make the gateway acknowledge it as "thinking…" instead, and the answer
        // would arrive as a new message under a confirmation that still has its buttons.
        var name = CardText.EscapeName(pending.SubjectName);
        await press.UpdateAsync(DiscordReply.Say(StaffActionWords.Doing(pending, name)), ct).ConfigureAwait(false);

        var (target, discordTarget) = Targets(pending);

        var (user, refusal, outcome) = await StaffAsync(press.DiscordUserId, pending.Needs, writes: true, ct)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            await RecordAsync(press.DiscordUserId, user, pending.Action, outcome, target, ct, discordTarget).ConfigureAwait(false);
            await press.UpdateAsync(refusal, ct).ConfigureAwait(false);
            return null;
        }

        if (_staff is null)
        {
            await press.UpdateAsync(DiscordReply.Say(NotSetUpMessage), ct).ConfigureAwait(false);
            return null;
        }

        StaffLegAnswers legs;
        var again = false;

        try
        {
            if (pending.OnDiscord)
            {
                // The server has no key of its own to claim, so the confirmation is claimed here:
                // a second press gets the first answers and sends nothing.
                var staff = _staff;
                var member = Member(user!);
                var (answers, first) = await _pending
                    .RunOnceAsync(pending.Token, () => RunLegsAsync(staff, pending, member, ct))
                    .ConfigureAwait(false);

                (legs, again) = (answers, !first);
            }
            else
            {
                legs = new StaffLegAnswers(
                    await _staff.RunAsync(
                            pending.Action, pending.Key, pending.SubjectId, pending.ReasonIds, pending.Note, Member(user!), ct)
                        .ConfigureAwait(false),
                    null);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The confirmation already says "Banning…"; it must not stay that way. Whether VRChat
            // got the request is not known here, so the line sends the moderator to look rather
            // than guessing: starting again from the card makes a new key, which could act twice.
            _log.Error(e, "A {Action} from Discord failed", pending.Action);

            try
            {
                await press.UpdateAsync(DiscordReply.Say(FailedMessage), ct).ConfigureAwait(false);
            }
            catch (Exception updateError)
            {
                _log.Debug(updateError, "Could not rewrite the confirmation after a failure");
            }

            await RecordAsync(press.DiscordUserId, user, pending.Action, "error", target, ct, discordTarget).ConfigureAwait(false);
            return null;
        }

        var publicAddress = await PublicAddressAsync(ct).ConfigureAwait(false);
        await press.UpdateAsync(Answer(pending, legs, publicAddress), ct).ConfigureAwait(false);

        // The one thing the channel sees: who dealt with the card, once, and only when it is done.
        if (legs.VRChat is { Done: true, Repeat: false }
            && !pending.OnDiscord
            && pending is { CardChannelId: { } channelId, CardMessageId: { } messageId }
            && gateway is not null)
        {
            var line = StaffActionWords.Handled(pending.Action, CardText.EscapeName(user!.Username));
            var marked = await gateway.MarkHandledAsync(channelId, messageId, line, StaffMenus.ActButton, ct).ConfigureAwait(false);

            if (!marked.Sent)
                _log.Warning("Could not mark the card {Message} as handled: {Reason}", messageId, marked.Error);

            // Repeats are never folded into a card somebody acted on, which would bring back the
            // buttons it took off. The gateway refuses such a fold anyway; forgetting the card here
            // saves the next pass the read.
            try
            {
                await _db.DiscordEventChannels
                    .Where(c => c.RepeatPostId == messageId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.RepeatPostId, (string?)null), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warning(
                    e, "Could not stop repeats going into the card {Message}; the next fold into it will be refused instead",
                    messageId);
            }
        }

        var answered = new[] { legs.VRChat, legs.Discord }.Where(a => a is not null).Select(a => a!).ToList();

        var result = answered switch
        {
            _ when again || answered.Any(a => a.Repeat) => "repeat",
            _ when answered.All(a => a.Done) => "done",
            _ when answered.Any(a => a.Done) => "partial",
            _ when answered.Any(a => a.Refused) => "refused",
            _ => "failed",
        };

        await RecordAsync(press.DiscordUserId, user, pending.Action, result, target, ct, discordTarget).ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// Every place the action acts, group first. Each answers on its own: the server is not left
    /// alone because the group said no, and the group is not undone because the server did.
    /// </summary>
    private static async Task<StaffLegAnswers> RunLegsAsync(IStaffActions staff, PendingStaffAction pending, StaffMember member, CancellationToken ct)
    {
        StaffActionAnswer? vrchat = null;
        StaffActionAnswer? discord = null;

        if (pending.OnVRChat)
        {
            vrchat = await staff
                .RunAsync(pending.Action, pending.Key, pending.SubjectId, pending.ReasonIds, pending.Note, member, ct)
                .ConfigureAwait(false);
        }

        if (pending.OnDiscord)
        {
            var why = DiscordReason(pending);

            discord = pending.Action == StaffActionWords.Ban
                ? await staff.DiscordBanAsync(pending.TargetDiscordId!, why, pending.DeleteMessageDays, member, ct).ConfigureAwait(false)
                : await staff.DiscordKickAsync(pending.TargetDiscordId!, why, member, ct).ConfigureAwait(false);
        }

        return new StaffLegAnswers(vrchat, discord);
    }

    /// <summary>
    /// Why, for Discord's audit log: what was written on the server's form, or for an action on both
    /// places the reasons picked and the note, as the group's own record has them.
    /// </summary>
    private static string DiscordReason(PendingStaffAction pending)
    {
        var parts = new List<string>();

        if (pending.ReasonLabels.Count > 0)
            parts.Add(string.Join(", ", pending.ReasonLabels));

        if (pending.Note.Length > 0)
            parts.Add(pending.Note);

        return string.Join(": ", parts);
    }

    /// <summary>The access record's targets: the VRChat id and the Discord id the action was about.</summary>
    private static (string? Target, string? DiscordTarget) Targets(PendingStaffAction pending)
        => (pending.OnVRChat ? pending.SubjectId : null, pending.OnDiscord ? pending.TargetDiscordId : null);

    // ── Forms ────────────────────────────────────────────────────────────────────────────────

    /// <summary>A form was sent: a note, or the reasons for an action.</summary>
    public async Task<DiscordReply> HandleFormAsync(DiscordFormSubmit submit, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(submit);

        var id = submit.FormId;

        if (StaffMenus.TryReadPerson(id, StaffMenus.NoteForm, out var onDiscord, out var personId))
            return await WriteNoteAsync(submit, onDiscord, personId, gateway, ct).ConfigureAwait(false);

        if (StaffMenus.TokenAfter(id, StaffMenus.ActForm) is { } act)
            return await ReadReasonsAsync(submit, act, ct).ConfigureAwait(false);

        return DiscordReply.Say("Modbot does not know that form.");
    }

    private async Task<DiscordReply> WriteNoteAsync(
        DiscordFormSubmit submit, bool onDiscord, string personId, IDiscordGateway? gateway, CancellationToken ct)
    {
        var (user, refusal, outcome) = await StaffAsync(submit.DiscordUserId, ModbotPermissions.WriteNotes, writes: true, ct).ConfigureAwait(false);
        if (refusal is not null)
        {
            await RecordAsync(submit.DiscordUserId, user, "note", outcome, personId, ct).ConfigureAwait(false);
            return refusal;
        }

        if (onDiscord && IsBot(personId, gateway))
            return DiscordReply.Say(OwnAccountMessage);

        var text = submit.Text(NoteField);
        if (text.Length == 0)
            return DiscordReply.Say(EmptyNoteMessage);

        if (_staff is null)
            return DiscordReply.Say(NotSetUpMessage);

        var platform = onDiscord ? FactPlatform.Discord : FactPlatform.VRChat;
        var written = await _staff.WriteNoteAsync(platform, personId, text, Member(user!), ct).ConfigureAwait(false);

        await RecordAsync(submit.DiscordUserId, user, "note", written.Written ? "answered" : "refused", personId, ct).ConfigureAwait(false);

        return written.Written
            ? await NoteAddedAsync(onDiscord, personId, ct).ConfigureAwait(false)
            : DiscordReply.Say(written.Error ?? "The note could not be written.");
    }

    /// <summary>
    /// The first <paramref name="max"/> characters, one fewer when the cut would split an emoji or
    /// any other character written as two halves: half of one is not a character, and JSON and
    /// Postgres both refuse it.
    /// </summary>
    public static string Cut(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= max)
            return text;

        var end = max > 0 && char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..end];
    }

    /// <summary>The reasons form for an action was sent: check it all, then ask for the confirmation.</summary>
    private async Task<DiscordReply> ReadReasonsAsync(DiscordFormSubmit submit, string token, CancellationToken ct)
    {
        if (_pending.Action(token, _clock.UtcNow) is not { } pending)
            return DiscordReply.Say(RunOutMessage);

        if (pending.DiscordUserId != submit.DiscordUserId)
            return DiscordReply.Say(NotYoursMessage);

        var (target, discordTarget) = Targets(pending);

        var (user, refusal, outcome) = await StaffAsync(submit.DiscordUserId, pending.Needs, writes: true, ct)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            await RecordAsync(submit.DiscordUserId, user, pending.Action, outcome, target, ct, discordTarget).ConfigureAwait(false);
            return refusal;
        }

        if (_staff is null)
            return DiscordReply.Say(NotSetUpMessage);

        // An action on the Discord server alone has a free reason, not the group's list.
        if (!pending.OnVRChat)
            return await ReadDiscordFormAsync(submit, pending, user!, ct).ConfigureAwait(false);

        // No list on the form (the action has no reasons), or a list left empty, is no reasons
        // picked; the service then decides whether one was needed.
        var picked = new List<Guid>();
        foreach (var value in submit.Picked(ReasonsField))
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (!Guid.TryParseExact(value, "N", out var reasonId))
                return DiscordReply.Say(UnknownReasonMessage);

            picked.Add(reasonId);
        }

        var note = submit.Text(NoteField);

        // Every check the service makes before it sends, made now, so the confirmation is only ever
        // shown for something that would be sent.
        if (await _staff.CheckAsync(pending.Action, pending.SubjectId, picked, note, Member(user!), ct).ConfigureAwait(false) is { } no)
        {
            await RecordAsync(submit.DiscordUserId, user, pending.Action, "refused", target, ct, discordTarget).ConfigureAwait(false);
            return DiscordReply.Say(no);
        }

        // Both places: the server's own checks too (the bot, the owner, staff), before the confirmation.
        if (pending.OnDiscord
            && await _staff.DiscordCheckAsync(pending.Action, pending.TargetDiscordId!, Member(user!), ct).ConfigureAwait(false) is { } offLimits)
        {
            await RecordAsync(submit.DiscordUserId, user, pending.Action, "refused", target, ct, discordTarget).ConfigureAwait(false);
            return DiscordReply.Say(offLimits);
        }

        var reasons = await _staff.ReasonsAsync(pending.Action, ct).ConfigureAwait(false);
        var labels = picked
            .Select(id => reasons.Reasons.FirstOrDefault(r => r.Id == id)?.Label)
            .Where(label => label is not null)
            .Select(label => label!)
            .ToList();

        var ready = pending with { ReasonIds = picked, ReasonLabels = labels, Note = note, Ready = true };
        _pending.Update(ready);

        return Confirmation(ready);
    }

    /// <summary>
    /// The form for an action on the Discord server alone was sent: the reason, and for a ban how
    /// many days of messages to delete, then every check the server makes, then the confirmation.
    /// </summary>
    private async Task<DiscordReply> ReadDiscordFormAsync(
        DiscordFormSubmit submit, PendingStaffAction pending, ModbotUser user, CancellationToken ct)
    {
        var (target, discordTarget) = Targets(pending);
        var banning = pending.Action == StaffActionWords.Ban;
        var why = Cut(submit.Text(WhyField), DiscordWhyLength);

        if (banning && why.Length == 0)
            return DiscordReply.Say(BanNeedsAReasonMessage);

        var days = 0;
        if (banning && submit.Picked(DeleteDaysField).FirstOrDefault() is { } picked
            && (!int.TryParse(picked, NumberStyles.None, CultureInfo.InvariantCulture, out days) || days is not (0 or 1 or 7)))
        {
            return DiscordReply.Say(PickDaysMessage);
        }

        if (await _staff!.DiscordCheckAsync(pending.Action, pending.TargetDiscordId!, Member(user), ct).ConfigureAwait(false) is { } no)
        {
            await RecordAsync(submit.DiscordUserId, user, pending.Action, "refused", target, ct, discordTarget).ConfigureAwait(false);
            return DiscordReply.Say(no);
        }

        var ready = pending with { Note = why, DeleteMessageDays = days, Ready = true };
        _pending.Update(ready);

        return Confirmation(ready);
    }

    // ── Shapes ───────────────────────────────────────────────────────────────────────────────

    private static DiscordForm NoteForm(string formId)
        => new(
            "Add a note",
            formId,
            [new DiscordFormField(NoteField, "Note", DiscordFormFieldKind.LongText, Required: true, MaxLength: NoteLength)]);

    /// <summary>"Ban **name**?", what was picked and written, a coloured yes and a Cancel. Public for tests.</summary>
    public static DiscordReply Confirmation(PendingStaffAction pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        var sb = new StringBuilder(StaffActionWords.Question(pending, CardText.EscapeName(pending.SubjectName)));

        if (pending.ReasonLabels.Count > 0)
            sb.Append("\nReasons: ").Append(CardText.EscapeText(string.Join(", ", pending.ReasonLabels)));

        if (pending.Note.Length > 0)
            sb.Append(pending.OnVRChat ? "\nNote: " : "\nReason: ").Append(CardText.Fit(CardText.EscapeText(pending.Note), 1000));

        if (pending.OnDiscord && pending.Action == StaffActionWords.Ban && pending.DeleteMessageDays > 0)
        {
            sb.Append("\nTheir messages from the last ")
                .Append(pending.DeleteMessageDays == 1 ? "day" : pending.DeleteMessageDays.ToString(CultureInfo.InvariantCulture) + " days")
                .Append(" will be deleted.");
        }

        return new DiscordReply(
            sb.ToString(),
            [],
            null,
            null,
            [
                new DiscordActionButton(
                    StaffActionWords.Label(pending),
                    StaffMenus.YesButton + pending.Token,
                    StaffActionWords.Style(pending.Action)),
                new DiscordActionButton("Cancel", StaffMenus.NoButton + pending.Token),
            ]);
    }

    /// <summary>What the confirmation says once the service has answered. Public for tests.</summary>
    public static DiscordReply Answer(PendingStaffAction pending, StaffActionAnswer answer, string? publicAddress)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(answer);

        var name = CardText.EscapeName(pending.SubjectName);
        var sb = new StringBuilder();

        if (answer.Done)
        {
            sb.Append(StaffActionWords.Done(pending.Action, name));

            if (answer.DiscordDone)
                sb.Append("\nTheir linked Discord account was banned too.");
            else if (answer.DiscordError is { Length: > 0 } discord)
                sb.Append("\nDiscord did not ban their linked account: ").Append(CardText.EscapeText(discord));

            if (answer.CaseFileError is { Length: > 0 } caseFile)
                sb.Append('\n').Append(caseFile);
        }
        else if (answer.Refused || answer.Gone || answer.Repeat)
        {
            sb.Append(answer.Error ?? "Nothing was sent.");
        }
        else
        {
            sb.Append("VRChat refused: ").Append(CardText.EscapeText(answer.Error ?? "it did not say why."));
        }

        IReadOnlyList<DiscordLinkButton>? links =
            answer.CaseId is { } caseId && !string.IsNullOrWhiteSpace(publicAddress)
                ? [new DiscordLinkButton(CardButtons.OpenCaseLabel, $"{publicAddress.TrimEnd('/')}/cases/{caseId}")]
                : null;

        return new DiscordReply(sb.ToString(), [], links);
    }

    /// <summary>
    /// What the confirmation says once every place the action acted has answered. The group alone is
    /// <see cref="Answer(PendingStaffAction, StaffActionAnswer, string?)"/>; the server alone and both
    /// say what each did on its own line. Public for tests.
    /// </summary>
    public static DiscordReply Answer(PendingStaffAction pending, StaffLegAnswers legs, string? publicAddress)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(legs);

        if (legs.VRChat is { } vrchat && legs.Discord is null)
            return Answer(pending, vrchat, publicAddress);

        var name = CardText.EscapeName(pending.SubjectName);
        var lines = new List<string>();
        IReadOnlyList<DiscordLinkButton>? links = null;

        if (legs.VRChat is { } group)
        {
            var inGroup = Answer(pending, group, publicAddress);
            lines.Add(inGroup.Text ?? string.Empty);
            links = inGroup.Links;
        }

        if (legs.Discord is { } server)
        {
            lines.Add(server switch
            {
                { Done: true } => StaffActionWords.DoneOnDiscord(pending.Action, name, server.Unchanged),
                { Refused: true } => server.Error ?? "Nothing was sent.",
                _ => "Discord did not do it: " + CardText.EscapeText(server.Error ?? "it did not say why."),
            });
        }

        return new DiscordReply(string.Join('\n', lines), [], links);
    }

    private async Task<DiscordReply> NoteAddedAsync(bool onDiscord, string personId, CancellationToken ct)
    {
        var publicAddress = await PublicAddressAsync(ct).ConfigureAwait(false);
        var url = CardLink.UrlFor(onDiscord ? CardSubject.DiscordPerson : CardSubject.Person, personId, publicAddress);

        return url is null
            ? DiscordReply.Say(NoteAddedMessage)
            : new DiscordReply(NoteAddedMessage, [], [new DiscordLinkButton("Open in Modbot", url)]);
    }

    // ── Who is acting ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The staff account behind this Discord account, or the reply that refuses it, in the order
    /// design §3 gives.
    /// </summary>
    private async Task<(ModbotUser? User, DiscordReply? Refusal, string Outcome)> StaffAsync(
        string discordUserId, ModbotPermissions required, bool writes, CancellationToken ct)
    {
        var user = await StaffDiscord.AccountForAsync(_db, discordUserId, _clock.UtcNow, ct).ConfigureAwait(false);

        if (user is null)
            return (null, DiscordReply.Say(DiscordCommandHandler.NotLinkedMessage), "not-linked");

        if (user.IsDisabled)
            return (user, DiscordReply.Say("Your Modbot account is disabled."), "disabled");

        // The web app refuses every request from an account with no VRChat link
        // (VRChatLinkedRequirement); anything here that writes does the same.
        if (writes && !user.IsVRChatLinked)
            return (user, DiscordReply.Say(NeedsVRChatMessage), "no-vrchat");

        if (!DiscordCommands.Allows(user.EffectivePermissions, required))
        {
            return (user, DiscordReply.Say($"You need the \"{DiscordCommands.Label(required)}\" permission in Modbot."), "no-permission");
        }

        return (user, null, "answered");
    }

    private static StaffMember Member(ModbotUser user) => new(user.Id, user.Username, user.EffectivePermissions);

    private static bool IsBot(string discordUserId, IDiscordGateway? gateway)
        => gateway?.BotUserId is { } bot && string.Equals(bot, discordUserId, StringComparison.Ordinal);

    private static async Task<DiscordReply?> ShowAsync(
        Func<DiscordForm, CancellationToken, Task> show, DiscordForm form, CancellationToken ct)
    {
        try
        {
            await show(form, ct).ConfigureAwait(false);
            return null;
        }
        catch (InvalidOperationException)
        {
            return DiscordReply.Say(TooLateMessage);
        }
    }

    private async Task<(CardStyle Style, bool ShowPictures)> StyleAsync(CancellationToken ct)
    {
        var settings = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => new { s.PublicAddress, s.ManagedGroupName, s.VRChatImagesProxied })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return (
            new CardStyle(settings?.PublicAddress, settings?.ManagedGroupName, BrandIcon.For(settings?.PublicAddress)),
            settings?.VRChatImagesProxied ?? true);
    }

    private async Task<string?> PublicAddressAsync(CancellationToken ct)
        => await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.PublicAddress)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <summary>The same access record a slash command leaves (<c>modbot.discord.command</c>).</summary>
    private async Task RecordAsync(
        string discordUserId, ModbotUser? user, string command, string outcome, string? target, CancellationToken ct, string? discordTarget = null)
    {
        var data = new JsonObject
        {
            ["command"] = command,
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
                SubjectId = discordUserId,
                ActorPlatform = user is null ? null : FactPlatform.Modbot,
                ActorId = user?.Id.ToString(),
                Source = FactSource.Discord,
                Data = data,
            }, ct)
            .ConfigureAwait(false);
    }
}
