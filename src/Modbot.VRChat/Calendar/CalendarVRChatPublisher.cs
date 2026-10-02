using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;
using CalendarEventOccurrenceKind = VRChat.API.Model.CalendarEventOccurrenceKind;
using PaginatedCalendarEventList = VRChat.API.Model.PaginatedCalendarEventList;
using VRChatCalendarEvent = VRChat.API.Model.CalendarEvent;

namespace Modbot.VRChat.Calendar;

public enum CalendarPublishOutcome
{
    /// <summary>No managed group is set.</summary>
    NotConfigured = 1,

    /// <summary>Everything is as it should be, or waiting for edits to settle.</summary>
    NothingToDo = 2,

    /// <summary>One write went through.</summary>
    Written = 3,

    /// <summary>The calendar bucket is cold-stopped, or a sign-in is being waited out. Nothing was retried.</summary>
    RateLimited = 4,

    /// <summary>VRChat refused, or did not answer.</summary>
    Failed = 5,
}

/// <param name="Action"><c>create</c>, <c>update</c> or <c>delete</c>, when a write was attempted.</param>
public sealed record CalendarPublishResult(CalendarPublishOutcome Outcome, Guid? EventId = null, string? Action = null);

/// <summary>
/// Keeps the group's VRChat calendar in line with the events planned in Modbot (calendar design §3.1).
/// </summary>
/// <remarks>
/// <para>
/// <strong>One write a pass.</strong> The calendar lane allows one write a minute and the gate waits
/// for it, so a second write in the same pass would only hold the pass up.
/// </para>
/// <para>
/// <strong>Quick edits fold into one update.</strong> An event is not written until it has gone
/// <see cref="SettleFor"/> without a change, and then whatever it says at that moment is sent.
/// </para>
/// <para>
/// <strong>A 429 is never retried.</strong> The limiter cold-stops the calendar bucket; the place
/// shows "waiting"; the next pass asks again, and the limiter refuses without sending until the
/// stop's own wait is over and its single probe is due (foundation §4.3.1).
/// </para>
/// <para>
/// A refusal is not sent again until the event changes. An update or delete with no answer -- a
/// timeout, or VRChat's own 5xx -- is tried again after <see cref="RetryUnansweredAfter"/>.
/// </para>
/// <para>
/// <strong>A create with no answer is never sent again on its own.</strong> VRChat has answered
/// creates with a 500 and made the event anyway (2026-09-25, and three times on 2026-10-01), so the
/// place waits, and <see cref="LookAfter"/> later the group's calendar is read, every page of the
/// event's month, for the copy (<see cref="CalendarVRChatMatch"/>). One found is taken as this
/// event's own, or deleted if the event is no longer wanted. It is looked for as the create sent it
/// (<see cref="CalendarEventPlace.CreateSent"/>), so a fix made meanwhile goes out as an update.
/// With none found after a whole read, the place fails with <see cref="NotAddedError"/>, and only a
/// moderator's Try again (<see cref="TryAgain"/>) sends the create again, after one more look. Until
/// 2026-10-01 the look ran only before an automatic retry 15 minutes on, compared titles exactly,
/// and read one page; the calendar read had already taken VRChat's copy in as a second event by then.
/// </para>
/// <para>
/// While the place waits for the look nothing has failed yet, so no failure fact is written until
/// the create is found not added.
/// </para>
/// </remarks>
public sealed class CalendarVRChatPublisher
{
    public static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan RetryUnansweredAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long after a create with no answer the calendar is read for the copy it may have made.
    /// A look that could not be made is made again <see cref="RetryUnansweredAfter"/> later.
    /// </summary>
    public static readonly TimeSpan LookAfter = TimeSpan.FromMinutes(2);

    /// <summary>What the place says when a create with no answer is not on VRChat's calendar either.</summary>
    public const string NotAddedError = "VRChat did not add the event.";

    /// <summary>The fingerprint a refused delete is kept under, so it is not repeated every pass.</summary>
    private const string DeleteFingerprint = "delete";

    /// <summary>
    /// The store marker kept as the failed fingerprint of a create with no answer that was then not
    /// found on VRChat's calendar: nothing is sent until a moderator presses Try again.
    /// </summary>
    public const string NotAddedFingerprint = "not-added";

    /// <summary>
    /// The store marker for a moderator's Try again: the calendar is looked at once more, and the
    /// create is sent only when the copy is not there.
    /// </summary>
    public const string TryAgainFingerprint = "try-again";

    /// <summary>
    /// How long after a create that was not added the calendar read still takes a copy that turns
    /// up late as that event's own. Later than this, a row like it is somebody else's.
    /// </summary>
    public static readonly TimeSpan FindLateCopyFor = TimeSpan.FromHours(24);

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly CalendarFacts _facts;
    private readonly ILogger _log;

    /// <summary>
    /// Why this pass could not look for an earlier copy before a retry, when that is what failed.
    /// The fact says so as its own thing rather than as a failed write: nothing was written.
    /// </summary>
    private string? _couldNotCheck;

    /// <summary>
    /// A create got no answer this pass. The place only waits: whether it failed is not known until
    /// the calendar is read, so no failure fact is written yet.
    /// </summary>
    private bool _waitingToLook;

    public CalendarVRChatPublisher(
        IVRChatGate gate, ModbotContext db, IModbotClock clock, CalendarFacts facts, ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);

