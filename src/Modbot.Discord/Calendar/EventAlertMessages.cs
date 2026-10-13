using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.Discord.Commands;
using Modbot.Discord.Gateway;
using Serilog;

namespace Modbot.Discord.Calendar;

/// <summary>
/// Tells the members who signed up (<c>/notify</c>, the Notify me button) that an event is about to
/// start, by direct message or by a ping in a channel (event alerts design §5).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Queue, then send.</strong> A pass first writes an <see cref="EventAlert"/> for each date
/// that is within the admin's minutes of starting and has members signed up, with a delivery for
/// each member; then it sends a few waiting deliveries. The due time is worked out from the date as
/// it stands now, every pass, so nothing is stored until the row is queued: a start moved before that
/// simply moves when it is queued, and a delivery still waiting when its date moves later waits for
/// the new time.
/// </para>
/// <para>
/// <strong>Once.</strong> A date alerts once: the unique index on the event and the date's planned
/// start decides it if two passes meet. A one-time event alerts once for good, so any alert row of
/// its own counts and a start moved afterwards does not alert again. A delivery is marked sending
/// before it goes out, in one statement that only succeeds while it is still waiting; a restart
/// in between leaves it sending, counted as sent and never sent again. Every failure is final:
/// nothing is retried, a closed direct message and a rate limit included.
/// </para>
/// <para>
/// <strong>Paced.</strong> At most <see cref="EventReminderMessages.PerPass"/> direct messages a pass,
/// <see cref="EventReminderMessages.Between"/> apart, and one ping post a pass.
/// </para>
/// </remarks>
public sealed class EventAlertMessages
{
    /// <summary>The most characters Discord allows in one message.</summary>
    public const int MessageLength = 2000;

    /// <summary>The most members one ping post may mention: Discord's limit for allowed mentions.</summary>
    public const int MostPerPing = 100;

    /// <summary>How long a queued alert and its deliveries are kept before they are deleted.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(30);

    /// <summary>The most waiting deliveries one pass looks at.</summary>
    private const int Scan = 500;

    /// <summary>The most finished alerts one pass writes a fact for.</summary>
    private const int FinishPerPass = 50;

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ILogger _log;

    public EventAlertMessages(
        ModbotContext db,
        IModbotClock clock,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);

