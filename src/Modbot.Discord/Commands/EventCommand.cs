using System.Globalization;
using System.Text.Json.Nodes;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.Interactions;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Commands;

/// <summary>A cancel of one date that was asked for and waits for a press.</summary>
/// <param name="Planned">The date as the event's repeat plans it: what a date is known by.</param>
/// <param name="StartsAt">When the date starts, for the question and the answer.</param>
/// <param name="WholeEvent">A one-off event's only date: the press cancels the event.</param>
public sealed record PendingDateCancel(
    string Token,
    string DiscordUserId,
    DateTimeOffset StartedAt,
    Guid EventId,
    DateTimeOffset Planned,
    bool SayIt,
    string Title,
    DateTimeOffset StartsAt,
    bool WholeEvent) : IPendingConfirmation;

/// <summary>
/// <c>/event</c>: open an event's instance now, and cancel one date of it (Discord commands design
/// §3.3, §3.7 and §4, step 7).
/// </summary>
/// <remarks>
/// <para>
/// <strong>The calendar's own code.</strong> Both go through <see cref="ICalendarActions"/>, which the
/// API implements with <c>CalendarOpener</c> and <c>CalendarCancellations</c>, the code the web app's
/// endpoints call: the same refusals in the same words, the same rows, the same facts
/// (<c>modbot.planned.date.cancelled</c> and the opener's own). The caller is already known:
/// <see cref="DiscordCommandHandler"/> has found the Modbot account, checked it has a VRChat link
/// and Manage calendar, and writes the <c>modbot.discord.command</c> fact when <see cref="RunAsync"/>
/// returns.
/// </para>
/// <para>
/// <strong>Cancelling a date asks first</strong> (design §3.3): "Cancel <strong>Title</strong> on
/// &lt;t:…:f&gt;?" with <strong>Cancel this date</strong> and <strong>Keep it</strong>. Everything the
/// cancel checks is checked before the question, so a question is only ever asked about something
/// that would be done, and the press checks the caller's account and permission again. A cancel is
/// done once whatever happens to the buttons: a second press gets the first answer, and the service
/// answers "cancelled already" without writing a second fact.
/// </para>
/// <para>
/// <strong>A one-off event's only date is the whole event.</strong> The button then says
/// <strong>Cancel the event</strong>.
/// </para>
/// <para>
/// <strong>Say so</strong> is today's cancel behaviour: with it yes, the calendar's Discord loop posts,
/// once, that it is cancelled in the event's channel. An event with no channel refuses it before
/// the question.
/// </para>
/// </remarks>
public sealed class EventCommand
{
    /// <summary>Every button of this command starts with this.</summary>
    public const string ButtonPrefix = DiscordActionButton.Prefix + "event:";

    /// <summary>The question's yes: <c>modbot:event:yes:&lt;token&gt;</c>.</summary>
    public const string YesButton = ButtonPrefix + "yes:";

    /// <summary>The question's no: <c>modbot:event:no:&lt;token&gt;</c>.</summary>
    public const string NoButton = ButtonPrefix + "no:";

    public const string PickEventMessage = "Pick an event from the list.";
    public const string PickDateMessage = "Pick a date from the list.";
    public const string NothingCancelledMessage = "Nothing was cancelled.";
    public const string CancellingMessage = "Cancelling…";
    public const string FailedMessage = "Something went wrong on Modbot's side. Check the event in Modbot before pressing again.";
    public const string AlreadyCancelledMessage = "It was cancelled already.";
    public const string TheEvent = "the event";
    public const string CancelDateLabel = "Cancel this date";
    public const string CancelEventLabel = "Cancel the event";
    public const string KeepItLabel = "Keep it";

    /// <summary>How many dates the date list offers.</summary>
    public const int DatesOffered = 10;

    /// <summary>How many events the event list offers.</summary>
    public const int EventsOffered = DiscordSuggestion.Most;

    private readonly ModbotContext _db;
    private readonly IFactWriter _facts;
    private readonly IModbotClock _clock;
    private readonly PendingConfirmations<PendingDateCancel, CalendarCancelAnswer> _pending;
    private readonly ICalendarActions? _calendar;

    public EventCommand(
        ModbotContext db,
        IFactWriter facts,
        IModbotClock clock,
        PendingConfirmations<PendingDateCancel, CalendarCancelAnswer> pending,
        ICalendarActions? calendar = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(pending);

        _db = db;
        _facts = facts;
        _clock = clock;
        _pending = pending;
        _calendar = calendar;
    }

    /// <summary>Whether a press is one of this command's.</summary>
    public static bool IsButton(string buttonId)
        => buttonId.StartsWith(ButtonPrefix, StringComparison.Ordinal);

    // ── The command ──────────────────────────────────────────────────────────────────────────