        _gate = gate;
        _db = db;
        _clock = clock;
        _facts = facts;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<CalendarPublishResult> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.ManagedGroupId is not { Length: > 0 } groupId)
            return new CalendarPublishResult(CalendarPublishOutcome.NotConfigured);

        var now = _clock.UtcNow;

        var places = await _db.CalendarEventPlaces
            .Where(p => p.Place == CalendarPlaces.VRChat && p.State != CalendarPlaceStates.Removed)
            .ToDictionaryAsync(p => p.EventId, ct).ConfigureAwait(false);

        var placeIds = places.Keys.ToList();

        // What each place said before this pass, and whether VRChat held a copy of it, so a change
        // of state writes its fact (below).
        var was = places.ToDictionary(p => p.Key, p => (State: p.Value.State, OnVRChat: p.Value.ExternalId is not null));

        var events = await _db.CalendarEvents
            .Where(e => placeIds.Contains(e.Id)
                || (e.PublishToVRChat
                    && e.DeletedAt == null
                    && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open)))
            .OrderBy(e => e.UpdatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        (CalendarEvent Event, CalendarEventPlace Place, string Action, string Fingerprint)? due = null;

        foreach (var calendarEvent in events)
        {
            places.TryGetValue(calendarEvent.Id, out var place);

            if (Wants(calendarEvent))
            {
                if (place is null)
                {
                    place = new CalendarEventPlace
                    {
                        EventId = calendarEvent.Id,
                        Place = CalendarPlaces.VRChat,
                        State = CalendarPlaceStates.Waiting,
                        UpdatedAt = now,
                    };

                    _db.CalendarEventPlaces.Add(place);
                    places[calendarEvent.Id] = place;
                }

                var fingerprint = CalendarVRChatRequests.Fingerprint(calendarEvent);

                // A create that got no answer: its copy is looked for, and it is never sent again
                // except by a moderator's Try again.
                if (NotAdded(place))
                    continue;

                // A moderator's Try again: one more look, then the create.
                if (TryingAgain(place))
                {
                    due ??= (calendarEvent, place, "create", fingerprint);
                    continue;
                }

                if (MayHaveBeenCreated(place))
                {
                    if (LookDue(place, now))
                        due ??= (calendarEvent, place, "create", fingerprint);

                    continue;
                }

                if (place.ExternalId is not null && place.SentFingerprint == fingerprint)
                    continue;

                if (Held(place, fingerprint, now))
                    continue;

                if (place.State != CalendarPlaceStates.Waiting)
                {
                    place.State = CalendarPlaceStates.Waiting;
                    place.UpdatedAt = now;
                }

                // Still being edited: wait for it to settle, so three quick fixes are one write.
                if (now - calendarEvent.UpdatedAt < SettleFor)
                    continue;

                due ??= (calendarEvent, place, place.ExternalId is null ? "create" : "update", fingerprint);
                continue;
            }

            if (place is null)
                continue;

            // A finished event stays on VRChat's calendar as history, as long as it is still ticked.
            if (calendarEvent.State == CalendarEventStates.Finished
                && calendarEvent.PublishToVRChat
                && calendarEvent.DeletedAt is null)
            {
                continue;
            }

            // Never on VRChat, unless a create that got no answer made it anyway: then look first.
            if (place.ExternalId is null && !MayHaveBeenCreated(place))
            {
                place.State = CalendarPlaceStates.Removed;
                place.UpdatedAt = now;
                continue;
            }

            if (MayHaveBeenCreated(place))
            {
                if (LookDue(place, now))
                    due ??= (calendarEvent, place, "delete", DeleteFingerprint);

                continue;
            }

            if (Held(place, DeleteFingerprint, now))
                continue;

            due ??= (calendarEvent, place, "delete", DeleteFingerprint);
        }

        if (due is not { } work)
        {
            // The series are as they should be: a date changed on its own is next (§2.2).
            if (DueDate(events, places, now) is { } date)
            {
                var dateOutcome = await WriteDateAsync(date.Event, date.Place, date.Change, date.Fingerprint, groupId, now, ct)
                    .ConfigureAwait(false);

                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                if (dateOutcome == CalendarPublishOutcome.Failed)
                {
                    await _facts.RecordAsync(
                        FactType.PlannedEventPublishFailed,
                        date.Event,
                        new JsonObject
                        {
                            ["place"] = CalendarPlaces.VRChat,
                            ["action"] = "date",
                            ["date"] = date.Change.PlannedStartsAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                            ["error"] = date.Change.VRChatError,
                        },
                        ct: ct).ConfigureAwait(false);
                }

                return new CalendarPublishResult(dateOutcome, date.Event.Id, "date");
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await RecordChangesAsync(events, places, was, ct).ConfigureAwait(false);
            return new CalendarPublishResult(CalendarPublishOutcome.NothingToDo);
        }

        var outcome = await WriteAsync(work.Event, work.Place, work.Action, work.Fingerprint, groupId, settings, now, ct)
            .ConfigureAwait(false);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RecordChangesAsync(events, places, was, ct).ConfigureAwait(false);

        if (outcome == CalendarPublishOutcome.Failed && !_waitingToLook)
        {
            var failed = new JsonObject
            {
                ["place"] = CalendarPlaces.VRChat,
                ["action"] = _couldNotCheck is null ? work.Action : "check",
                ["error"] = _couldNotCheck ?? work.Place.Error,
            };

            // A refusal for a group permission Modbot's VRChat account lacks says which one, so the
            // fact's card in a Discord channel can say what to give it as the calendar page does.
            if (_couldNotCheck is null && work.Place.MissingGroupPermission is { Length: > 0 } permission)
            {
                failed["fix"] = VRChatGroupPermissions.Sentence(
                    new MissingGroupPermission(permission, groupId, null, null));
            }

            await _facts.RecordAsync(FactType.PlannedEventPublishFailed, work.Event, failed, ct: ct).ConfigureAwait(false);
        }

        return new CalendarPublishResult(outcome, work.Event.Id, work.Action);
    }

    /// <summary>
    /// A fact for each VRChat place that got onto VRChat's calendar for the first time or came off
    /// it in this pass, so the live stream tells the calendar page (added 2026-10-01). A failure
    /// already wrote its own. An edit writes none: a place that VRChat already held goes through
    /// waiting and back to published on every edit, and that is not "published" again. A place
    /// that VRChat never held has nothing to be taken down from.
    /// </summary>
    private async Task RecordChangesAsync(
        List<CalendarEvent> events,
        Dictionary<Guid, CalendarEventPlace> places,
        Dictionary<Guid, (string State, bool OnVRChat)> was,
        CancellationToken ct)
    {
        foreach (var calendarEvent in events)
        {
            if (!places.TryGetValue(calendarEvent.Id, out var place))
                continue;

            var (before, onVRChat) = was.TryGetValue(calendarEvent.Id, out var seen) ? seen : (null, false);
            if (place.State == before)
                continue;

            if (place.State == CalendarPlaceStates.Published && !onVRChat)
            {
                await _facts.RecordAsync(
                    FactType.PlannedEventPublished,
                    calendarEvent,
                    new JsonObject { ["place"] = CalendarPlaces.VRChat },
                    ct: ct).ConfigureAwait(false);
            }
            else if (place.State == CalendarPlaceStates.Removed && onVRChat)
            {
                await _facts.RecordAsync(
                    FactType.PlannedEventTakenDown,
                    calendarEvent,
                    new JsonObject { ["place"] = CalendarPlaces.VRChat, ["was"] = before },
                    ct: ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether an event belongs on VRChat's calendar right now.</summary>
    public static bool Wants(CalendarEvent calendarEvent) =>
        calendarEvent.PublishToVRChat
        && calendarEvent.DeletedAt is null
        && CalendarEventStates.IsLive(calendarEvent.State);

    // ── One date of a series (calendar design §2.2) ──────────────────────────────────────
    //
    // VRChat keeps a repeating event as a series, and lists each of its dates as an "occurrence"
    // with an id of its own and the series' id beside it. A date cancelled in Modbot is deleted on
    // VRChat by that date's own id, and a date moved or reworded is updated by it -- the same delete
    // and update calls the series uses, on the same calendar budget. The id is found by reading the
    // month the date is in before every write, never taken from an earlier pass: when the series is
    // written again VRChat makes its dates afresh with new ids, and a write to an old id answered
    // 200 and changed nothing (seen 2026-10-01), so a kept id cannot be trusted.
    //
    // Never sent to the series' own id: deleting that would take every date off VRChat. A date that
    // VRChat does not list apart from its series is shown as failed rather than guessed at.

    /// <summary>
    /// The first date changed on its own that VRChat has not been told about, of an event whose
    /// series VRChat already has as it should be.
    /// </summary>
    private static (CalendarEvent Event, CalendarEventPlace Place, CalendarDateChange Change, string Fingerprint)? DueDate(
        List<CalendarEvent> events, Dictionary<Guid, CalendarEventPlace> places, DateTimeOffset now)
    {
        foreach (var calendarEvent in events)
        {
            // A finished event is still on VRChat as history, and may have finished only because its
            // last date was cancelled: that date's delete still goes out.
            var kept = Wants(calendarEvent)
                || (calendarEvent.State == CalendarEventStates.Finished && calendarEvent.PublishToVRChat && calendarEvent.DeletedAt is null);

            if (!kept || calendarEvent.Repeat == CalendarRepeats.None || calendarEvent.DateChanges.Count == 0)
                continue;

            if (!places.TryGetValue(calendarEvent.Id, out var place)
                || place.ExternalId is null
                || place.State != CalendarPlaceStates.Published
                || place.SentFingerprint != CalendarVRChatRequests.Fingerprint(calendarEvent))
            {
                continue;
            }

            foreach (var change in calendarEvent.DateChanges.OrderBy(c => c.PlannedStartsAt))
            {
                if (IsOver(calendarEvent, change, now))
                    continue;

                var fingerprint = CalendarVRChatRequests.DateFingerprint(calendarEvent, change);

                if (change.VRChatSentFingerprint == fingerprint || DateHeld(change, fingerprint, now))
                    continue;

                // Still being changed: three quick fixes to one date are one write, as for a series.
                if (now - change.UpdatedAt < SettleFor)
                    continue;

                return (calendarEvent, place, change, fingerprint);
            }
        }

        return null;
    }

    /// <summary>A date that has ended, at its planned time and at its own: nothing to tell VRChat about it.</summary>
    private static bool IsOver(CalendarEvent calendarEvent, CalendarDateChange change, DateTimeOffset now) =>
        change.PlannedStartsAt + CalendarRepeat.LengthOf(calendarEvent) <= now
        && (change.EndsAt is not { } ends || ends <= now);

    /// <summary>A date's failure that should not be sent again yet, by the same rule as a series'.</summary>
    private static bool DateHeld(CalendarDateChange change, string fingerprint, DateTimeOffset now)
    {
        if (change.VRChatErrorAt is not { } at)
            return false;

        if (change.VRChatFailedFingerprint == fingerprint)
            return true;

        return change.VRChatFailedFingerprint is null && now - at < RetryUnansweredAfter;
    }

    private async Task<CalendarPublishOutcome> WriteDateAsync(
        CalendarEvent calendarEvent,
        CalendarEventPlace place,
        CalendarDateChange change,
        string fingerprint,
        string groupId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var seriesId = place.ExternalId!;
        string? id = null;

        // Looked up before every write, never sent to an id kept from an earlier pass (see above).
        {
            // Every page of the month the date is planned in, the neighbouring month when it is near
            // the edge (VRChat's month is not exactly the UTC month), and the month it was moved to:
            // read the way the calendar page reads them, until the date turns up.
            var months = CalendarVRChatReader.MonthsListing(change.PlannedStartsAt)
                .Concat(change.StartsAt is { } movedTo ? CalendarVRChatReader.MonthsListing(movedTo) : [])
                .Concat(change.VRChatSentStartsAt is { } sentTo ? CalendarVRChatReader.MonthsListing(sentTo) : [])
                .Distinct()
                .ToList();

            var whole = true;

            foreach (var month in months)
            {
                var look = await CalendarVRChatReader
                    .ReadMonthPagesAsync(_gate, groupId, month, VRChatCallPriority.Background, ct)
                    .ConfigureAwait(false);

                if (look.Failed is { } failed)
                {
                    if (failed.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
                        return CalendarPublishOutcome.RateLimited;

                    DateFailed(change, "Could not read VRChat's calendar: " + Reason(failed.Kind, failed.RawResponse, failed.ErrorMessage, failed.StatusCode), refused: null, now);
                    return CalendarPublishOutcome.Failed;
                }

                whole &= look.Whole;
                id = FindDate(look.Rows, seriesId, change);

                if (id is not null)
                    break;
            }

            // Not found is never taken as "nothing to do": a cancel would then be marked sent while
            // the date is still on VRChat. It shows on the date instead, and is not looked for again
            // until the date changes.
            if (id is null)
            {
                DateFailed(
                    change,
                    whole
                        ? "Could not find this date on VRChat's calendar."
                        : "Could not find this date on VRChat's calendar: that month has more events than Modbot reads at once.",
                    refused: fingerprint,
                    now);
                return CalendarPublishOutcome.Failed;
            }
        }

        int status;
        bool success;
        string? error;
        string? body;
        VRChatFailureKind kind;
        DateTimeOffset? answeredUpdatedAt = null;
        var dateId = id;

        if (change.Cancelled)
        {
            var result = await _gate.ExecuteAsync(
                new VRChatEndpoint(VRChatEndpointClass.CalendarWrite, groupId, "DeleteGroupCalendarEvent"),
                (client, token) => client.Calendar.DeleteGroupCalendarEventWithHttpInfoAsync(groupId, dateId, token),
                VRChatCallPriority.Background,
                ct).ConfigureAwait(false);

            (status, success, error, body, kind) = (result.StatusCode, result.Success, result.ErrorMessage, result.RawResponse, result.Kind);

            // Already gone is what a cancel wanted: the id was found in this very pass.
            if (status == 404)
                success = true;
        }
        else
        {
            var request = CalendarVRChatRequests.UpdateDate(calendarEvent, change);
            var result = await _gate.ExecuteAsync(
                new VRChatEndpoint(VRChatEndpointClass.CalendarWrite, groupId, "UpdateGroupCalendarEvent"),
                (client, token) => client.Calendar.UpdateGroupCalendarEventWithHttpInfoAsync(groupId, dateId, request, token),
                VRChatCallPriority.Background,
                ct).ConfigureAwait(false);

            (status, success, error, body, kind) = (result.StatusCode, result.Success, result.ErrorMessage, result.RawResponse, result.Kind);
            answeredUpdatedAt = CalendarVRChatCopy.UpdatedAt(result.Value);

            // A 404 for an id found in this very pass is a failure like any other, shown on the
            // date: it is not looked for again until the date changes, so the two cannot go round
            // in a loop.
        }

        if (success)
        {
            change.VRChatId = dateId;
            DateSent(change, fingerprint);

            // Where VRChat now has the date, so a later look for it can find it there even after
            // the date is put back as planned and its own times are gone from the row.
            if (!change.Cancelled)
                change.VRChatSentStartsAt = CalendarRepeat.Changed(change, CalendarRepeat.LengthOf(calendarEvent)).StartsAt;

            // A date put back as planned was kept only until VRChat had the planned date back.
            if (CalendarDates.IsPlain(change, CalendarRepeat.LengthOf(calendarEvent)))
                calendarEvent.DateChanges.Remove(change);

            // The series' dates carry this write's time now; a read of the calendar should not take
            // it for a change made on VRChat.
            if (answeredUpdatedAt is { } updatedAt && (place.VRChatUpdatedAt is null || updatedAt > place.VRChatUpdatedAt))
                place.VRChatUpdatedAt = updatedAt;

            _log.Information(
                "VRChat calendar {Action} for one date of the event {EventId}",
                change.Cancelled ? "delete" : "update", calendarEvent.Id);

            return CalendarPublishOutcome.Written;
        }

        if (kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
        {
            // Never retried here: the limiter decides when anything is sent again.
            return CalendarPublishOutcome.RateLimited;
        }

        DateFailed(change, Reason(kind, body, error, status), refused: status == 0 || status >= 500 ? null : fingerprint, now);

        _log.Warning(
            "VRChat calendar write for one date of the event {EventId} failed: {Status} {Reason}",
            calendarEvent.Id, status, change.VRChatError);

        return CalendarPublishOutcome.Failed;
    }

    /// <summary>
    /// VRChat's own id for the date: the one listed with the series' id beside it, at the date's
    /// planned start, or at the time it was moved to when VRChat has it there already.
    /// </summary>
    internal static string? FindDate(IEnumerable<VRChatCalendarEvent> rows, string seriesId, CalendarDateChange change)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(change);

        var dates = rows
            .Where(r => r.Id is { Length: > 0 }
                && r.DeletedAt is null
                && r.OccurrenceKind == CalendarEventOccurrenceKind.Occurrence
                && string.Equals(r.SeriesId, seriesId, StringComparison.Ordinal)
                && !string.Equals(r.Id, seriesId, StringComparison.Ordinal))
            .ToList();

        if (change.VRChatId is { Length: > 0 } known && dates.FirstOrDefault(r => r.Id == known) is { } listed)
            return listed.Id;

        var planned = dates.FirstOrDefault(r => SameTime(r.StartsAt, change.PlannedStartsAt));
        if (planned is not null)
            return planned.Id;

        // Where it was moved to, and where Modbot last sent it: a date put back as planned has no
        // times of its own any more, but VRChat still has it where the last write put it.
        foreach (var at in new[] { change.StartsAt, change.VRChatSentStartsAt })
        {
            if (at is { } time && dates.FirstOrDefault(r => SameTime(r.StartsAt, time)) is { } found)
                return found.Id;
        }

        return null;
    }

    private static bool SameTime(DateTime vrchat, DateTimeOffset modbot) =>
        Math.Abs((AsUtc(vrchat) - modbot.UtcDateTime).TotalSeconds) < 1;

    private static void DateSent(CalendarDateChange change, string fingerprint)
    {
        change.VRChatSentFingerprint = fingerprint;
        change.VRChatFailedFingerprint = null;
        change.VRChatError = null;
        change.VRChatErrorAt = null;
    }

    /// <param name="refused">The fingerprint VRChat refused, not sent again until it changes; null tries again later.</param>
    private static void DateFailed(CalendarDateChange change, string error, string? refused, DateTimeOffset now)
    {
        change.VRChatError = Trim(error);
        change.VRChatErrorAt = now;
        change.VRChatFailedFingerprint = refused;
    }

    /// <summary>
    /// A create was sent and got no answer, and nothing has been confirmed since, so the event may
    /// be on VRChat's calendar without Modbot knowing its id. <see cref="CalendarEventPlace.ErrorAt"/>
    /// is when that create was sent.
    /// </summary>
    internal static bool MayHaveBeenCreated(CalendarEventPlace place) =>
        place.ExternalId is null && place.ErrorAt is not null && place.FailedFingerprint is null;

    /// <summary>
    /// A create that got no answer and was not on VRChat's calendar either: it waits for a
    /// moderator's Try again. <see cref="CalendarEventPlace.ErrorAt"/> is still when it was sent.
    /// </summary>
    public static bool NotAdded(CalendarEventPlace place)
    {
        ArgumentNullException.ThrowIfNull(place);

        return place.Place == CalendarPlaces.VRChat
            && place.ExternalId is null
            && place.State == CalendarPlaceStates.Failed
            && place.FailedFingerprint == NotAddedFingerprint;
    }

    /// <summary>
    /// A moderator's Try again on a create that was not added: the next pass looks at the calendar
    /// once more, takes the copy if it has turned up, and otherwise sends the create. False,
    /// changing nothing, for a place in any other state.
    /// </summary>
    /// <remarks>
    /// When the first create was sent, and what it sent, are kept for that look.
    /// </remarks>
    public static bool TryAgain(CalendarEventPlace place, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(place);

        if (!NotAdded(place))
            return false;

        place.State = CalendarPlaceStates.Waiting;
        place.FailedFingerprint = TryAgainFingerprint;
        place.Error = null;
        place.MissingGroupPermission = null;
        place.UpdatedAt = now;
        return true;
    }

    /// <summary>A moderator pressed Try again, and the look before the create has not run yet.</summary>
    internal static bool TryingAgain(CalendarEventPlace place) =>
        place.ExternalId is null
        && place.ErrorAt is not null
        && place.FailedFingerprint == TryAgainFingerprint;

    /// <summary>
    /// Whether the calendar is read for a create's copy this pass: <see cref="LookAfter"/> after
    /// the create, then <see cref="RetryUnansweredAfter"/> after a look that could not be made.
    /// </summary>
    private static bool LookDue(CalendarEventPlace place, DateTimeOffset now)
    {
        var sentAt = place.ErrorAt!.Value;

        return place.UpdatedAt > sentAt
            ? now - place.UpdatedAt >= RetryUnansweredAfter
            : now - sentAt >= LookAfter;
    }

    /// <summary>A failure that should not be sent again yet.</summary>
    private static bool Held(CalendarEventPlace place, string fingerprint, DateTimeOffset now)
    {
        if (place.State != CalendarPlaceStates.Failed)
            return false;

        // Refused: not again until what would be sent changes.
        if (place.FailedFingerprint == fingerprint)
            return true;

        // No answer: again after a while, not every pass.
        return place.FailedFingerprint is null
            && place.ErrorAt is { } at
            && now - at < RetryUnansweredAfter;
    }

    private async Task<CalendarPublishOutcome> WriteAsync(
        CalendarEvent calendarEvent,
        CalendarEventPlace place,
        string action,
        string fingerprint,
        string groupId,
        Settings settings,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var endpoint = new VRChatEndpoint(VRChatEndpointClass.CalendarWrite, groupId, action switch
        {
            "create" => "CreateGroupCalendarEvent",
            "update" => "UpdateGroupCalendarEvent",
            _ => "DeleteGroupCalendarEvent",
        });

        int status;
        bool success;
        string? error;
        string? body;
        VRChatFailureKind kind;
        string? createdId = null;
        string? foundId = null;
        DateTimeOffset? answeredUpdatedAt = null;

        if (MayHaveBeenCreated(place))
        {
            // Looked for as it was sent: VRChat's copy has none of the edits made since.
            var look = await LookForEarlierCreateAsync(
                CalendarVRChatMatch.AsSent(place, calendarEvent), place.ErrorAt!.Value, groupId, ct).ConfigureAwait(false);

            if (look.Failed is { } failed)
                return CouldNotLook(calendarEvent, place, action, failed, now);

            foundId = look.FoundId;

            if (foundId is not null && action == "create")
            {
                // Made after all: taken as this event's own. An edit made since goes out as an update.
                CalendarVRChatMatch.Adopt(place, calendarEvent, foundId, look.UpdatedAt, now);

                _log.Information(
                    "VRChat calendar create for the event {EventId} had gone through after all as {VRChatEventId}",
                    calendarEvent.Id, foundId);

                return CalendarPublishOutcome.NothingToDo;
            }

            if (foundId is null && !look.Whole)
            {
                // Too many events that month to read them all: nothing is decided, and the look is
                // made again later.
                place.UpdatedAt = now;
                return CalendarPublishOutcome.NothingToDo;
            }

            if (foundId is null && action == "create")
            {
                // Not made. Never sent again on its own: a moderator's Try again sends it.
                place.State = CalendarPlaceStates.Failed;
                place.FailedFingerprint = NotAddedFingerprint;
                place.Error = NotAddedError;
                place.MissingGroupPermission = null;
                place.UpdatedAt = now;

                _log.Warning(
                    "VRChat calendar create for the event {EventId} got no answer and is not on VRChat's calendar",
                    calendarEvent.Id);

                return CalendarPublishOutcome.Failed;
            }

            if (foundId is null && action == "delete")
            {
                // Never made: nothing to take off.
                place.State = CalendarPlaceStates.Removed;
                place.FailedFingerprint = null;
                place.Error = null;
                place.MissingGroupPermission = null;
                place.ErrorAt = null;
                place.CreateSent = null;
                place.UpdatedAt = now;
                return CalendarPublishOutcome.NothingToDo;
            }
        }

        if (TryingAgain(place) && action == "create")
        {
            // One more look before a moderator's Try again sends the create: the copy may have
            // turned up since. A look that fails goes back to waiting for the copy, as after a
            // create with no answer; nothing is sent without it.
            var look = await LookForEarlierCreateAsync(
                CalendarVRChatMatch.AsSent(place, calendarEvent), place.ErrorAt!.Value, groupId, ct).ConfigureAwait(false);

            if (look.Failed is { } failed)
            {
                // A rate limit keeps the Try again for the next pass; the limiter decides when.
                if (failed.Kind is not (VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting))
                    place.FailedFingerprint = null;

                return CouldNotLook(calendarEvent, place, action, failed, now);
            }

            if (look.FoundId is { } lateId)
            {
                CalendarVRChatMatch.Adopt(place, calendarEvent, lateId, look.UpdatedAt, now);

                _log.Information(
                    "VRChat calendar create for the event {EventId} was found as {VRChatEventId} before it was tried again",
                    calendarEvent.Id, lateId);

                return CalendarPublishOutcome.NothingToDo;
            }
        }

        switch (action)
        {
            case "create":
            {
                var request = CalendarVRChatRequests.Create(calendarEvent);
                var result = await _gate.ExecuteAsync(
                    endpoint,
                    (client, token) => client.Calendar.CreateGroupCalendarEventWithHttpInfoAsync(groupId, request, token),
                    VRChatCallPriority.Background,
                    ct).ConfigureAwait(false);

                (status, success, error, body, kind) = (result.StatusCode, result.Success, result.ErrorMessage, result.RawResponse, result.Kind);
                createdId = result.Value?.Id;
                answeredUpdatedAt = CalendarVRChatCopy.UpdatedAt(result.Value);

                if (success && string.IsNullOrEmpty(createdId))
                {
                    success = false;
                    error = "VRChat created the event but did not say its id.";
                }

                break;
            }

            case "update":
            {
                var request = CalendarVRChatRequests.Update(calendarEvent);
                var id = place.ExternalId!;
                var result = await _gate.ExecuteAsync(
                    endpoint,
                    (client, token) => client.Calendar.UpdateGroupCalendarEventWithHttpInfoAsync(groupId, id, request, token),
                    VRChatCallPriority.Background,
                    ct).ConfigureAwait(false);

                (status, success, error, body, kind) = (result.StatusCode, result.Success, result.ErrorMessage, result.RawResponse, result.Kind);
                answeredUpdatedAt = CalendarVRChatCopy.UpdatedAt(result.Value);

                // Deleted on VRChat's side while an edit made in Modbot was waiting to go out: the
                // edit is the newer of the two, so the event is made again (calendar design §12.3).
                if (status == 404)
                {
                    place.ExternalId = null;
                    place.SentFingerprint = null;
                    place.VRChatUpdatedAt = null;
                    place.ErrorAt = null;
                    place.CreateSent = null;
                    place.State = CalendarPlaceStates.Waiting;
                    place.UpdatedAt = now;
                    return CalendarPublishOutcome.NothingToDo;
                }

                break;
            }

            default:
            {
                var id = place.ExternalId ?? foundId!;
                var result = await _gate.ExecuteAsync(
                    endpoint,
                    (client, token) => client.Calendar.DeleteGroupCalendarEventWithHttpInfoAsync(groupId, id, token),
                    VRChatCallPriority.Background,
                    ct).ConfigureAwait(false);

                (status, success, error, body, kind) = (result.StatusCode, result.Success, result.ErrorMessage, result.RawResponse, result.Kind);

                // Already gone is what a delete wanted.
                if (status == 404)
                    success = true;

                break;
            }
        }

        place.UpdatedAt = now;

        if (success)
        {
            if (action == "delete")
            {
                place.ExternalId = null;
                place.SentFingerprint = null;
                place.State = CalendarPlaceStates.Removed;
                place.VRChatUpdatedAt = null;
            }
            else
            {
                place.ExternalId = createdId ?? place.ExternalId;
                place.SentFingerprint = fingerprint;
                place.State = CalendarPlaceStates.Published;

                // What VRChat says now is Modbot's own write, so a read of the calendar does not take
                // it for a change made there. Null when VRChat did not say; the next read starts from
                // whatever it finds.
                place.VRChatUpdatedAt = answeredUpdatedAt;

                // A series made or written again may not hold the dates changed on their own any
                // more, and whether VRChat keeps them through an update is not known: each one still
                // to come is looked for again and sent again. A cancelled date before where the
                // series now starts is not in it, so there is nothing to take off.
                var seriesStartsAt = CalendarVRChatRequests.SeriesStartsAt(calendarEvent);

                foreach (var change in calendarEvent.DateChanges.Where(c => !IsOver(calendarEvent, c, now)))
                {
                    // VRChat makes the series' dates afresh, with new ids, and puts a moved date back
                    // at its planned time (seen 2026-10-01): every changed date -- cancelled, moved,
                    // reworded or put back -- is looked for again and sent again. Where it was last
                    // sent no longer holds either.
                    change.VRChatId = null;
                    change.VRChatSentStartsAt = null;

                    change.VRChatSentFingerprint = change.Cancelled && change.PlannedStartsAt < seriesStartsAt
                        ? CalendarVRChatRequests.DateFingerprint(calendarEvent, change)
                        : null;
                    change.VRChatFailedFingerprint = null;
                    change.VRChatError = null;
                    change.VRChatErrorAt = null;
                }
            }

            place.FailedFingerprint = null;
            place.Error = null;
            place.MissingGroupPermission = null;
            place.ErrorAt = null;
            place.CreateSent = null;

            _log.Information("VRChat calendar {Action} for the event {EventId}", action, calendarEvent.Id);
            return CalendarPublishOutcome.Written;
        }

        if (kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
        {
            // Not an error and never retried here: the limiter decides when anything is sent again.
            place.State = CalendarPlaceStates.Waiting;
            _log.Information(
                "VRChat calendar {Action} for the event {EventId} is waiting: {Reason}",
                action, calendarEvent.Id, error ?? "the calendar bucket is stopped");

            return CalendarPublishOutcome.RateLimited;
        }

        // A create with no answer, VRChat's own trouble, or an answer with no id: the event may be
        // on VRChat's calendar. The place waits, what was sent is kept, and the calendar is read for
        // it before anything else is sent (calendar design §3.1). No failure fact yet: nothing is
        // known to have failed.
        if (action == "create" && (status == 0 || status >= 500 || status is >= 200 and < 300))
        {
            place.State = CalendarPlaceStates.Waiting;
            place.Error = null;
            place.ErrorAt = now;
            place.FailedFingerprint = null;
            place.MissingGroupPermission = null;
            place.CreateSent = CalendarVRChatMatch.Remember(calendarEvent);
            _waitingToLook = true;

            _log.Warning(
                "VRChat calendar create for the event {EventId} got no answer: {Status} {Reason}; its calendar is read for it before anything else is sent",
                calendarEvent.Id, status, Trim(Reason(kind, body, error, status)));

            return CalendarPublishOutcome.Failed;
        }

        if (action == "create")
            place.CreateSent = null;

        place.State = CalendarPlaceStates.Failed;
        place.Error = Trim(Reason(kind, body, error, status));
        place.ErrorAt = now;

        // A 403 because Modbot's VRChat account lacks Manage Group Calendar: kept apart from the
        // text, so the page can say which permission and link to where it is given.
        place.MissingGroupPermission =
            VRChatGroupPermissions.Refusal(status, kind, endpoint.Operation, groupId, body, settings)?.Permission;

        // Featuring an event may be more than the group or Modbot's account is allowed, and what
        // VRChat answers then is not known (calendar repeats and VRChat settings design §6).
        // Saying it was on names the one switch to try without.
        if (calendarEvent.Featured && action != "delete" && place.MissingGroupPermission is null && status is >= 400 and < 500)
            place.Error = Trim($"{place.Error} (sent with Featured on)");

        // Nothing came back, or VRChat's own trouble: try again later. Anything else is a refusal of
        // this content, and sending it again would get the same answer.
        place.FailedFingerprint = status == 0 || status >= 500 ? null : fingerprint;

        _log.Warning(
            "VRChat calendar {Action} for the event {EventId} failed: {Status} {Reason}",
            action, calendarEvent.Id, status, place.Error);

        return CalendarPublishOutcome.Failed;
    }

    /// <summary>
    /// Reads every page of the group's calendar for the month the event's current time starts in
    /// (and the month beside it, when the start is near the edge: VRChat's month is not exactly the
    /// UTC month), for the copy a create sent at <paramref name="sentAt"/> made that no other Modbot
    /// event already owns.
    /// </summary>
    /// <returns>
    /// VRChat's answer when a read failed; the id found (a repeating event's series id), with
    /// VRChat's <c>updatedAt</c> for it; and whether every month was read to its end.
    /// </returns>
    private async Task<(VRChatResult<PaginatedCalendarEventList>? Failed, string? FoundId, DateTimeOffset? UpdatedAt, bool Whole)> LookForEarlierCreateAsync(
        CalendarEvent calendarEvent, DateTimeOffset sentAt, string groupId, CancellationToken ct)
    {
        var starts = calendarEvent.OccurrenceStartsAt ?? calendarEvent.StartsAt;
        var months = new[] { starts - CalendarVRChatReader.MonthEdge, starts, starts + CalendarVRChatReader.MonthEdge }
            .Select(CalendarVRChatReader.MonthOf)
            .Distinct()
            .ToList();

        var rows = new List<global::VRChat.API.Model.CalendarEvent>();
        var whole = true;

        foreach (var month in months)
        {
            var read = await CalendarVRChatReader.ReadMonthPagesAsync(_gate, groupId, month, VRChatCallPriority.Background, ct)
                .ConfigureAwait(false);

            if (read.Failed is { } failed)
                return (failed, null, null, false);

            rows.AddRange(read.Rows);
            whole &= read.Whole;
        }

        var owned = await _db.CalendarEventPlaces
            .Where(p => p.Place == CalendarPlaces.VRChat && p.ExternalId != null)
            .Select(p => p.ExternalId!)
            .ToListAsync(ct).ConfigureAwait(false);

        var found = rows.FirstOrDefault(r => CalendarVRChatMatch.IsCopyOf(r, calendarEvent, sentAt)
            && !owned.Contains(CalendarVRChatReader.KeyOf(r))
            && !owned.Contains(r.Id));

        return found is null
            ? (null, null, null, whole)
            : (null, CalendarVRChatReader.KeyOf(found), CalendarVRChatCopy.UpdatedAt(found), whole);
    }

    /// <summary>The look before a retry could not be made. Nothing is written without it.</summary>
    private CalendarPublishOutcome CouldNotLook(
        CalendarEvent calendarEvent,
        CalendarEventPlace place,
        string action,
        VRChatResult<PaginatedCalendarEventList> result,
        DateTimeOffset now)
    {
        // Asked again on the next pass: the limiter refuses without sending until its own wait is over.
        if (result.Kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting)
        {
            place.State = CalendarPlaceStates.Waiting;
            return CalendarPublishOutcome.RateLimited;
        }

        // Kept as "no answer", so the look is made again later rather than skipped. The time the
        // create was sent is kept too: a copy must have been made after it.
        place.UpdatedAt = now;
        place.State = CalendarPlaceStates.Failed;
        place.FailedFingerprint = null;
        _couldNotCheck = Trim(Reason(result.Kind, result.RawResponse, result.ErrorMessage, result.StatusCode));
        place.Error = Trim("Could not check VRChat's calendar for an earlier copy: " + _couldNotCheck);
        place.MissingGroupPermission = null;

        _log.Warning(
            "VRChat calendar {Action} for the event {EventId} is held: {Status} {Reason}",
            action, calendarEvent.Id, result.StatusCode, place.Error);

        return CalendarPublishOutcome.Failed;
    }

    /// <summary>VRChat's own words when it gave any, otherwise the gate's.</summary>
    private static string Reason(VRChatFailureKind kind, string? body, string? error, int status) =>
        (kind == VRChatFailureKind.WafBlocked ? null : VRChatRefusal.MessageOf(body))
        ?? error
        ?? $"VRChat answered {status}.";

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    private static string Trim(string text) => text.Length <= 1024 ? text : text[..1023] + "…";
}
