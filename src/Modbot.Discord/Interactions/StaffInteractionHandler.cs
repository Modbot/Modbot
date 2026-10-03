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

    /// <summary>The longest note, as the note service allows.</summary>
    public const int NoteLength = 2000;

    /// <summary>The longest note on an action, as the moderation service allows and a Discord text box takes.</summary>
    public const int ActionNoteLength = 4000;

    /// <summary>A Discord list offers at most this many.</summary>
    public const int MaxReasonsOffered = 25;

    public const string NoteField = "note";
    public const string ReasonsField = "reasons";

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly IModbotClock _clock;
    private readonly LookupQuery _lookup;
    private readonly PendingStaffActions _pending;
    private readonly CardPictures _pictures;
    private readonly IStaffActions? _staff;
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

        DiscordReply reply;
        var vrchatId = await _db.LinkedVRChatUserIdAsync(target.Id, ct).ConfigureAwait(false);

        // Linked: the person is their VRChat profile, as /lookup shows it, with what the caller's
        // permissions let /lookup show.
        var summary = vrchatId is { Length: > 0 }
            ? await _lookup.SummarizeAsync(vrchatId, null, LookupSight.Of(user.EffectivePermissions), ct).ConfigureAwait(false)
            : null;

        if (vrchatId is { Length: > 0 } && summary is not null)
        {
            var profile = summary.Profile;

            var pictures = _pictures.ForMessage(showPictures);
            var picture = new CardPicture(
                Thumbnail: await pictures.AddAsync(ProfilePictures.Best(profile), ct).ConfigureAwait(false),
                Image: await pictures.AddAsync(profile?.BannerUrl, ct).ConfigureAwait(false),
                AuthorIcon: await pictures.AddAsync(profile?.RepresentedGroupIconUrl, ct).ConfigureAwait(false));

            reply = new DiscordReply(
                null,
                [DiscordCommandHandler.ProfileCard(summary, style, picture)],
                null,
                pictures.Files,
                CardButtons.ForLookup(onDiscord: false, vrchatId, summary.IsBanned, Allows));
        }
        else
        {
            reply = new DiscordReply(
                null,
                [await DiscordCardAsync(target, style, ct).ConfigureAwait(false)],
                null,
                null,
                CardButtons.ForLookup(onDiscord: true, target.Id, banned: false, Allows));
        }

        await RecordAsync(call.DiscordUserId, user, call.CommandName, "answered", target.Id, ct).ConfigureAwait(false);
        return reply;
    }

    /// <summary>
    /// A Discord account with no VRChat link: what Modbot has recorded about it in Discord. Counts
    /// only; the notes themselves are read in Modbot, behind its own audit-log permission.
    /// </summary>
    private async Task<DiscordEmbedContent> DiscordCardAsync(DiscordTargetUser target, CardStyle style, CancellationToken ct)
    {
        string[] types =
        [
            FactType.NoteAdded,
            FactType.NoteTakenBack,
            FactType.DiscordMemberTimedOut,
            FactType.DiscordMemberBanned,
            FactType.DiscordMemberKicked,
        ];

        var counts = await _db.Events.AsNoTracking()
            .Where(e => e.SubjectPlatform == FactPlatform.Discord && e.SubjectId == target.Id && types.Contains(e.Type))
            .GroupBy(e => e.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Type, g => g.Count, ct)
            .ConfigureAwait(false);

        int Count(string type) => counts.GetValueOrDefault(type);

        var notes = Math.Max(0, Count(FactType.NoteAdded) - Count(FactType.NoteTakenBack));

        return new DiscordEmbedContent(
            CardText.Plain(target.Username, 256),
            null,
            CardColour.Violet,
            [
                new DiscordEmbedField("VRChat", "Not linked", Inline: true),
                new DiscordEmbedField("Notes", notes.ToString(CultureInfo.InvariantCulture), Inline: true),
                new DiscordEmbedField(
                    "Timeouts · bans · kicks",
                    $"{Count(FactType.DiscordMemberTimedOut)} · {Count(FactType.DiscordMemberBanned)} · {Count(FactType.DiscordMemberKicked)}",
                    Inline: true),
            ],
            null,
            CardLink.UrlFor(CardSubject.DiscordPerson, target.Id, style.PublicAddress),
            style.GroupFooter,
            FooterIconUrl: style.FooterIconUrl);
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

        var reasons = await _staff.ReasonsAsync(action, ct).ConfigureAwait(false);
        if (reasons.Required && reasons.Reasons.Count == 0)
            return DiscordReply.Say(NoReasonsMessage);

        if (!_pending.TryAdd(pending, now))
            return DiscordReply.Say(TooManyMessage);

        var fields = new List<DiscordFormField>();

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

        var form = new DiscordForm(StaffActionWords.FormTitle(action, name), StaffMenus.ActForm + pending.Token, fields);

        var shown = await ShowAsync(press.ShowFormAsync, form, ct).ConfigureAwait(false);
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
        await press.UpdateAsync(DiscordReply.Say(StaffActionWords.Doing(pending.Action, name)), ct).ConfigureAwait(false);

        var (user, refusal, outcome) = await StaffAsync(press.DiscordUserId, StaffActionWords.Requires(pending.Action), writes: true, ct)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            await RecordAsync(press.DiscordUserId, user, pending.Action, outcome, pending.SubjectId, ct).ConfigureAwait(false);
            await press.UpdateAsync(refusal, ct).ConfigureAwait(false);
            return null;
        }

        if (_staff is null)
        {
            await press.UpdateAsync(DiscordReply.Say(NotSetUpMessage), ct).ConfigureAwait(false);
            return null;
        }

        StaffActionAnswer answer;

        try
        {
            answer = await _staff.RunAsync(
                    pending.Action, pending.Key, pending.SubjectId, pending.ReasonIds, pending.Note, Member(user!), ct)
                .ConfigureAwait(false);
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

            await RecordAsync(press.DiscordUserId, user, pending.Action, "error", pending.SubjectId, ct).ConfigureAwait(false);
            return null;
        }

        var publicAddress = await PublicAddressAsync(ct).ConfigureAwait(false);
        await press.UpdateAsync(Answer(pending, answer, publicAddress), ct).ConfigureAwait(false);

        // The one thing the channel sees: who dealt with the card, once, and only when it is done.
        if (answer is { Done: true, Repeat: false }
            && pending is { CardChannelId: { } channelId, CardMessageId: { } messageId }
            && gateway is not null)
        {
            var line = StaffActionWords.Handled(pending.Action, CardText.EscapeName(user!.Username));
            var marked = await gateway.MarkHandledAsync(channelId, messageId, line, StaffMenus.ActButton, ct).ConfigureAwait(false);

            if (!marked.Sent)
                _log.Warning("Could not mark the card {Message} as handled: {Reason}", messageId, marked.Error);

            // Repeats are never folded into a card somebody acted on: the fold rewrites the message
            // whole, which would take this line away and bring back the buttons it took off.
            await _db.DiscordEventChannels
                .Where(c => c.RepeatPostId == messageId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RepeatPostId, (string?)null), ct)
                .ConfigureAwait(false);
        }

        var result = answer switch
        {
            { Repeat: true } => "repeat",
            { Done: true } => "done",
            { Refused: true } => "refused",
            _ => "failed",
        };

        await RecordAsync(press.DiscordUserId, user, pending.Action, result, pending.SubjectId, ct).ConfigureAwait(false);
        return null;
    }

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

        var (user, refusal, outcome) = await StaffAsync(submit.DiscordUserId, StaffActionWords.Requires(pending.Action), writes: true, ct)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            await RecordAsync(submit.DiscordUserId, user, pending.Action, outcome, pending.SubjectId, ct).ConfigureAwait(false);
            return refusal;
        }

        if (_staff is null)
            return DiscordReply.Say(NotSetUpMessage);

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
            await RecordAsync(submit.DiscordUserId, user, pending.Action, "refused", pending.SubjectId, ct).ConfigureAwait(false);
            return DiscordReply.Say(no);
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

        var sb = new StringBuilder(StaffActionWords.Question(pending.Action, CardText.EscapeName(pending.SubjectName)));

        if (pending.ReasonLabels.Count > 0)
            sb.Append("\nReasons: ").Append(CardText.EscapeText(string.Join(", ", pending.ReasonLabels)));

        if (pending.Note.Length > 0)
            sb.Append("\nNote: ").Append(CardText.Fit(CardText.EscapeText(pending.Note), 1000));

        return new DiscordReply(
            sb.ToString(),
            [],
            null,
            null,
            [
                new DiscordActionButton(
                    StaffActionWords.Label(pending.Action),
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
        string discordUserId, ModbotUser? user, string command, string outcome, string? target, CancellationToken ct)
    {
        var data = new JsonObject
        {
            ["command"] = command,
            ["outcome"] = outcome,
        };

        if (target is not null)
            data["target"] = target;

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
