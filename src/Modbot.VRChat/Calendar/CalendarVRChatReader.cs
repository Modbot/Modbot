using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;
using VRChat.API.Model;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;
using VRChatEvent = VRChat.API.Model.CalendarEvent;

namespace Modbot.VRChat.Calendar;

public enum CalendarReadOutcome
{
    /// <summary>No managed group is set.</summary>
    NotConfigured = 1,

    /// <summary>VRChat's calendar was read and Modbot's brought in line with it.</summary>
    Read = 2,

    /// <summary>Every month asked for was read in the last few minutes, so nothing was asked.</summary>
    Remembered = 3,

    /// <summary>The read lane is cold-stopped, or a sign-in is being waited out. Nothing was retried.</summary>
    Waiting = 4,

    /// <summary>VRChat refused, or did not answer.</summary>
    Failed = 5,
}

/// <summary>One month of VRChat's calendar as read: the rows, whether every page was read, and how.</summary>
/// <param name="Failed">The answer that stopped the read, when one did.</param>
internal sealed record CalendarMonthRead(
    List<VRChatEvent> Rows,
    bool Whole,
    int Requests,
    VRChatResult<PaginatedCalendarEventList>? Failed = null);

/// <param name="Requests">How many requests were sent to VRChat.</param>
/// <param name="Error">What went wrong, in VRChat's words when it gave any.</param>
public sealed record CalendarReadResult(CalendarReadOutcome Outcome, int Requests = 0, string? Error = null);

/// <summary>
/// What was read of VRChat's calendar and when, shared by every request, so ten moderators opening
/// the calendar at once cost one read (calendar design §12.1).
/// </summary>
/// <remarks>
/// In memory: a restart forgets it, which costs one read per month and nothing else.
/// </remarks>
public sealed class CalendarVRChatReadMemory
{
    /// <summary>How long a month's read is used before VRChat is asked again. Refresh skips it.</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(5);

    private readonly Dictionary<DateOnly, DateTimeOffset> _months = [];

    /// <summary>One read at a time: a second waits, then finds the months the first just read.</summary>
    internal SemaphoreSlim Lock { get; } = new(1, 1);

    internal bool IsFresh(DateOnly month, DateTimeOffset now) =>
        _months.TryGetValue(month, out var at) && now - at < KeepFor && now >= at;

    internal void Remember(DateOnly month, DateTimeOffset at) => _months[month] = at;
}

/// <summary>
/// Brings events made on VRChat -- on vrchat.com or in the game -- into Modbot's calendar, and
/// changes and deletes made there to the events Modbot already has (calendar design §12).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only when someone uses it.</strong> The calendar page reads the months it shows when it
/// opens, the VRChat page's Overview reads the month it needs, and Refresh reads again. Nothing
/// reads on a timer. Each month's answer is used for <see cref="CalendarVRChatReadMemory.KeepFor"/>
/// by everyone.
/// </para>
/// <para>
/// <strong>Requests:</strong> one list read per month (more only when a month has more than one
/// page), one read of a repeating event's series when it is new to Modbot or changed on VRChat, and
/// one read of an event that went missing from a month before it is taken as deleted. All on the
/// <c>calendar.read</c> budget. A 429 cold-stops it and is never retried (foundation §4.3.1).
/// </para>
/// <para>
/// <strong>The newest change wins.</strong> VRChat's <c>updatedAt</c> later than the one Modbot last
/// saw means the event was changed on VRChat. That change is copied in, unless an edit made in Modbot
/// is newer and not sent yet; then Modbot's goes out as usual. A change copied in becomes what Modbot
/// last sent, so the publisher does not send it back.
/// </para>
/// <para>
/// <strong>A delete is only a delete once VRChat says so.</strong> An event missing from a month that
/// was read to its end is looked up on its own before anything happens: VRChat's 404, or the event
/// marked deleted, deletes it in Modbot too. Anything else -- a failed read, a read cut short, an
/// event moved to another month -- deletes nothing.
/// </para>
/// </remarks>
public sealed class CalendarVRChatReader
{
    /// <summary>Pages of one month read at most. VRChat's page is 60 events.</summary>
    public const int MaxPagesPerMonth = 3;

    /// <summary>Series read at most in one go; the rest wait for the next read.</summary>
    public const int MaxSeriesReads = 10;

    /// <summary>Missing events looked up at most in one go.</summary>
    public const int MaxLookups = 5;

