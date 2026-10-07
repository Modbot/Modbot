using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Modbot.Discord.ModerationLog;
using Serilog;

namespace Modbot.Discord.Calendar;

/// <summary>
/// Sends the direct messages members asked for with <c>/remindme</c>, and tidies up the finished
/// ones (Discord commands design §3.5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Worked out from the date as it stands now.</strong> A reminder is for a date as its repeat
/// plans it, and is checked against <see cref="CalendarRepeat.ForDate"/> each time: a date moved
/// on its own is followed (the time is worked out again from where it moved to), and a date or an
/// event that has been cancelled or deleted is marked skipped and sends nothing.
/// </para>
/// <para>
/// <strong>Once.</strong> A reminder is marked sending before the message goes out, in one statement
/// that only succeeds while it is still waiting; a crash between the two leaves it sending, counted
/// as sent and never sent again, and a member who pressed Stop a moment earlier wins or loses
/// cleanly, never both.
/// </para>
/// <para>
/// <strong>Paced.</strong> <see cref="CalendarInviteMessages.Between"/> between two messages and at
/// most <see cref="PerPass"/> a pass, like the event invites, so a long list goes out as a trickle.
/// A reminder more than <see cref="RemindMeCommand.LateBy"/> late is not sent -- a message about an
/// event that is nearly over is noise -- and neither is one for a date that has started.
/// </para>
/// <para>
/// <strong>Only while <c>/remindme</c> is on.</strong> The operator's switch stops the messages as
/// well as the command; reminders wait, and are skipped if they are too late by the time it is
/// switched on again. Stop always works. Sent, stopped, skipped and failed rows are deleted
/// <see cref="KeptFor"/> after they last changed, one stuck sending after <see cref="StuckFor"/>, and a
/// waiting one <see cref="KeptFor"/> after it was due, whether or not the command is on.
/// </para>
/// </remarks>
public sealed class EventReminderMessages
{
    /// <summary>The most direct messages one pass sends.</summary>
    public const int PerPass = 5;

    /// <summary>The pause between two direct messages: the same as the calendar's invites.</summary>
    public static readonly TimeSpan Between = CalendarInviteMessages.Between;

    /// <summary>How long a finished reminder is kept before it is deleted.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    /// <summary>How long a reminder may stay sending before it is deleted: the process ended while it went out.</summary>
    public static readonly TimeSpan StuckFor = TimeSpan.FromDays(1);

    /// <summary>
    /// How far ahead a waiting reminder is looked at. A date moved earlier brings its reminder
    /// forward, so a reminder is checked against its date from this long before it is due, not only
    /// once it is due by the time it was first worked out.
    /// </summary>
    public static readonly TimeSpan LookAhead = TimeSpan.FromDays(2);

    /// <summary>The most waiting reminders one pass looks at.</summary>
    private const int Scan = 500;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public EventReminderMessages(
        ModbotContext db,
        IModbotClock clock,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _clock = clock;
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <summary>The direct message's words: <c>**Title** starts &lt;t:…:f&gt; (&lt;t:…:R&gt;).</c></summary>
    public static string Text(string title, DateTimeOffset startsAt)
    {
        ArgumentNullException.ThrowIfNull(title);

        return $"**{RemindMeCommand.Escaped(title)}** starts {DiscordTime.Absolute(startsAt)} ({DiscordTime.Relative(startsAt)}).";
    }

    /// <returns>How many direct messages went out.</returns>
    public async Task<int> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var now = _clock.UtcNow;

        await DeleteOldAsync(now, ct).ConfigureAwait(false);

        if (!await CommandSwitchSetting.IsOnAsync(_db, DiscordCommands.RemindMe, ct).ConfigureAwait(false))
            return 0;

        var rows = await _db.EventReminders.AsNoTracking()
            .Where(r => r.State == EventReminderStates.Waiting && r.RemindAt <= now + LookAhead)
            .OrderBy(r => r.RemindAt)
            .ThenBy(r => r.Id)
            .Take(Scan)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return 0;

        var ids = rows.Select(r => r.EventId).Distinct().ToList();
        // Only events /events would still show: the same rule, asked again now. An event taken off
        // Discord, cancelled or deleted since the member asked is not in this list, and its
        // reminder is skipped before anything is sent, so no message names what the server no
        // longer shows.
        var events = await EventsCommand.Listed(_db)
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct)
            .ConfigureAwait(false);

        var sent = 0;
        var tried = 0;

