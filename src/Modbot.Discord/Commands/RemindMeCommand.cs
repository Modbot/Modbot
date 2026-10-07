using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;
using Modbot.Discord.Cards;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;

namespace Modbot.Discord.Commands;

/// <summary>What <see cref="RemindMeCommand.RunAsync"/> answered, and how to name it in the command fact.</summary>
public sealed record RemindMeAnswer(DiscordReply Reply, string Outcome);

/// <summary>
/// <c>/remindme</c>: a direct message before the next date of an event, for any member of the server
/// (Discord commands design §3.5 and §4, step 6).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Running it is the opt-in</strong>, for that one reminder (decision 6: the command is off
/// until the operator turns it on). Modbot sends one confirmation direct message straight away, with
/// <strong>Stop</strong>, which also proves direct messages reach the member. If Discord says they
/// are closed (error 50007) the member is told so privately and <em>nothing is stored</em>: the row
/// is written inside a transaction that is rolled back when the message does not go out.
/// </para>
/// <para>
/// <strong>Only what <c>/events</c> would show.</strong> The events offered and accepted are the
/// ones <see cref="EventsCommand.ListedAsync"/> lists -- published in this Discord, scheduled or
/// open -- so a reminder cannot reveal an event the server does not already show. A repeating event
/// reminds about its next date only (decision 9), and the reminder is for that date as the repeat
/// plans it, so a date moved later is followed (<see cref="Calendar.EventReminderMessages"/>).
/// </para>
/// <para>
/// <strong>Event invites stay separate.</strong> A reminder signs nobody up for invites, and
/// stopping invites under <c>/me</c> does not stop a reminder. Each has its own Stop.
/// </para>
/// <para>
/// With no event named, it lists the member's waiting reminders, each with a Stop button. Every
/// Stop button carries <see cref="DiscordActionButton.ServerMark"/> and the server's id, because a
/// press in a direct message names no server and several Modbots can share one bot.
/// </para>
/// </remarks>
public sealed class RemindMeCommand
{
    /// <summary>The key of the per-person limit, apart from <c>/me</c>'s, <c>/verify</c>'s and <c>/events</c>'.</summary>
    public const string LimitsKey = "remindme";

    public const string TooFastMessage = "Slow down. Try again in a minute.";
    public const string DirectMessagesClosedMessage = "Your direct messages are closed, so Modbot can't remind you.";
    public const string CouldNotMessage = "Modbot could not send you a message.";
    public const string NoEventMessage = "Modbot can't find that event.";
    public const string NoDatesMessage = "That event has no dates left.";
    public const string TooCloseMessage = "That is too close to the start. Pick a shorter time.";
    public const string DuplicateMessage = "You already have a reminder for that date.";
    public const string NoneMessage = "You have no reminders.";
    public const string StoppedMessage = "Reminder stopped.";
    public const string NothingToStopMessage = "Nothing to stop.";

    public static string TooManyMessage { get; } = $"You already have {EventReminder.MostWaiting} reminders.";

    /// <summary>The Stop button's id: <c>modbot:remind:stop:&lt;reminder id&gt;</c>, then the server mark.</summary>
    public const string StopButton = DiscordActionButton.Prefix + "remind:stop:";

    public const string StopLabel = "Stop";

    /// <summary>How long a reminder may be late and still be sent. After that the date is too close to be useful.</summary>
    public static readonly TimeSpan LateBy = TimeSpan.FromMinutes(15);

    /// <summary>A message holds 2,000 characters; Discord refuses more.</summary>
    private const int MessageLength = 2000;

    /// <summary>A title in the list is cut to this, so ten lines always fit one message.</summary>
    private const int TitleInList = 80;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly MemberCommandLimits _limits;