    /// <summary>
    /// VRChat's month is not exactly the UTC month (an event at 01:00 UTC on the 1st has been seen in
    /// the month before), so an event only counts as missing when Modbot expects it this far inside.
    /// </summary>
    internal static readonly TimeSpan MonthEdge = TimeSpan.FromHours(14);

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly CalendarFacts _facts;
    private readonly CalendarVRChatReadMemory _memory;
    private readonly ILogger _log;

    private int _requests;

    /// <summary>The settings row this read started with, for whether a new event goes to Google too.</summary>
    private Modbot.Core.Data.Entities.Settings? _settings;

    public CalendarVRChatReader(
        IVRChatGate gate,
        ModbotContext db,
        IModbotClock clock,
        CalendarFacts facts,
        CalendarVRChatReadMemory memory,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(memory);

        _gate = gate;
        _db = db;
        _clock = clock;
        _facts = facts;
        _memory = memory;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    /// <summary>The UTC months a range touches, earliest first.</summary>
    public static IReadOnlyList<DateOnly> MonthsOf(DateTimeOffset from, DateTimeOffset to)
    {
        var first = MonthOf(from);
        var last = MonthOf(to > from ? to - TimeSpan.FromTicks(1) : from);
        var months = new List<DateOnly>();

        for (var month = first; month <= last && months.Count < 4; month = month.AddMonths(1))
            months.Add(month);

        return months;
    }

    public static DateOnly MonthOf(DateTimeOffset at)
    {
        var utc = at.UtcDateTime;
        return new DateOnly(utc.Year, utc.Month, 1);
    }

    /// <summary>Reads the months from <paramref name="from"/> to <paramref name="to"/>: what the calendar page shows.</summary>
    public Task<CalendarReadResult> ReadAsync(DateTimeOffset from, DateTimeOffset to, bool refresh, CancellationToken ct = default) =>
        ReadMonthsAsync(MonthsOf(from, to), refresh, ct);

    /// <summary>
    /// Reads this month, and the next as well when nothing on VRChat is left this month: what the
    /// Overview's next event needs.
    /// </summary>
    public async Task<CalendarReadResult> ReadUpcomingAsync(bool refresh, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var month = MonthOf(now);

        var result = await ReadMonthsAsync([month], refresh, ct).ConfigureAwait(false);
        if (result.Outcome is not (CalendarReadOutcome.Read or CalendarReadOutcome.Remembered))
            return result;

        // Anything Modbot has from VRChat still to come this month is the next event; otherwise it
        // may be early next month.
        var monthEnd = new DateTimeOffset(month.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var events = await _db.CalendarEvents.AsNoTracking()
            .Where(e => e.DeletedAt == null && e.PublishToVRChat
                && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open))
            .ToListAsync(ct).ConfigureAwait(false);

        if (events.Any(e => CalendarRepeat.Between(e, now, monthEnd).Any()))
            return result;

        var next = await ReadMonthsAsync([month.AddMonths(1)], refresh, ct).ConfigureAwait(false);
        return next with { Requests = next.Requests + result.Requests };
    }

    private async Task<CalendarReadResult> ReadMonthsAsync(IReadOnlyList<DateOnly> months, bool refresh, CancellationToken ct)
    {
        _requests = 0;

        await _memory.Lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
            if (settings.ManagedGroupId is not { Length: > 0 } groupId)
                return new CalendarReadResult(CalendarReadOutcome.NotConfigured);

            _settings = settings;

            var startedAt = _clock.UtcNow;
            var due = months.Where(m => refresh || !_memory.IsFresh(m, startedAt)).ToList();

            if (due.Count == 0)
                return new CalendarReadResult(CalendarReadOutcome.Remembered);

            var rows = new List<VRChatEvent>();
            var complete = new List<DateOnly>();
            var read = new List<DateOnly>();
            CalendarReadResult? stopped = null;

            foreach (var month in due)
            {
                var (result, monthRows, whole) = await ReadMonthAsync(groupId, month, ct).ConfigureAwait(false);

                if (result is not null)
                {
                    stopped = result;
                    break;
                }

                rows.AddRange(monthRows);
                read.Add(month);

                if (whole)
                    complete.Add(month);
            }

            if (read.Count > 0)
                stopped = await ApplyAsync(groupId, rows, complete, startedAt, ct).ConfigureAwait(false) ?? stopped;

            foreach (var month in read)
                _memory.Remember(month, startedAt);

            _log.Information(
                "Read VRChat's calendar for {Months} with {Requests} requests",
                string.Join(", ", read.Select(m => m.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))),
                _requests);

            return stopped is null
                ? new CalendarReadResult(CalendarReadOutcome.Read, _requests)
                : stopped with { Requests = _requests };
        }
        finally
        {
            _memory.Lock.Release();
        }
    }

    /// <summary>One month's list, page by page. A result when it stopped; whether the whole month was read.</summary>
    private async Task<(CalendarReadResult? Stopped, List<VRChatEvent> Rows, bool Whole)> ReadMonthAsync(
        string groupId, DateOnly month, CancellationToken ct)
    {
        var read = await ReadMonthPagesAsync(_gate, groupId, month, VRChatCallPriority.Interactive, ct).ConfigureAwait(false);
        _requests += read.Requests;

        return read.Failed is { } failed
            ? (Stopped(failed), read.Rows, false)
            : (null, read.Rows, read.Whole);
    }

    /// <summary>
    /// One month of the group's calendar, page by page, at most <see cref="MaxPagesPerMonth"/> pages:
    /// the rows, whether the whole month was read, the failed answer when one stopped it, and how
    /// many requests it took. Shared with the publisher's look for one date's id (§2.2), so both
    /// read a month the same way.
    /// </summary>
    internal static async Task<CalendarMonthRead> ReadMonthPagesAsync(
        IVRChatGate gate, string groupId, DateOnly month, VRChatCallPriority priority, CancellationToken ct)
    {
        var rows = new List<VRChatEvent>();
        var date = month.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var requests = 0;

        for (var page = 0; page < MaxPagesPerMonth; page++)
        {
            var offset = rows.Count;
            requests++;

            var result = await gate.ExecuteAsync(
                new VRChatEndpoint(VRChatEndpointClass.CalendarRead, groupId, "GetGroupCalendarEvents"),
                (client, token) => client.Calendar.GetGroupCalendarEventsWithHttpInfoAsync(
                    groupId, date: date, offset: offset == 0 ? null : offset, cancellationToken: token),
                priority,
                ct).ConfigureAwait(false);

            if (!result.Success)
                return new CalendarMonthRead(rows, Whole: false, requests, result);

            var found = result.Value?.Results ?? [];
            rows.AddRange(found);

            if (result.Value?.HasNext != true || found.Count == 0)
                return new CalendarMonthRead(rows, Whole: true, requests);
        }

        // More pages than are read at once: what was read is used, and nothing counts as missing.
        return new CalendarMonthRead(rows, Whole: false, requests);
    }

    /// <summary>
    /// The UTC months a time may be listed in on VRChat: its own, and the neighbouring one when it is
    /// within <see cref="MonthEdge"/> of the boundary, since VRChat's month is not exactly the UTC month.
    /// </summary>
    internal static IReadOnlyList<DateOnly> MonthsListing(DateTimeOffset at)
    {
        var month = MonthOf(at);
        var months = new List<DateOnly> { month };

        if (MonthOf(at - MonthEdge) != month)
            months.Add(month.AddMonths(-1));

        if (MonthOf(at + MonthEdge) != month)
            months.Add(month.AddMonths(1));

        return months;
    }

    /// <summary>One event on its own: a series with its rule, or a one-off. Null with a 404.</summary>
    private async Task<(CalendarReadResult? Stopped, VRChatEvent? Event, bool Gone)> ReadOneAsync(
        string groupId, string id, CancellationToken ct)
    {
        _requests++;

        var result = await _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.CalendarRead, groupId, "GetGroupCalendarEvent"),
            (client, token) => client.Calendar.GetGroupCalendarEventWithHttpInfoAsync(groupId, id, token),
            VRChatCallPriority.Interactive,
            ct).ConfigureAwait(false);

        if (result.StatusCode == 404)
            return (null, null, true);

        if (!result.Success)
            return (Stopped(result), null, false);

        return result.Value is { } found
            ? (null, found, found.DeletedAt is not null)
            : (Stopped(result), null, false);
    }

    private static CalendarReadResult Stopped<T>(VRChatResult<T> result) =>
        result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting
            ? new CalendarReadResult(CalendarReadOutcome.Waiting, Error: result.ErrorMessage)
            : new CalendarReadResult(
                CalendarReadOutcome.Failed,
                Error: (result.Kind == VRChatFailureKind.WafBlocked ? null : VRChatRefusal.MessageOf(result.RawResponse))
                    ?? result.ErrorMessage
                    ?? $"VRChat answered {result.StatusCode}.");

    /// <summary>
    /// The id Modbot keeps for what a row belongs to: a date of a repeating event belongs to its
    /// series, anything else is itself.
    /// </summary>
    public static string KeyOf(VRChatEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return row.OccurrenceKind == CalendarEventOccurrenceKind.Occurrence && !string.IsNullOrEmpty(row.SeriesId)
            ? row.SeriesId
            : row.Id;
    }

    private async Task<CalendarReadResult?> ApplyAsync(
        string groupId, List<VRChatEvent> rows, List<DateOnly> complete, DateTimeOffset startedAt, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        CalendarReadResult? stopped = null;
        var facts = new List<(string Type, CalendarEvent Event, JsonObject Data)>();

        var places = await _db.CalendarEventPlaces
            .Where(p => p.Place == CalendarPlaces.VRChat)
            .ToListAsync(ct).ConfigureAwait(false);

        var eventIds = places.Select(p => p.EventId).ToList();
        var known = await _db.CalendarEvents
            .Where(e => eventIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct).ConfigureAwait(false);

        var owned = places
            .Where(p => p.ExternalId is { Length: > 0 } && known.ContainsKey(p.EventId))
            .GroupBy(p => p.ExternalId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // A Modbot event whose create has not come back with an id, or whose place was just taken
        // off: a row with its title may be that very event, and taking it in would make a second.
        // Compared by letters and digits only, because VRChat changes the text it is sent.
        var waitingIds = places.Where(p => p.ExternalId is null && p.State != CalendarPlaceStates.Removed).Select(p => p.EventId).ToHashSet();
        var removedIds = places.Where(p => p.State == CalendarPlaceStates.Removed && p.UpdatedAt >= startedAt - CalendarVRChatReadMemory.KeepFor).Select(p => p.EventId).ToHashSet();
        var unplaced = await _db.CalendarEvents.AsNoTracking()
            .Where(e => e.PublishToVRChat && e.DeletedAt == null && !eventIds.Contains(e.Id)
                && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open))
            .Select(e => e.Title)
            .ToListAsync(ct).ConfigureAwait(false);

        // The title a create with no answer sent is held too: an edit since is not on VRChat's copy.
        var sentTitles = places
            .Where(p => waitingIds.Contains(p.EventId) && p.CreateSent is not null && known.ContainsKey(p.EventId))
            .Select(p => CalendarVRChatMatch.AsSent(p, known[p.EventId]).Title);

        var held = known.Values
            .Where(e => waitingIds.Contains(e.Id) || removedIds.Contains(e.Id))
            .Select(e => e.Title)
            .Concat(unplaced)
            .Concat(sentTitles)
            .Select(CalendarVRChatMatch.PlainTitle)
            .ToHashSet(StringComparer.Ordinal);

        // A create that got no answer, which VRChat may have made anyway: a copy of it on the
        // calendar is taken as that event's own rather than taken in, matched against what the
        // create sent (calendar design §3.1). One found not added is still taken for a while, as
        // VRChat may show it late; after that a row like it is somebody else's.
        var unanswered = places
            .Where(p => p.ErrorAt is { } sentAt
                && (CalendarVRChatPublisher.MayHaveBeenCreated(p)
                    || CalendarVRChatPublisher.TryingAgain(p)
                    || (CalendarVRChatPublisher.NotAdded(p) && now - sentAt < CalendarVRChatPublisher.FindLateCopyFor))
                && known.TryGetValue(p.EventId, out var e)
                && CalendarVRChatPublisher.Wants(e))
            .OrderBy(p => p.ErrorAt)
            .ToList();

        var live = rows.Where(r => !r.IsDraft && r.DeletedAt is null && r.Id is { Length: > 0 }).ToList();
        var groups = live.GroupBy(KeyOf, StringComparer.Ordinal).ToList();

        var seen = rows.Where(r => r.DeletedAt is null && r.Id is { Length: > 0 })
            .SelectMany(r => new[] { r.Id, KeyOf(r) })
            .ToHashSet(StringComparer.Ordinal);

        var seriesReads = 0;

        foreach (var group in groups)
        {
            if (stopped is not null)
                break;

            var key = group.Key;
            var first = group.First();
            // Dates of a repeating event carry no rule: the series itself is read for it.
            var isSeries = !string.Equals(key, first.Id, StringComparison.Ordinal);
            var updatedAt = group.Select(CalendarVRChatCopy.UpdatedAt).Max();

            owned.TryGetValue(key, out var place);
            if (place is null)
                place = group.Select(r => owned.GetValueOrDefault(r.Id)).FirstOrDefault(p => p is not null);

            if (place is not null)
            {
                var calendarEvent = known[place.EventId];

                if (calendarEvent.DeletedAt is not null || calendarEvent.State is CalendarEventStates.Cancelled or CalendarEventStates.Draft)
                    continue;

                // First time this event is seen on a read: what VRChat has now is where changes count from.
                if (place.VRChatUpdatedAt is null || updatedAt is null)
                {
                    place.VRChatUpdatedAt ??= updatedAt;
                    continue;
                }

                if (updatedAt <= place.VRChatUpdatedAt)
                    continue;

                if (Pending(calendarEvent, place) && calendarEvent.UpdatedAt >= updatedAt)
                {
                    // Modbot's own edit is newer and on its way; it wins.
                    place.VRChatUpdatedAt = updatedAt;
                    continue;
                }

                VRChatEvent? source = first;
                if (isSeries)
                {
                    if (seriesReads++ >= MaxSeriesReads)
                        continue;

                    var (result, series, gone) = await ReadOneAsync(groupId, key, ct).ConfigureAwait(false);
                    if (result is not null)
                    {
                        stopped = result;
                        break;
                    }

                    if (gone)
                    {
                        Delete(calendarEvent, place, now, facts);
                        continue;
                    }

                    source = series;
                }

                CopyIn(calendarEvent, place, source!, updatedAt, now, facts);
                continue;
            }

            var sent = unanswered.FirstOrDefault(p =>
            {
                var asSent = CalendarVRChatMatch.AsSent(p, known[p.EventId]);
                return group.Any(r => CalendarVRChatMatch.IsCopyOf(r, asSent, p.ErrorAt!.Value));
            });
            if (sent is not null)
            {
                unanswered.Remove(sent);
                Adopt(key, known[sent.EventId], sent, updatedAt, now, facts);
                owned[key] = sent;
                continue;
            }

            if (held.Contains(CalendarVRChatMatch.PlainTitle(first.Title)))
                continue;

            VRChatEvent? made = first;
            if (isSeries)
            {
                if (seriesReads++ >= MaxSeriesReads)
                    continue;

                var (result, series, gone) = await ReadOneAsync(groupId, key, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    stopped = result;
                    break;
                }

                if (gone || series is null)
                    continue;

                made = series;
            }

            if (CalendarVRChatCopy.CannotKeep(made) is { } why)
            {
                _log.Information("VRChat calendar event {VRChatEventId} is not taken in: it {Why}", key, why);
                continue;
            }

            TakeIn(key, made, updatedAt, now, facts);
        }

        if (stopped is null && complete.Count > 0)
            stopped = await FindDeletedAsync(groupId, owned, known, seen, complete, now, facts, ct).ConfigureAwait(false);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        foreach (var (type, calendarEvent, data) in facts)
            await _facts.RecordAsync(type, calendarEvent, data, ct: ct).ConfigureAwait(false);

        return stopped;
    }

    /// <summary>
    /// Events Modbot has on VRChat and expects inside a month that was read to its end, but which
    /// the list did not have: each looked up on its own, and deleted only when VRChat says so.
    /// </summary>
    private async Task<CalendarReadResult?> FindDeletedAsync(
        string groupId,
        Dictionary<string, CalendarEventPlace> owned,
        Dictionary<Guid, CalendarEvent> known,
        HashSet<string> seen,
        List<DateOnly> complete,
        DateTimeOffset now,
        List<(string Type, CalendarEvent Event, JsonObject Data)> facts,
        CancellationToken ct)
    {
        var lookups = 0;

        foreach (var (externalId, place) in owned)
        {
            if (seen.Contains(externalId) || place.State == CalendarPlaceStates.Removed)
                continue;

            var calendarEvent = known[place.EventId];
            if (calendarEvent.DeletedAt is not null
                || calendarEvent.State is CalendarEventStates.Cancelled or CalendarEventStates.Draft
                || !calendarEvent.PublishToVRChat
                || Pending(calendarEvent, place))
            {
                continue;
            }

            var expected = complete.Any(month =>
            {
                var from = new DateTimeOffset(month.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) + MonthEdge;
                var to = new DateTimeOffset(month.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) - MonthEdge;
                return CalendarRepeat.Between(calendarEvent, from, to).Any(o => o.StartsAt >= from);
            });

            if (!expected || lookups++ >= MaxLookups)
                continue;

            var (result, found, gone) = await ReadOneAsync(groupId, externalId, ct).ConfigureAwait(false);
            if (result is not null)
                return result;

            if (gone)
            {
                Delete(calendarEvent, place, now, facts);
                continue;
            }

            // Still there, only somewhere else: a change like any other.
            if (found is not null && CalendarVRChatCopy.UpdatedAt(found) is { } updatedAt
                && (place.VRChatUpdatedAt is null || updatedAt > place.VRChatUpdatedAt))
            {
                if (place.VRChatUpdatedAt is null || (Pending(calendarEvent, place) && calendarEvent.UpdatedAt >= updatedAt))
                    place.VRChatUpdatedAt = updatedAt;
                else
                    CopyIn(calendarEvent, place, found, updatedAt, now, facts);
            }
        }

        return null;
    }

    /// <summary>An edit made in Modbot that the publisher has not sent yet.</summary>
    private static bool Pending(CalendarEvent calendarEvent, CalendarEventPlace place) =>
        place.SentFingerprint != CalendarVRChatRequests.Fingerprint(calendarEvent);

    /// <summary>A new event from VRChat's calendar, kept in Modbot and already published there.</summary>
    private void TakeIn(
        string externalId,
        VRChatEvent source,
        DateTimeOffset? updatedAt,
        DateTimeOffset now,
        List<(string Type, CalendarEvent Event, JsonObject Data)> facts)
    {
        var calendarEvent = new CalendarEvent
        {
            Id = Guid.CreateVersion7(),
            MadeOnVRChat = true,
            PublishToVRChat = true,

            // Onto the Discord server too, as an event made in Modbot is (calendar design §12.2).
            // The channel post stays off: it needs a channel, and nobody picked one.
            PublishToDiscord = true,
            State = CalendarEventStates.Scheduled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        CalendarVRChatCopy.Onto(calendarEvent, source);
        CalendarTimeline.Advance(calendarEvent, now);

        // Onto the Google calendar as well when that is set up and the event is not for members
        // only, as a new event made in Modbot is (Google Calendar design decision 6).
        calendarEvent.PublishToGoogle = _settings is { } settings && CalendarGoogle.TicksByDefault(settings, calendarEvent);

        _db.CalendarEvents.Add(calendarEvent);
        _db.CalendarEventPlaces.Add(new CalendarEventPlace
        {
            EventId = calendarEvent.Id,
            Place = CalendarPlaces.VRChat,
            State = CalendarPlaceStates.Published,
            ExternalId = externalId,
            SentFingerprint = CalendarVRChatRequests.Fingerprint(calendarEvent),
            VRChatUpdatedAt = updatedAt,
            UpdatedAt = now,
        });

        var data = CalendarEventFields.Of(calendarEvent);
        data["on"] = "vrchat";
        facts.Add((FactType.PlannedEventCreated, calendarEvent, data));

        _log.Information("Took in the VRChat calendar event {VRChatEventId} as {EventId}", externalId, calendarEvent.Id);
    }

    /// <summary>
    /// The copy a create with no answer made after all, taken as that event's VRChat place instead of
    /// being taken in as a second event.
    /// </summary>
    private void Adopt(
        string externalId,
        CalendarEvent calendarEvent,
        CalendarEventPlace place,
        DateTimeOffset? updatedAt,
        DateTimeOffset now,
        List<(string Type, CalendarEvent Event, JsonObject Data)> facts)
    {
        CalendarVRChatMatch.Adopt(place, calendarEvent, externalId, updatedAt, now);
        facts.Add((FactType.PlannedEventPublished, calendarEvent, new JsonObject { ["place"] = CalendarPlaces.VRChat }));

        _log.Information(
            "VRChat calendar create for the event {EventId} had gone through after all as {VRChatEventId}",
            calendarEvent.Id, externalId);
    }

    /// <summary>A change made on VRChat, copied onto the Modbot event it belongs to.</summary>
    private void CopyIn(
        CalendarEvent calendarEvent,
        CalendarEventPlace place,
        VRChatEvent source,
        DateTimeOffset? updatedAt,
        DateTimeOffset now,
        List<(string Type, CalendarEvent Event, JsonObject Data)> facts)
    {
        place.VRChatUpdatedAt = updatedAt;

        if (CalendarVRChatCopy.CannotKeep(source) is { } why)
        {
            _log.Warning(
                "VRChat calendar event {VRChatEventId} was changed on VRChat so that it {Why}; Modbot keeps its own version of {EventId}",
                place.ExternalId, why, calendarEvent.Id);
            return;
        }

        var before = CalendarEventFields.Of(calendarEvent);
        var fingerprint = CalendarVRChatRequests.Fingerprint(calendarEvent);
        var picture = calendarEvent.ImageUrl;
        var zoneBefore = CalendarRepeat.ZoneOf(calendarEvent);
        var title = calendarEvent.Title;
        var description = calendarEvent.Description;

        CalendarVRChatCopy.Onto(calendarEvent, source);

        // An event made in Modbot keeps its own words. VRChat rewrites the text it is sent (an en
        // dash dropped, "." turned into "․", seen 2026-10-01), so its copy of a Modbot event's title
        // reads as a change though nobody made one -- and copying it in replaced the moderators'
        // words with VRChat's. Which differences are VRChat's rewrites and which an edit on
        // vrchat.com cannot be told apart, so for these events the words are Modbot's alone (calendar
        // design §12.3); times, repeat and VRChat's own settings are still copied in.
        if (!calendarEvent.MadeOnVRChat)
        {
            calendarEvent.Title = title;
            calendarEvent.Description = description;
        }

        var changed =CalendarVRChatRequests.Fingerprint(calendarEvent) != fingerprint || calendarEvent.ImageUrl != picture;

        if (changed)
        {
            // Given new dates: the current occurrence is worked out again, as an edit on the page does.
            if (calendarEvent.State == CalendarEventStates.Finished)
                calendarEvent.State = CalendarEventStates.Scheduled;

            // Dates changed on their own in Modbot follow the series by day, as after an edit on
            // the page (calendar design §2.2).
            CalendarDates.Rematch(calendarEvent, zoneBefore, now);

            calendarEvent.OccurrenceStartsAt = null;
            CalendarTimeline.Advance(calendarEvent, now);

            calendarEvent.Version++;
            calendarEvent.UpdatedAt = now;
        }

        // What VRChat has is now what Modbot last sent: nothing goes back.
        place.SentFingerprint = CalendarVRChatRequests.Fingerprint(calendarEvent);
        place.State = CalendarPlaceStates.Published;
        place.FailedFingerprint = null;
        place.Error = null;
        place.ErrorAt = null;
        place.MissingGroupPermission = null;
        place.UpdatedAt = now;

        if (!changed)
            return;

        facts.Add((FactType.PlannedEventChanged, calendarEvent, new JsonObject
        {
            ["title"] = calendarEvent.Title,
            ["on"] = "vrchat",
            ["before"] = before,
            ["after"] = CalendarEventFields.Of(calendarEvent),
        }));

        _log.Information("Copied a change made on VRChat to the event {EventId}", calendarEvent.Id);
    }

    /// <summary>Deleted on VRChat: deleted in Modbot too, the way a delete on the page is.</summary>
    private void Delete(
        CalendarEvent calendarEvent,
        CalendarEventPlace place,
        DateTimeOffset now,
        List<(string Type, CalendarEvent Event, JsonObject Data)> facts)
    {
        if (CalendarEventStates.IsLive(calendarEvent.State))
        {
            calendarEvent.State = CalendarEventStates.Cancelled;
            calendarEvent.CancelledAt = now;
        }

        calendarEvent.DeletedAt = now;
        calendarEvent.UpdatedAt = now;
        calendarEvent.Version++;

        // Already gone from VRChat: the publisher has nothing to take off.
        place.State = CalendarPlaceStates.Removed;
        place.ExternalId = null;
        place.SentFingerprint = null;
        place.FailedFingerprint = null;
        place.Error = null;
        place.ErrorAt = null;
        place.MissingGroupPermission = null;
        place.VRChatUpdatedAt = null;
        place.UpdatedAt = now;

        facts.Add((FactType.PlannedEventDeleted, calendarEvent, new JsonObject { ["title"] = calendarEvent.Title, ["on"] = "vrchat" }));

        _log.Information("The event {EventId} was deleted on VRChat; deleted in Modbot too", calendarEvent.Id);
    }
}