    public async Task<StaffCommandAnswer> RunAsync(DiscordCommandCall call, ModbotUser user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(user);

        if (_calendar is null)
            return new StaffCommandAnswer(DiscordReply.Say(StaffInteractionHandler.NotSetUpMessage), "refused");

        if (!Guid.TryParse(call.Option(DiscordCommands.EventOption)?.Trim(), out var eventId))
            return new StaffCommandAnswer(DiscordReply.Say(PickEventMessage), "invalid");

        return call.Subcommand switch
        {
            DiscordCommands.EventOpen => await OpenAsync(eventId, user, ct).ConfigureAwait(false),
            DiscordCommands.EventCancelDate => await AskAsync(call, eventId, user, ct).ConfigureAwait(false),
            _ => new StaffCommandAnswer(DiscordReply.Say("Pick open or cancel-date."), "invalid"),
        };
    }

    private async Task<StaffCommandAnswer> OpenAsync(Guid eventId, ModbotUser user, CancellationToken ct)
    {
        var answer = await _calendar!.OpenNowAsync(eventId, CommandAccess.Member(user), ct).ConfigureAwait(false);

        return answer.Opened
            ? new StaffCommandAnswer(DiscordReply.Say($"Opened **{Name(answer.Title)}**."), "answered")
            : new StaffCommandAnswer(DiscordReply.Say(CardText.EscapeText(answer.Message)), "refused");
    }