        _db = db;
        _clock = clock;
        _facts = facts;
        _partitions = partitions;
        _delay = delay ?? Task.Delay;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Discord);
    }

    /// <summary>How many members one ping post holds: as many as fit in 2,000 characters, and no more than 100.</summary>
    /// <param name="userIds">Who is waiting to be pinged, in order.</param>
    /// <param name="text">The words after the mentions.</param>
    public static int FitsInPing(IReadOnlyList<string> userIds, string text)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        ArgumentNullException.ThrowIfNull(text);

        // The gateway writes each mention, a space after it, and then the text.
        var room = MessageLength - text.Length;
        var count = 0;
        var used = 0;

        foreach (var id in userIds)
        {
            var needs = "<@>".Length + id.Length + 1;
            if (count >= MostPerPing || (count > 0 && used + needs > room))
                break;

            used += needs;
            count++;
        }

        return count;
    }

    /// <returns>How many alerts went out: direct messages plus ping posts.</returns>
    public async Task<int> RunOnceAsync(IDiscordGateway gateway, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(gateway);

        var now = _clock.UtcNow;

        await _db.EventAlerts
            .Where(a => a.QueuedAt < now - KeptFor)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        // Off leaves what is queued where it is: it goes if alerts are switched on again before the
        // event starts, and is skipped then otherwise.
        if (!settings.EventAlertsOn)
            return 0;

        var direct = settings.EventAlertsDirectMessage;
        var channel = settings.EventAlertsChannelId is { Length: > 0 } c ? c : null;
        var ping = settings.EventAlertsPing && channel is not null;
        var minutes = TimeSpan.FromMinutes(settings.EventAlertsMinutesBefore);
        var guildId = settings.DiscordGuildId?.Trim();

        if (!direct && !ping)
            return 0;

        await QueueAsync(guildId, direct, ping, minutes, ct).ConfigureAwait(false);

        var sent = await SendAsync(gateway, guildId, direct, ping ? channel : null, minutes, ct).ConfigureAwait(false);

        await FinishAsync(ct).ConfigureAwait(false);

        return sent;
    }

    // ── Queue ────────────────────────────────────────────────────────────────────────────────

    private async Task QueueAsync(string? guildId, bool direct, bool ping, TimeSpan minutes, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var window = now + minutes;

        // The events /events would show, that someone is signed up for.
        var events = await EventsCommand.Listed(_db)
            .Where(e => _db.EventAlertSignups.Any(s => s.EventId == e.Id)
                        || e.Categories.Any(category => _db.EventAlertSignups.Any(s => s.CategoryId == category.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var calendarEvent in events)
        {
            foreach (var date in CalendarRepeat.Between(calendarEvent, now, window).ToList())
            {
                if (date.StartsAt <= now || date.StartsAt > window)
                    continue;

                var oneTime = calendarEvent.Repeat == CalendarRepeats.None;
                var planned = date.PlannedStartsAt;

                // Once: for a one-time event any row counts, whatever date it was queued under.
                var queued = oneTime
                    ? await _db.EventAlerts.AnyAsync(a => a.EventId == calendarEvent.Id, ct).ConfigureAwait(false)
                    : await _db.EventAlerts.AnyAsync(a => a.EventId == calendarEvent.Id && a.OccurrenceStartsAt == planned, ct).ConfigureAwait(false);

                if (queued)
                    continue;

                await QueueDateAsync(calendarEvent, planned, guildId, direct, ping, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task QueueDateAsync(
        CalendarEvent calendarEvent, DateTimeOffset planned, string? guildId, bool direct, bool ping, CancellationToken ct)
    {
        var categoryIds = calendarEvent.Categories.Select(category => category.Id).ToList();

        // Once each, however many of the event's categories they chose.
        var users = await _db.EventAlertSignups.AsNoTracking()
            .Where(s => s.EventId == calendarEvent.Id
                        || (s.CategoryId != null && categoryIds.Contains(s.CategoryId.Value)))
            .Select(s => s.DiscordUserId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (users.Count == 0)
            return;

        var chosen = await _db.EventAlertMembers.AsNoTracking()
            .Where(m => users.Contains(m.DiscordUserId))
            .ToDictionaryAsync(m => m.DiscordUserId, m => m.Way, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var gone = await GoneAsync(guildId, users, ct).ConfigureAwait(false);
        var now = _clock.UtcNow;

        var alert = new EventAlert
        {
            EventId = calendarEvent.Id,
            OccurrenceStartsAt = planned,
            QueuedAt = now,
        };

        var deliveries = new List<EventAlertDelivery>(users.Count);

        foreach (var user in users)
        {
            var way = WayFor(chosen.GetValueOrDefault(user), direct, ping);
            if (way is null)
                continue;

            deliveries.Add(new EventAlertDelivery
            {
                AlertId = alert.Id,
                DiscordUserId = user,
                Way = way,
                State = gone.Contains(user) ? EventAlertDeliveryStates.Skipped : EventAlertDeliveryStates.Waiting,
                UpdatedAt = now,
            });
        }

        _db.EventAlerts.Add(alert);
        _db.EventAlertDeliveries.AddRange(deliveries);

        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException e)
        {
            // Two passes met on the unique index: the other one queued this date.
            _log.Information(e, "The alert for the event {EventId} was queued by another pass", calendarEvent.Id);
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>The member's way, falling back to the other where theirs is off; null when neither is on.</summary>
    private static string? WayFor(string? chosen, bool direct, bool ping)
    {
        var way = EventAlertSettings.IsWay(chosen)
            ? chosen!
            : direct ? EventAlertSettings.Direct : EventAlertSettings.Ping;

        if (way == EventAlertSettings.Direct)
            return direct ? way : ping ? EventAlertSettings.Ping : null;

        return ping ? way : direct ? EventAlertSettings.Direct : null;
    }

    /// <summary>Those of <paramref name="users"/> who left the server or are banned from it.</summary>
    private async Task<HashSet<string>> GoneAsync(string? guildId, IReadOnlyCollection<string> users, CancellationToken ct)
    {
        var gone = new HashSet<string>(StringComparer.Ordinal);

        if (guildId is not { Length: > 0 } || users.Count == 0)
            return gone;

        gone.UnionWith(await _db.DiscordMembers.AsNoTracking()
            .Where(m => m.GuildId == guildId && users.Contains(m.UserId) && m.LeftAt != null)
            .Select(m => m.UserId)
            .ToListAsync(ct)
            .ConfigureAwait(false));

        gone.UnionWith(await _db.DiscordBans.AsNoTracking()
            .Where(b => b.GuildId == guildId && users.Contains(b.UserId) && b.LiftedAt == null)
            .Select(b => b.UserId)
            .ToListAsync(ct)
            .ConfigureAwait(false));

        return gone;
    }

    // ── Send ─────────────────────────────────────────────────────────────────────────────────

    private sealed record Waiting(EventAlertDelivery Delivery, EventAlert Alert);

    private async Task<int> SendAsync(
        IDiscordGateway gateway, string? guildId, bool direct, string? pingChannel, TimeSpan minutes, CancellationToken ct)
    {
        var rows = await (
                from d in _db.EventAlertDeliveries.AsNoTracking()
                join a in _db.EventAlerts.AsNoTracking() on d.AlertId equals a.Id
                where d.State == EventAlertDeliveryStates.Waiting
                orderby a.QueuedAt, d.Id
                select new Waiting(d, a))
            .Take(Scan)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return 0;

        var ids = rows.Select(r => r.Alert.EventId).Distinct().ToList();
        var events = await EventsCommand.Listed(_db)
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct)
            .ConfigureAwait(false);

        var gone = await GoneAsync(guildId, rows.Select(r => r.Delivery.DiscordUserId).Distinct().ToList(), ct).ConfigureAwait(false);

        var sent = 0;
        var tried = 0;
        var now = _clock.UtcNow;

        // Per alert, the members whose ping is ready, in order.
        var pings = new Dictionary<Guid, (CalendarEvent Event, CalendarOccurrence Date, List<EventAlertDelivery> Ready)>();

        foreach (var row in rows)
        {
            var delivery = row.Delivery;

            if (!events.TryGetValue(row.Alert.EventId, out var calendarEvent)
                || DateOf(calendarEvent, row.Alert.OccurrenceStartsAt) is not { } date
                || date.StartsAt <= now
                || gone.Contains(delivery.DiscordUserId)
                || (delivery.Way == EventAlertSettings.Direct ? !direct : pingChannel is null))
            {
                await FinishDeliveryAsync(delivery.Id, EventAlertDeliveryStates.Waiting, EventAlertDeliveryStates.Skipped, false, now, ct)
                    .ConfigureAwait(false);
                continue;
            }

            // Not yet: the date moved later, so the alert follows it.
            if (now < date.StartsAt - minutes)
                continue;

            if (delivery.Way == EventAlertSettings.Ping)
            {
                if (!pings.TryGetValue(delivery.AlertId, out var group))
                    pings[delivery.AlertId] = group = (calendarEvent, date, []);

                group.Ready.Add(delivery);
                continue;
            }

            if (tried >= EventReminderMessages.PerPass)
                continue;

            if (tried > 0)
                await _delay(EventReminderMessages.Between, ct).ConfigureAwait(false);

            tried++;

            if (await SendDirectAsync(gateway, delivery, calendarEvent, date, ct).ConfigureAwait(false))
                sent++;
        }

        // One post a pass.
        if (pingChannel is not null)
        {
            foreach (var (_, group) in pings)
            {
                if (group.Ready.Count == 0)
                    continue;

                if (await SendPingAsync(gateway, pingChannel, group.Event, group.Date, group.Ready, ct).ConfigureAwait(false))
                    sent++;

                break;
            }
        }

        return sent;
    }

    /// <summary>The date an alert is about, as it stands now; null when it was cancelled or the repeat has no date there.</summary>
    private static CalendarOccurrence? DateOf(CalendarEvent calendarEvent, DateTimeOffset planned)
        => calendarEvent.Repeat == CalendarRepeats.None
            // A one-time event has its one date wherever it is now: the alert's own date is where
            // it was when it was queued.
            ? CalendarRepeat.Between(calendarEvent, DateTimeOffset.MinValue).Cast<CalendarOccurrence?>().FirstOrDefault()
            : CalendarRepeat.ForDate(calendarEvent, planned);

    private async Task<bool> SendDirectAsync(
        IDiscordGateway gateway, EventAlertDelivery delivery, CalendarEvent calendarEvent, CalendarOccurrence date, CancellationToken ct)
    {
        // Written first, and only while it is still waiting: never twice.
        if (!await ClaimAsync([delivery.Id], _clock.UtcNow, ct).ConfigureAwait(false))
            return false;

        var title = CalendarRepeat.TitleOf(calendarEvent, date);
        var link = await CalendarJoinLink.ForAnyoneAsync(_db, calendarEvent, date.StartsAt, ct).ConfigureAwait(false);

        DiscordPostOutcome outcome;

        try
        {
            outcome = await gateway.SendDirectMessageAsync(
                    delivery.DiscordUserId,
                    EventReminderMessages.Text(title, date.StartsAt),
                    link is null ? null : [new DiscordLinkButton(EventsCommand.JoinLabel, link)],
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Whether it went out is not known, so it is failed and never tried again.
            _log.Warning(e, "An alert for the event {EventId} blew up while it was sent", calendarEvent.Id);
            await FinishDeliveryAsync(delivery.Id, EventAlertDeliveryStates.Sending, EventAlertDeliveryStates.Failed, false, _clock.UtcNow, ct)
                .ConfigureAwait(false);
            return false;
        }

        if (outcome.Sent)
        {
            await FinishDeliveryAsync(delivery.Id, EventAlertDeliveryStates.Sending, EventAlertDeliveryStates.Sent, false, _clock.UtcNow, ct)
                .ConfigureAwait(false);
            return true;
        }

        await FinishDeliveryAsync(
                delivery.Id, EventAlertDeliveryStates.Sending, EventAlertDeliveryStates.Failed, outcome.DirectMessagesClosed, _clock.UtcNow, ct)
            .ConfigureAwait(false);
        _log.Information("An alert for the event {EventId} was refused", calendarEvent.Id);
        return false;
    }

    private async Task<bool> SendPingAsync(
        IDiscordGateway gateway,
        string channel,
        CalendarEvent calendarEvent,
        CalendarOccurrence date,
        List<EventAlertDelivery> ready,
        CancellationToken ct)
    {
        var title = CalendarRepeat.TitleOf(calendarEvent, date);
        var text = EventReminderMessages.Text(title, date.StartsAt);
        var link = await CalendarJoinLink.ForAnyoneAsync(_db, calendarEvent, date.StartsAt, ct).ConfigureAwait(false);

        var chunk = ready.Take(FitsInPing(ready.Select(d => d.DiscordUserId).ToList(), text)).ToList();
        var claimedAt = _clock.UtcNow;

        // Claimed in one statement, only the ones still waiting; whoever a purge removed meanwhile
        // is not pinged.
        var chunkIds = chunk.Select(d => d.Id).ToList();
        await ClaimAsync(chunkIds, claimedAt, ct).ConfigureAwait(false);

        var users = await _db.EventAlertDeliveries.AsNoTracking()
            .Where(d => chunkIds.Contains(d.Id) && d.State == EventAlertDeliveryStates.Sending && d.UpdatedAt == claimedAt)
            .OrderBy(d => d.Id)
            .Select(d => d.DiscordUserId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (users.Count == 0)
            return false;

        var claimed = chunk.Where(d => users.Contains(d.DiscordUserId)).Select(d => d.Id).ToList();

        DiscordPostOutcome outcome;

        try
        {
            outcome = await gateway.SendPingAsync(
                    channel,
                    users,
                    text,
                    link is null ? null : [new DiscordLinkButton(EventsCommand.JoinLabel, link)],
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warning(e, "A ping for the event {EventId} blew up while it was sent", calendarEvent.Id);
            await FinishManyAsync(claimed, EventAlertDeliveryStates.Failed, _clock.UtcNow, ct).ConfigureAwait(false);
            return false;
        }

        // Anything but a clear "posted" is final, an unclear answer included: a second post is worse.
        await FinishManyAsync(claimed, outcome.Sent ? EventAlertDeliveryStates.Sent : EventAlertDeliveryStates.Failed, _clock.UtcNow, ct)
            .ConfigureAwait(false);

        if (!outcome.Sent)
            _log.Information("A ping for the event {EventId} was refused", calendarEvent.Id);

        return outcome.Sent;
    }

    private async Task<bool> ClaimAsync(IReadOnlyCollection<Guid> ids, DateTimeOffset now, CancellationToken ct)
        => await _db.EventAlertDeliveries
            .Where(d => ids.Contains(d.Id) && d.State == EventAlertDeliveryStates.Waiting)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(d => d.State, EventAlertDeliveryStates.Sending)
                    .SetProperty(d => d.UpdatedAt, now),
                ct)
            .ConfigureAwait(false) > 0;

    private Task<int> FinishDeliveryAsync(
        Guid id, string from, string to, bool closed, DateTimeOffset now, CancellationToken ct)
        => _db.EventAlertDeliveries
            .Where(d => d.Id == id && d.State == from)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(d => d.State, to)
                    .SetProperty(d => d.DirectMessagesClosed, closed)
                    .SetProperty(d => d.UpdatedAt, now),
                ct);

    private Task<int> FinishManyAsync(IReadOnlyCollection<Guid> ids, string to, DateTimeOffset now, CancellationToken ct)
        => _db.EventAlertDeliveries
            .Where(d => ids.Contains(d.Id) && d.State == EventAlertDeliveryStates.Sending)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(d => d.State, to)
                    .SetProperty(d => d.UpdatedAt, now),
                ct);

    // ── The fact ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the fact for each alert whose deliveries are all done and sets its finish time. The
    /// time is set first, so a fact is never written twice. A delivery stuck sending counts as sent.
    /// </summary>
    private async Task FinishAsync(CancellationToken ct)
    {
        var done = await _db.EventAlerts.AsNoTracking()
            .Where(a => a.FinishedAt == null
                        && !_db.EventAlertDeliveries.Any(d => d.AlertId == a.Id && d.State == EventAlertDeliveryStates.Waiting))
            .OrderBy(a => a.QueuedAt)
            .Take(FinishPerPass)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var alert in done)
        {
            var now = _clock.UtcNow;

            var claimed = await _db.EventAlerts
                .Where(a => a.Id == alert.Id && a.FinishedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(a => a.FinishedAt, now), ct)
                .ConfigureAwait(false);

            if (claimed == 0)
                continue;

            var states = await _db.EventAlertDeliveries.AsNoTracking()
                .Where(d => d.AlertId == alert.Id)
                .Select(d => new { d.Way, d.State, d.DirectMessagesClosed })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            static bool WentOut(string state) => state is EventAlertDeliveryStates.Sent or EventAlertDeliveryStates.Sending;

            var title = await _db.CalendarEvents.AsNoTracking()
                .Where(e => e.Id == alert.EventId)
                .Select(e => e.Title)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false) ?? string.Empty;

            await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);
            await _facts.WriteAsync(
                new FactRecord
                {
                    Type = FactType.EventAlertSent,
                    OccurredAt = now,
                    SubjectPlatform = FactPlatform.Modbot,
                    SubjectId = alert.EventId.ToString(),
                    Source = FactSource.Modbot,
                    Data = new JsonObject
                    {
                        ["title"] = title,
                        ["occurrenceStartsAt"] = alert.OccurrenceStartsAt.ToString("O"),
                        ["direct"] = states.Count(s => s.Way == EventAlertSettings.Direct && WentOut(s.State)),
                        ["ping"] = states.Count(s => s.Way == EventAlertSettings.Ping && WentOut(s.State)),
                        ["closed"] = states.Count(s => s.State == EventAlertDeliveryStates.Failed && s.DirectMessagesClosed),
                        ["failed"] = states.Count(s => s.State == EventAlertDeliveryStates.Failed && !s.DirectMessagesClosed),
                        ["skipped"] = states.Count(s => s.State == EventAlertDeliveryStates.Skipped),
                    },
                },
                ct).ConfigureAwait(false);
        }
    }
}