    public RemindMeCommand(
        ModbotContext db,
        IModbotClock clock,
        [FromKeyedServices(LimitsKey)] MemberCommandLimits limits)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limits);

        _db = db;
        _clock = clock;
        _limits = limits;
    }

    /// <summary>Counts one use for this person and says whether it is allowed. See <see cref="MemberCommandLimits"/>.</summary>
    public bool TryUse(string discordUserId) => _limits.TryUse(discordUserId, _clock.UtcNow);

    // ── Words ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The minutes a <c>before</c> choice means; an unknown or missing choice is the default, 1 hour.</summary>
    public static int MinutesFor(string? choice) => choice switch
    {
        DiscordCommands.RemindBefore15Minutes => 15,
        DiscordCommands.RemindBefore1Day => 1440,
        _ => 60,
    };

    /// <summary>"15 minutes", "1 hour" or "1 day".</summary>
    public static string BeforeWords(int minutes) => minutes switch
    {
        15 => "15 minutes",
        1440 => "1 day",
        60 => "1 hour",
        _ => minutes.ToString(CultureInfo.InvariantCulture) + " minutes",
    };

    /// <summary>
    /// The confirmation direct message, with both times named: <c>I'll remind you about **Title** at
    /// &lt;t:…:f&gt;. It starts &lt;t:…:f&gt;.</c>
    /// </summary>
    public static string ConfirmationText(string title, DateTimeOffset remindAt, DateTimeOffset startsAt)
    {
        ArgumentNullException.ThrowIfNull(title);

        return $"I'll remind you about **{Escaped(title)}** at {DiscordTime.Absolute(remindAt)}. It starts {DiscordTime.Absolute(startsAt)}.";
    }

    /// <summary>The private reply to a sign-up: <c>I'll remind you &lt;t:…:f&gt;.</c></summary>
    public static string SignedUpText(DateTimeOffset remindAt) => $"I'll remind you {DiscordTime.Absolute(remindAt)}.";

    /// <summary>A title made safe to sit inside bold text.</summary>
    public static string Escaped(string title)
        => CardText.EscapeName(CardText.Plain(title, CalendarEvent.MaxTitleLength));

    /// <summary>The Stop button for a reminder in a server: its id carries the server's mark.</summary>
    public static DiscordActionButton StopFor(Guid reminderId, string guildId, string label = StopLabel)
        => new(label, DiscordActionButton.Marked(StopButton + reminderId.ToString("D"), guildId));

    /// <summary>Whether a button id is a reminder's Stop button, with or without its server mark.</summary>
    public static bool IsStopButton(string buttonId)
        => DiscordActionButton.Plain(buttonId).StartsWith(StopButton, StringComparison.Ordinal);

    // ── The dates on offer ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The next date of an event that has not started: the one a reminder is for. Null when none
    /// is left. A date that is running now is not "next".
    /// </summary>
    public static CalendarOccurrence? NextDate(CalendarEvent calendarEvent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(calendarEvent);

        foreach (var date in CalendarRepeat.Between(calendarEvent, now))
        {
            if (date.StartsAt > now)
                return date;
        }

        return null;
    }

    // ── Suggestions ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Events to pick under <c>event:</c>, soonest first: the title, with the event's id as the value.
    /// Only events <c>/events</c> would show. One query; Discord gives three seconds.
    /// </summary>
    public async Task<IReadOnlyList<DiscordSuggestion>> SuggestAsync(string typed, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var word = (typed ?? string.Empty).Trim();
        var listed = await EventsCommand.ListedAsync(_db, ct).ConfigureAwait(false);

        return
        [
            .. listed
                .Select(e => (Event: e, Next: NextDate(e, now)))
                .Where(x => x.Next is not null)
                .Where(x => word.Length == 0 || x.Event.Title.Contains(word, StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.Next!.Value.StartsAt)
                .ThenBy(x => x.Event.Title, StringComparer.Ordinal)
                .Take(DiscordSuggestion.Most)
                .Select(x => new DiscordSuggestion(
                    CardText.Plain(CalendarRepeat.TitleOf(x.Event, x.Next!.Value), DiscordSuggestion.Longest),
                    x.Event.Id.ToString("D"))),
        ];
    }

    // ── The command ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <c>/remindme</c> for one member: with <c>event</c>, signs them up; without, lists their
    /// reminders. The caller has already counted the use and checked the switch.
    /// </summary>
    /// <param name="gateway">The session the confirmation goes through; null when the bot is not ready.</param>
    public async Task<RemindMeAnswer> RunAsync(DiscordCommandCall call, IDiscordGateway? gateway, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(call);

        var asked = call.Option(DiscordCommands.RemindEventOption)?.Trim() ?? string.Empty;

        return asked.Length == 0
            ? await ListAsync(call.DiscordUserId, ct).ConfigureAwait(false)
            : await SignUpAsync(call, asked, gateway, ct).ConfigureAwait(false);
    }

    private async Task<RemindMeAnswer> SignUpAsync(
        DiscordCommandCall call, string asked, IDiscordGateway? gateway, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var userId = call.DiscordUserId;

        var listed = await EventsCommand.ListedAsync(_db, ct).ConfigureAwait(false);

        var match = Find(listed, asked);
        if (match is null)
            return new(DiscordReply.Say(NoEventMessage), "no-event");

        if (NextDate(match, now) is not { } date)
            return new(DiscordReply.Say(NoDatesMessage), "no-dates");

        var minutes = MinutesFor(call.Option(DiscordCommands.RemindBeforeOption));
        var remindAt = date.StartsAt - TimeSpan.FromMinutes(minutes);

        // A reminder more than 15 minutes late is never sent, so promising one would be a lie.
        if (now - remindAt > LateBy)
            return new(DiscordReply.Say(TooCloseMessage), "too-close");

        var guildId = await GuildIdAsync(ct).ConfigureAwait(false);
        if (gateway is null || guildId is null)
            return new(DiscordReply.Say(CouldNotMessage), "failed");

        var reminder = new EventReminder
        {
            Id = Guid.CreateVersion7(now),
            DiscordUserId = userId,
            EventId = match.Id,
            OccurrenceStartsAt = date.PlannedStartsAt,
            MinutesBefore = minutes,
            RemindAt = remindAt,
            State = EventReminderStates.Waiting,
            CreatedAt = now,
            UpdatedAt = now,
        };

        // Written first and kept only if the confirmation goes out: a member whose direct messages
        // are closed leaves nothing behind. The Stop button needs the reminder's id, which is why
        // the row exists before the message does.
        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // One member at a time, so the rules below are checked and acted on as a single step: two
        // runs at once cannot both count nine waiting and both add a tenth. The lock is on the
        // member and is let go when the transaction ends. (The unique index backs up the
        // one-per-date rule; nothing else would stop an eleventh.)
        await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"event_reminder:" + userId}, 0))", ct)
            .ConfigureAwait(false);

        if (await _db.EventReminders.AsNoTracking()
                .AnyAsync(r => r.DiscordUserId == userId && r.EventId == match.Id
                               && r.OccurrenceStartsAt == date.PlannedStartsAt && r.State == EventReminderStates.Waiting, ct)
                .ConfigureAwait(false))
        {
            return new(DiscordReply.Say(DuplicateMessage), "duplicate");
        }

        if (await _db.EventReminders.AsNoTracking()
                .CountAsync(r => r.DiscordUserId == userId && r.State == EventReminderStates.Waiting, ct)
                .ConfigureAwait(false) >= EventReminder.MostWaiting)
        {
            return new(DiscordReply.Say(TooManyMessage), "limit");
        }

        _db.EventReminders.Add(reminder);

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two runs at once for the same date: the unique index let the other one in.
            _db.Entry(reminder).State = EntityState.Detached;
            return new(DiscordReply.Say(DuplicateMessage), "duplicate");
        }

        var title = CalendarRepeat.TitleOf(match, date);
        var sent = await gateway.SendDirectMessageAsync(
                userId,
                ConfirmationText(title, remindAt > now ? remindAt : now, date.StartsAt),
                null,
                [StopFor(reminder.Id, guildId)],
                ct)
            .ConfigureAwait(false);

        if (!sent.Sent)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            _db.Entry(reminder).State = EntityState.Detached;

            return sent.DirectMessagesClosed
                ? new(DiscordReply.Say(DirectMessagesClosedMessage), "dms-closed")
                : new(DiscordReply.Say(CouldNotMessage), "failed");
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new(DiscordReply.Say(SignedUpText(remindAt > now ? remindAt : now)), "answered");
    }

    /// <summary>An event picked from the suggestions (its id), or typed by its title when one event has that title.</summary>
    private static CalendarEvent? Find(IReadOnlyList<CalendarEvent> listed, string asked)
    {
        if (Guid.TryParse(asked, out var id))
            return listed.FirstOrDefault(e => e.Id == id);

        var titled = listed.Where(e => string.Equals(e.Title.Trim(), asked, StringComparison.OrdinalIgnoreCase)).ToList();
        return titled.Count == 1 ? titled[0] : null;
    }

    private async Task<string?> GuildIdAsync(CancellationToken ct)
    {
        var id = await _db.Settings.AsNoTracking()
            .Where(s => s.Id == 1)
            .Select(s => s.DiscordGuildId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
    }

    // ── The list ─────────────────────────────────────────────────────────────────────────────

    private async Task<RemindMeAnswer> ListAsync(string userId, CancellationToken ct)
    {
        var reminders = await _db.EventReminders.AsNoTracking()
            .Where(r => r.DiscordUserId == userId && r.State == EventReminderStates.Waiting)
            .OrderBy(r => r.RemindAt)
            .ThenBy(r => r.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (reminders.Count == 0)
            return new(DiscordReply.Say(NoneMessage), "listed");

        var guildId = await GuildIdAsync(ct).ConfigureAwait(false);

        var ids = reminders.Select(r => r.EventId).Distinct().ToList();
        var events = await _db.CalendarEvents.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct)
            .ConfigureAwait(false);

        var text = new StringBuilder();
        var buttons = new List<DiscordActionButton>();
        var number = 0;

        foreach (var reminder in reminders)
        {
            if (!events.TryGetValue(reminder.EventId, out var calendarEvent))
                continue;

            number++;

            // The date as it stands now; a date that has just been cancelled is shown as planned
            // until the next pass marks the reminder skipped.
            var date = CalendarRepeat.ForDate(calendarEvent, reminder.OccurrenceStartsAt);
            var title = date is { } d ? CalendarRepeat.TitleOf(calendarEvent, d) : calendarEvent.Title;
            var starts = date?.StartsAt ?? reminder.OccurrenceStartsAt;

            if (text.Length > 0)
                text.Append('\n');

            if (reminders.Count > 1)
                text.Append(number.ToString(CultureInfo.InvariantCulture)).Append(". ");

            text.Append("**").Append(Escaped(CardText.Plain(title, TitleInList))).Append("** · ")
                .Append(DiscordTime.Absolute(starts))
                .Append(" · ").Append(BeforeWords(reminder.MinutesBefore)).Append(" before");

            if (guildId is not null)
            {
                buttons.Add(StopFor(
                    reminder.Id,
                    guildId,
                    reminders.Count > 1 ? $"{StopLabel} {number.ToString(CultureInfo.InvariantCulture)}" : StopLabel));
            }
        }

        if (number == 0)
            return new(DiscordReply.Say(NoneMessage), "listed");

        return new(
            new DiscordReply(DiscordCommandHandler.WholeLines(text.ToString().Split('\n'), MessageLength), [], Actions: buttons),
            "listed");
    }

    // ── Stop ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A press on a Stop button, from the confirmation direct message or the list. Works whether
    /// <c>/remindme</c> is on or off -- nobody should be left unable to stop something they asked
    /// for -- and only for the member the reminder belongs to. Stops it in one statement that
    /// checks the state, so a press that lands as the message goes out either stops it or finds it
    /// sent, never both.
    /// </summary>
    public async Task<DiscordReply> StopAsync(string discordUserId, string buttonId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discordUserId);
        ArgumentNullException.ThrowIfNull(buttonId);

        var plain = DiscordActionButton.Plain(buttonId);

        if (!plain.StartsWith(StopButton, StringComparison.Ordinal)
            || !Guid.TryParse(plain[StopButton.Length..], out var id))
        {
            return DiscordReply.Say(NothingToStopMessage);
        }

        var now = _clock.UtcNow;

        var stopped = await _db.EventReminders
            .Where(r => r.Id == id && r.DiscordUserId == discordUserId && r.State == EventReminderStates.Waiting)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.State, EventReminderStates.Stopped)
                    .SetProperty(r => r.StoppedAt, now)
                    .SetProperty(r => r.UpdatedAt, now),
                ct)
            .ConfigureAwait(false);

        if (stopped > 0)
            return DiscordReply.Say(StoppedMessage);

        // A second press on the same button is the same answer.
        var already = await _db.EventReminders.AsNoTracking()
            .AnyAsync(r => r.Id == id && r.DiscordUserId == discordUserId && r.State == EventReminderStates.Stopped, ct)
            .ConfigureAwait(false);

        return DiscordReply.Say(already ? StoppedMessage : NothingToStopMessage);
    }
}