    private async Task<StaffCommandAnswer> AskAsync(DiscordCommandCall call, Guid eventId, ModbotUser user, CancellationToken ct)
    {
        if (!DateTimeOffset.TryParse(
                call.Option(DiscordCommands.EventDateOption)?.Trim(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var planned))
        {
            return new StaffCommandAnswer(DiscordReply.Say(PickDateMessage), "invalid");
        }

        var sayIt = call.Flag(DiscordCommands.EventSayOption);
        var plan = await _calendar!.PlanCancelDateAsync(eventId, planned, sayIt, CommandAccess.Member(user), ct).ConfigureAwait(false);

        if (!plan.Allowed)
            return new StaffCommandAnswer(DiscordReply.Say(CardText.EscapeText(plan.Message ?? "That did not work.")), "refused");

        var now = _clock.UtcNow;
        var pending = new PendingDateCancel(
            PendingConfirmations<PendingDateCancel, CalendarCancelAnswer>.NewToken(),
            call.DiscordUserId,
            now,
            eventId,
            planned,
            sayIt,
            plan.Title,
            plan.StartsAt,
            plan.WholeEvent);

        if (!_pending.TryAdd(pending, now))
            return new StaffCommandAnswer(DiscordReply.Say(StaffInteractionHandler.TooManyMessage), "refused");

        return new StaffCommandAnswer(Question(pending), "asked");
    }

    /// <summary>"Cancel **Title** on &lt;t:…:f&gt;?" with the two buttons. Public for tests.</summary>
    public static DiscordReply Question(PendingDateCancel pending)
    {
        ArgumentNullException.ThrowIfNull(pending);

        return new DiscordReply(
            $"Cancel **{Name(pending.Title)}** on {DiscordTime.Absolute(pending.StartsAt)}?",
            [],
            null,
            null,
            [
                new DiscordActionButton(
                    pending.WholeEvent ? CancelEventLabel : CancelDateLabel,
                    YesButton + pending.Token,
                    DiscordButtonStyle.Danger),
                new DiscordActionButton(KeepItLabel, NoButton + pending.Token),
            ]);
    }

    /// <summary>What the question says once the calendar has answered. Public for tests.</summary>
    public static DiscordReply Answer(PendingDateCancel pending, CalendarCancelAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(answer);

        if (!answer.Done)
            return DiscordReply.Say(CardText.EscapeText(answer.Message));

        if (answer.Repeat)
            return DiscordReply.Say(AlreadyCancelledMessage);

        return DiscordReply.Say(
            pending.WholeEvent
                ? $"Cancelled **{Name(pending.Title)}**."
                : $"Cancelled **{Name(pending.Title)}** on {DiscordTime.Absolute(pending.StartsAt)}.");
    }

    // ── The press ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A press on the question's buttons. Null means it was answered already, by rewriting the
    /// question; anything else is the reply to send.
    /// </summary>
    public async Task<DiscordReply?> HandleButtonAsync(DiscordButtonPress press, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(press);

        if (StaffMenus.TokenAfter(press.ButtonId, YesButton) is { } yes)
            return await ConfirmAsync(press, yes, ct).ConfigureAwait(false);

        if (StaffMenus.TokenAfter(press.ButtonId, NoButton) is { } no)
        {
            if (_pending.Get(no, _clock.UtcNow) is { } held && held.DiscordUserId != press.DiscordUserId)
                return DiscordReply.Say(StaffInteractionHandler.NotYoursMessage);

            _pending.Forget(no);
            await press.UpdateAsync(DiscordReply.Say(NothingCancelledMessage), ct).ConfigureAwait(false);
            return null;
        }

        return DiscordReply.Say("Modbot does not know that button.");
    }

    /// <summary>
    /// The question's yes. Checks who is pressing again, rewrites the question so its buttons are
    /// gone at once, and cancels once: a second press gets the first answer.
    /// </summary>
    private async Task<DiscordReply?> ConfirmAsync(DiscordButtonPress press, string token, CancellationToken ct)
    {
        if (_pending.Get(token, _clock.UtcNow) is not { } pending)
        {
            await press.UpdateAsync(DiscordReply.Say(StaffInteractionHandler.RunOutMessage), ct).ConfigureAwait(false);
            return null;
        }

        if (pending.DiscordUserId != press.DiscordUserId)
            return DiscordReply.Say(StaffInteractionHandler.NotYoursMessage);

        // The first answer, before anything that reads the database: it is what removes the
        // buttons, and every later answer edits the same message.
        await press.UpdateAsync(DiscordReply.Say(CancellingMessage), ct).ConfigureAwait(false);

        var about = new JsonObject
        {
            ["event"] = pending.EventId.ToString(),
            ["date"] = pending.Planned.ToString("O", CultureInfo.InvariantCulture),
        };

        // A command the operator switched off after the question was asked is not carried out.
        if (!await CommandSwitchSetting.IsOnAsync(_db, DiscordCommands.Event, ct).ConfigureAwait(false))
        {
            await CommandAccess.RecordAsync(_facts, _clock, press.DiscordUserId, null, DiscordCommands.Event, "off", about, ct).ConfigureAwait(false);
            await press.UpdateAsync(DiscordReply.Say(CommandSwitchSetting.OffMessage(DiscordCommands.Event, menu: false)), ct).ConfigureAwait(false);
            return null;
        }

        var (user, refusal, outcome) = await CommandAccess
            .StaffAsync(_db, _clock, press.DiscordUserId, ModbotPermissions.ManageCalendar, writes: true, DiscordCommands.Event, ct)
            .ConfigureAwait(false);

        if (refusal is not null)
        {
            await CommandAccess.RecordAsync(_facts, _clock, press.DiscordUserId, user, DiscordCommands.Event, outcome, about, ct).ConfigureAwait(false);
            await press.UpdateAsync(refusal, ct).ConfigureAwait(false);
            return null;
        }

        if (_calendar is null)
        {
            await press.UpdateAsync(DiscordReply.Say(StaffInteractionHandler.NotSetUpMessage), ct).ConfigureAwait(false);
            return null;
        }

        CalendarCancelAnswer answer;
        var first = true;

        try
        {
            var calendar = _calendar;
            var member = CommandAccess.Member(user!);

            (answer, first) = await _pending
                .RunOnceAsync(token, () => calendar.CancelDateAsync(pending.EventId, pending.Planned, pending.SayIt, member, ct))
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Whether the cancel was written is not known here; the question must not stay as
            // "Cancelling…", so it sends the person to look.
            await CommandAccess.RecordAsync(_facts, _clock, press.DiscordUserId, user, DiscordCommands.Event, "error", about, ct).ConfigureAwait(false);
            await press.UpdateAsync(DiscordReply.Say(FailedMessage), ct).ConfigureAwait(false);
            return null;
        }

        await press.UpdateAsync(Answer(pending, answer), ct).ConfigureAwait(false);

        var result = answer switch
        {
            { Done: false } => "refused",
            { Repeat: true } => "repeat",
            _ when !first => "repeat",
            _ => "done",
        };

        await CommandAccess.RecordAsync(_facts, _clock, press.DiscordUserId, user, DiscordCommands.Event, result, about, ct).ConfigureAwait(false);
        return null;
    }

    // ── Suggestions ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The events and dates under <c>/event</c> while they are typed: the upcoming events, and the
    /// event already picked's next ten dates. Nothing for anybody the command would refuse.
    /// </summary>
    /// <remarks>Not recorded as a fact: Discord asks on every key press.</remarks>
    public async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(DiscordSuggestionAsk ask, ModbotUser user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ask);
        ArgumentNullException.ThrowIfNull(user);

        if (_calendar is null || user.IsDisabled || !user.IsVRChatLinked)
            return [];

        var member = CommandAccess.Member(user);

        if (ask.OptionName == DiscordCommands.EventOption)
        {
            var events = await _calendar.UpcomingAsync(ask.Typed, EventsOffered, member, ct).ConfigureAwait(false);

            return
            [
                .. events.Select(e => new DiscordSuggestion(
                    CardText.Fit(CardText.Plain($"{e.Title} · {e.When}", DiscordSuggestion.Longest), DiscordSuggestion.Longest),
                    e.Id.ToString())),
            ];
        }

        if (ask.OptionName == DiscordCommands.EventDateOption
            && Guid.TryParse(ask.Option(DiscordCommands.EventOption)?.Trim(), out var eventId))
        {
            var dates = await _calendar.DatesAsync(eventId, DatesOffered, member, ct).ConfigureAwait(false);

            return
            [
                .. dates.Select(d => new DiscordSuggestion(
                    d.StartsAt == d.PlannedStartsAt ? d.When : $"{d.When} (moved)",
                    d.PlannedStartsAt.ToString("O", CultureInfo.InvariantCulture))),
            ];
        }

        return [];
    }

    private static string Name(string? title)
        => string.IsNullOrWhiteSpace(title)
            ? TheEvent
            : CardText.EscapeName(CardText.Plain(title, CalendarEvent.MaxTitleLength));
}