        foreach (var row in rows)
        {
            if (tried >= PerPass)
                break;

            now = _clock.UtcNow;

            if (!events.TryGetValue(row.EventId, out var calendarEvent))
            {
                await FinishAsync(row.Id, EventReminderStates.Waiting, EventReminderStates.Skipped, now, ct).ConfigureAwait(false);
                continue;
            }

            // The date as it stands now: null when it was cancelled on its own, or the repeat
            // no longer has a date there.
            if (CalendarRepeat.ForDate(calendarEvent, row.OccurrenceStartsAt) is not { } date)
            {
                await FinishAsync(row.Id, EventReminderStates.Waiting, EventReminderStates.Skipped, now, ct).ConfigureAwait(false);
                continue;
            }

            var remindAt = date.StartsAt - TimeSpan.FromMinutes(row.MinutesBefore);

            // Not due yet, or moved later: the reminder follows the date.
            if (remindAt > now)
            {
                if (remindAt != row.RemindAt)
                    await MoveAsync(row.Id, remindAt, now, ct).ConfigureAwait(false);

                continue;
            }

            // Started already (a date moved earlier), or too late to be worth a message.
            if (date.StartsAt <= now || now - remindAt > RemindMeCommand.LateBy)
            {
                await FinishAsync(row.Id, EventReminderStates.Waiting, EventReminderStates.Skipped, now, ct).ConfigureAwait(false);
                continue;
            }

            if (tried > 0)
                await _delay(Between, ct).ConfigureAwait(false);

            tried++;

            // Written first, and only while it is still waiting: never twice, and never after Stop.
            if (!await ClaimAsync(row.Id, _clock.UtcNow, ct).ConfigureAwait(false))
                continue;

            var title = CalendarRepeat.TitleOf(calendarEvent, date);
            var link = await CalendarJoinLink.ForAnyoneAsync(_db, calendarEvent, date.StartsAt, ct).ConfigureAwait(false);

            DiscordPostOutcome outcome;

            try
            {
                outcome = await gateway.SendDirectMessageAsync(
                        row.DiscordUserId,
                        Text(title, date.StartsAt),
                        link is null ? null : [new DiscordLinkButton(EventsCommand.JoinLabel, link)],
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Whether it went out is not known, so it is failed and never tried again: the
                // row must not stay sending. (A stop in the middle -- the process ending -- leaves
                // it sending, which the clean-up deletes after a day.)
                _log.Warning(e, "A reminder for the event {EventId} blew up while it was sent", row.EventId);
                await FinishAsync(row.Id, EventReminderStates.Sending, EventReminderStates.Failed, _clock.UtcNow, ct).ConfigureAwait(false);
                continue;
            }

            if (outcome.Sent)
            {
                await FinishAsync(row.Id, EventReminderStates.Sending, EventReminderStates.Sent, _clock.UtcNow, ct).ConfigureAwait(false);
                sent++;
                continue;
            }

            await FinishAsync(row.Id, EventReminderStates.Sending, EventReminderStates.Failed, _clock.UtcNow, ct).ConfigureAwait(false);
            _log.Information("A reminder for the event {EventId} was refused", row.EventId);
        }

        return sent;
    }

    /// <summary>
    /// Deletes the reminders that are over. Their ids name a member, so none stays without an end:
    /// a finished one goes <see cref="KeptFor"/> after it last changed, one stuck sending (the
    /// process ended in the middle of it) after <see cref="StuckFor"/>, and a waiting one nobody
    /// handled -- the command was off -- <see cref="KeptFor"/> after it was due.
    /// </summary>
    private Task<int> DeleteOldAsync(DateTimeOffset now, CancellationToken ct)
    {
        var finished = now - KeptFor;
        var stuck = now - StuckFor;

        return _db.EventReminders
            .Where(r => (r.UpdatedAt < finished
                         && (r.State == EventReminderStates.Sent
                             || r.State == EventReminderStates.Stopped
                             || r.State == EventReminderStates.Skipped
                             || r.State == EventReminderStates.Failed))
                        || (r.State == EventReminderStates.Sending && r.UpdatedAt < stuck)
                        || (r.State == EventReminderStates.Waiting && r.RemindAt < finished))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>Waiting to sending, only while it is still waiting. False when the member stopped it first.</summary>
    private async Task<bool> ClaimAsync(Guid id, DateTimeOffset now, CancellationToken ct)
        => await _db.EventReminders
            .Where(r => r.Id == id && r.State == EventReminderStates.Waiting)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.State, EventReminderStates.Sending)
                    .SetProperty(r => r.SentAt, now)
                    .SetProperty(r => r.UpdatedAt, now),
                ct)
            .ConfigureAwait(false) > 0;

    private Task<int> FinishAsync(Guid id, string from, string to, DateTimeOffset now, CancellationToken ct)
        => _db.EventReminders
            .Where(r => r.Id == id && r.State == from)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.State, to)
                    .SetProperty(r => r.UpdatedAt, now),
                ct);

    private Task<int> MoveAsync(Guid id, DateTimeOffset remindAt, DateTimeOffset now, CancellationToken ct)
        => _db.EventReminders
            .Where(r => r.Id == id && r.State == EventReminderStates.Waiting)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.RemindAt, remindAt)
                    .SetProperty(r => r.UpdatedAt, now),
                ct);
}
