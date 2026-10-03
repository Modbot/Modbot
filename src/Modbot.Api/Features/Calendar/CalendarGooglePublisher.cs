using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Google;
using Modbot.Core.Logging;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.VRChat.Calendar;
using Serilog;

namespace Modbot.Api.Features.Calendar;

public enum CalendarGoogleOutcome
{
    /// <summary>Google is not set up, or Sending is off and nothing is being removed.</summary>
    Off = 1,

    /// <summary>Everything is as it should be, or waiting for edits to settle.</summary>
    NothingToDo = 2,

    /// <summary>At least one write went through.</summary>
    Written = 3,

    /// <summary>Google is limiting Modbot, or the lane stopped this pass. Nothing was retried.</summary>
    Stopped = 4,

    /// <summary>Google refused something, and nothing went through.</summary>
    Failed = 5,
}

/// <param name="Calls">How many requests went to Google's Calendar API in the pass.</param>
public sealed record CalendarGoogleResult(CalendarGoogleOutcome Outcome, int Calls = 0);

/// <summary>
/// Keeps the Google calendar in Settings in line with the events planned in Modbot (Google Calendar
/// design §3.3 to §3.7, step 2). One pass every twenty seconds, from <see cref="CalendarGoogleService"/>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>At most once.</strong> Modbot picks the Google event id (<see cref="GoogleEventIds"/>) and
/// writes it on the place before the first insert. A place with an id and nothing confirmed sent
/// (<see cref="CalendarEventPlace.SentFingerprint"/> null) is always read back by that id before any
/// insert, at least <see cref="NoAnswerWait"/> after the insert that got no answer: Google says it
/// cannot promise to catch a repeated id, so its 409 alone is not trusted. Read back and found as
/// Modbot's own, it is taken as this event's; not found, the same id is inserted again.
/// </para>
/// <para>
/// <strong>A rate limit is never retried</strong> (CLAUDE.md). A 429, or a 403 for a limit, stops
/// every call to Google until <see cref="Modbot.Core.Data.Entities.Settings.GoogleStoppedUntil"/> (15 minutes, longer when
/// Google asks, six hours for the use limits); the places wait; the pass after that makes one call
/// as a probe. A refused key, a wrong clock, or a calendar no longer shared to change is written as
/// the Check's problem, which stops the lane until a new key or a good Check.
/// </para>
/// <para>
/// <strong>Quick edits fold into one write</strong>: an event is written once it has gone
/// <see cref="SettleFor"/> without a change. At most <see cref="MostCallsAPass"/> calls a pass, far
/// under Google's 600 a minute.
/// </para>
/// <para>
/// <strong>One date of a series</strong> (§3.5) is changed on Google's own copy of that date, found
/// by its planned start (<c>events.instances</c>) before every write. After the series is written
/// again, every date Google may hold as changed is sent again, because whether Google keeps them is
/// not known. A date put back as planned keeps its row until Google has it back; the row goes only
/// when no place holds it as changed (<see cref="CalendarDates.CanForget"/>).
/// </para>
/// <para>
/// Changes made on Google are never read back: Modbot's next write replaces them.
/// </para>
/// </remarks>
public sealed class CalendarGooglePublisher
{
    public static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long after a call with no answer (a timeout, or Google's own 5xx) the place is tried
    /// again: an insert is then read back first.
    /// </summary>
    public static readonly TimeSpan NoAnswerWait = TimeSpan.FromMinutes(1);

    /// <summary>The most requests to the Calendar API in one pass.</summary>
    public const int MostCallsAPass = 10;

    /// <summary>
    /// The store marker for an event found on Google as Modbot's own after an insert with no answer:
    /// what it says is not known, so it is written once more.
    /// </summary>
    public const string AdoptedFingerprint = "adopted";

    /// <summary>The store marker for a date Google may hold as changed, to be sent again after a series write.</summary>
    public const string AgainFingerprint = "again";

    /// <summary>The fingerprint a refused delete is held under.</summary>
    public const string DeleteFingerprint = "delete";

    /// <summary>
    /// The store marker for a refusal because Modbot may not change events on the calendar: held
    /// until the next good Check, not until the event changes (§3.7).
    /// </summary>
    public const string NotAllowedFingerprint = "not-allowed";

    /// <summary>What a place says when the id Modbot picked is somebody else's event.</summary>
    public const string TakenError = "The calendar already has another event with this id.";

    /// <summary>What a date says when Google's copy of the series has no such date.</summary>
    public const string DateNotFoundError = "Could not find this date on Google.";

    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly ISecretProtector _protector;
    private readonly GoogleSignIn _signIn;
    private readonly GoogleCalendarClient _google;
    private readonly CalendarFacts _facts;
    private readonly ILogger _log;

    // ── One pass ─────────────────────────────────────────────────────────────────────────
    private Modbot.Core.Data.Entities.Settings _settings = null!;
    private GoogleCredentials _key = null!;
    private string _calendarId = string.Empty;
    private IReadOnlyDictionary<string, string> _worldNames = new Dictionary<string, string>();
    private string? _groupName;
    private DateTimeOffset _now;
    private string? _token;
    private bool _refreshedToken;
    private int _calls;
    private int _budget;
    private bool _stopped;
    private bool _wrote;
    private readonly List<(CalendarEvent Event, JsonObject Data)> _failures = [];

    /// <summary>
    /// Events whose insert was sent again after a read-back found nothing, this pass. Once a pass
    /// each: a 409 then a 404 again waits for the next pass rather than going round.
    /// </summary>
    private readonly HashSet<Guid> _postedAfterReadBack = [];

    /// <summary>Events made anew with the next id this pass, after Google had deleted them. Once a pass each.</summary>
    private readonly HashSet<Guid> _madeAgain = [];

    /// <summary>Whether the calendar in Settings answered a read this pass: null until asked.</summary>
    private bool? _calendarThere;

    public CalendarGooglePublisher(
        ModbotContext db,
        IModbotClock clock,
        ISecretProtector protector,
        GoogleSignIn signIn,
        GoogleCalendarClient google,
        CalendarFacts facts,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(signIn);
        ArgumentNullException.ThrowIfNull(google);
        ArgumentNullException.ThrowIfNull(facts);

        _db = db;
        _clock = clock;
        _protector = protector;
        _signIn = signIn;
        _google = google;
        _facts = facts;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<CalendarGoogleResult> RunOnceAsync(CancellationToken ct = default)
    {
        _settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        var sending = CalendarGoogle.Ready(_settings);
        var removing = _settings.GoogleRemovingEvents && CalendarGoogle.SetUp(_settings);

        if (!sending && !removing)
            return new CalendarGoogleResult(CalendarGoogleOutcome.Off);

        _now = _clock.UtcNow;

        // Cold stop: nothing of any kind goes to Google before then.
        if (_settings.GoogleStoppedUntil is { } until && _now < until)
            return new CalendarGoogleResult(CalendarGoogleOutcome.Stopped);

        // The first pass after a stop is one call, as a probe.
        _budget = _settings.GoogleStoppedUntil is null ? MostCallsAPass : 1;

        if (_protector.Unprotect(_settings.GooglePrivateKeyEncrypted) is not { } pem)
        {
            LaneProblem(GoogleErrors.KeyNotAccepted);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new CalendarGoogleResult(CalendarGoogleOutcome.Failed);
        }

        _key = new GoogleCredentials(_settings.GoogleClientEmail!, _settings.GoogleKeyId!, pem);
        _calendarId = _settings.GoogleCalendarId!;
        _groupName = CalendarEndpoints.FeedName(_settings);

        var places = await _db.CalendarEventPlaces
            .Where(p => p.Place == CalendarPlaces.Google)
            .ToDictionaryAsync(p => p.EventId, ct).ConfigureAwait(false);

        var activeIds = places.Values.Where(p => p.State != CalendarPlaceStates.Removed).Select(p => p.EventId).ToList();

        var events = await _db.CalendarEvents
            .Where(e => activeIds.Contains(e.Id)
                || (!removing
                    && e.PublishToGoogle
                    && e.DeletedAt == null
                    && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open)))
            .OrderBy(e => e.UpdatedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        _worldNames = await CalendarEndpoints.FeedWorldNamesAsync(_db, events, ct).ConfigureAwait(false);

        // What each place said before this pass, so a change of state writes its fact.
        var was = places.ToDictionary(p => p.Key, p => (p.Value.State, OnGoogle: p.Value.SentFingerprint is not null));

        foreach (var calendarEvent in events)
        {
            if (OutOfCalls)
                break;

            var wants = removing ? CalendarGoogleWants.Nothing : CalendarGoogle.WantsOf(calendarEvent, _now);

            if (!places.TryGetValue(calendarEvent.Id, out var place))
            {
                if (wants != CalendarGoogleWants.There)
                    continue;

                place = new CalendarEventPlace
                {
                    EventId = calendarEvent.Id,
                    Place = CalendarPlaces.Google,
                    State = CalendarPlaceStates.Waiting,
                    UpdatedAt = _now,
                };

                _db.CalendarEventPlaces.Add(place);
                places[calendarEvent.Id] = place;
            }

            await StepAsync(calendarEvent, place, wants, ct).ConfigureAwait(false);
        }

        // The series are as they should be: the dates changed on their own are next.
        foreach (var calendarEvent in events)
        {
            if (OutOfCalls || removing)
                break;

            if (places.TryGetValue(calendarEvent.Id, out var place))
                await DatesAsync(calendarEvent, place, ct).ConfigureAwait(false);
        }

        // Remove Modbot's events is done once nothing of Modbot's is left on Google.
        if (removing && places.Values.All(p => p.State == CalendarPlaceStates.Removed))
        {
            _settings.GoogleRemovingEvents = false;
            _log.Information("Every event Modbot put on the Google calendar is removed");
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RecordFactsAsync(events, places, was, ct).ConfigureAwait(false);

        var outcome = _wrote ? CalendarGoogleOutcome.Written
            : _stopped ? CalendarGoogleOutcome.Stopped
            : _failures.Count > 0 ? CalendarGoogleOutcome.Failed
            : CalendarGoogleOutcome.NothingToDo;

        return new CalendarGoogleResult(outcome, _calls);
    }

    private bool OutOfCalls => _stopped || _calls >= _budget;

    // ── The event itself (§3.4, §3.6) ────────────────────────────────────────────────────

    private async Task StepAsync(CalendarEvent calendarEvent, CalendarEventPlace place, CalendarGoogleWants wants, CancellationToken ct)
    {
        // Written to the calendar Settings named before: taken off there first, then made anew.
        if (place.State != CalendarPlaceStates.Removed
            && place.ExternalId is not null
            && place.GoogleCalendarId is { } old
            && !string.Equals(old, _calendarId, StringComparison.Ordinal))
        {
            await MoveOffAsync(calendarEvent, place, old, ct).ConfigureAwait(false);
            return;
        }

        if (wants == CalendarGoogleWants.Nothing)
        {
            await TakeDownAsync(calendarEvent, place, ct).ConfigureAwait(false);
            return;
        }

        var body = CalendarGoogleBody.For(calendarEvent, _worldNames, _groupName);
        var fingerprint = body.Fingerprint(_calendarId);

        // Taken down before, and wanted again: made again with the next id.
        if (place.State == CalendarPlaceStates.Removed)
        {
            if (wants == CalendarGoogleWants.There && !Settling(calendarEvent))
                await InsertAsync(calendarEvent, place, body, fingerprint, nextTurn: true, ct).ConfigureAwait(false);

            return;
        }

        // Never sent.
        if (place.ExternalId is null)
        {
            if (wants == CalendarGoogleWants.KeptOnly)
            {
                MarkRemoved(calendarEvent, place);
                return;
            }

            if (!Held(place, fingerprint) && !Settling(calendarEvent))
                await InsertAsync(calendarEvent, place, body, fingerprint, nextTurn: false, ct).ConfigureAwait(false);

            return;
        }

        // An id was written and nothing confirmed: read back before anything else (§3.4).
        if (place.SentFingerprint is null)
        {
            if (!Held(place, fingerprint))
                await ReadBackAsync(calendarEvent, place, body, fingerprint, wants, ct).ConfigureAwait(false);

            return;
        }

        if (place.SentFingerprint == fingerprint)
        {
            // Google has this already. A failure of a later version, cleared by an edit back, is over.
            if (place.State != CalendarPlaceStates.Published)
                Published(calendarEvent, place, fingerprint, fresh: false);

            return;
        }

        if (Held(place, fingerprint))
            return;

        if (place.State != CalendarPlaceStates.Waiting)
        {
            place.State = CalendarPlaceStates.Waiting;
            place.UpdatedAt = _now;
        }

        if (place.SentFingerprint != AdoptedFingerprint && Settling(calendarEvent))
            return;

        await UpdateAsync(calendarEvent, place, body, fingerprint, wants, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The id is written on the place, and saved, before the insert goes out: whatever happens to
    /// the answer, the next pass knows which id to read back.
    /// </summary>
    private async Task InsertAsync(
        CalendarEvent calendarEvent, CalendarEventPlace place, CalendarGoogleBody body, string fingerprint, bool nextTurn, CancellationToken ct)
    {
        // The token first: a pass whose last call goes on a token request chooses no id, so the
        // insert is not left waiting to be read back for nothing.
        if (OutOfCalls || await TokenAsync(fresh: false, ct).ConfigureAwait(false) is null || OutOfCalls)
            return;

        place.ExternalId = nextTurn || place.ExternalId is not null
            ? GoogleEventIds.Next(calendarEvent.Id, place.ExternalId)
            : GoogleEventIds.For(calendarEvent.Id, 0);

        place.GoogleCalendarId = _calendarId;
        place.SentFingerprint = null;
        place.State = CalendarPlaceStates.Waiting;
        place.FailedFingerprint = null;
        place.Error = null;
        place.ErrorAt = _now;
        place.UpdatedAt = _now;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        await PostAsync(calendarEvent, place, body, fingerprint, ct).ConfigureAwait(false);
    }

    private async Task PostAsync(
        CalendarEvent calendarEvent, CalendarEventPlace place, CalendarGoogleBody body, string fingerprint, CancellationToken ct)
    {
        var id = place.ExternalId!;
        var answer = await CallAsync(token => _google.InsertEventAsync(token, _calendarId, body.ToJson(id), ct), ct).ConfigureAwait(false);

        if (answer is not { } result)
            return;

        if (result.Value is not null)
        {
            Published(calendarEvent, place, fingerprint, fresh: true);
            _log.Information("Google Calendar insert for the event {EventId} as {GoogleEventId}", calendarEvent.Id, id);
            return;
        }

        var failure = result.Failure!;

        switch (failure.Problem)
        {
            // Taken: Modbot's own from an earlier insert, or somebody else's. Read back to know which.
            case GoogleProblem.Duplicate:
                await ReadBackAsync(calendarEvent, place, body, fingerprint, CalendarGoogleWants.There, ct).ConfigureAwait(false);
                return;

            // Google may have made it: the place waits, and is read back before anything else.
            case GoogleProblem.Unavailable:
                NoAnswer(place);
                _log.Warning(
                    "Google Calendar insert for the event {EventId} got no answer; it is read back before anything else is sent",
                    calendarEvent.Id);
                return;

            default:
                Refused(calendarEvent, place, "create", failure, fingerprint);
                return;
        }
    }

    /// <summary>
    /// Reads back an event whose insert may have gone through: Modbot's own and live is taken as
    /// this event's; not there, the same id is inserted; deleted on Google, the next id is made.
    /// </summary>
    private async Task ReadBackAsync(
        CalendarEvent calendarEvent,
        CalendarEventPlace place,
        CalendarGoogleBody body,
        string fingerprint,
        CalendarGoogleWants wants,
        CancellationToken ct)
    {
        var id = place.ExternalId!;
        var answer = await CallAsync(token => _google.GetEventAsync(token, _calendarId, id, ct), ct).ConfigureAwait(false);

        if (answer is not { } result)
            return;

        if (result.Value is { } found)
        {
            if (!CalendarGoogleBody.IsOwnedBy(found, calendarEvent.Id))
            {
                // Not Modbot's: never written over. A later try uses the next id.
                place.ExternalId = GoogleEventIds.Next(calendarEvent.Id, id);
                Failed(calendarEvent, place, "create", TakenError, fingerprint);
                return;
            }

            if (CalendarGoogleBody.IsCancelled(found))
            {
                await GoneOnGoogleAsync(calendarEvent, place, body, fingerprint, wants, ct).ConfigureAwait(false);
                return;
            }

            // Made after all: this event's own. What it says is not known, so it is written once more.
            place.SentFingerprint = AdoptedFingerprint;
            place.GoogleCalendarId = _calendarId;
            place.State = CalendarPlaceStates.Waiting;
            place.FailedFingerprint = null;
            place.Error = null;
            place.ErrorAt = null;
            place.UpdatedAt = _now;

            _log.Information("Google Calendar event {GoogleEventId} for the event {EventId} was there after all", id, calendarEvent.Id);

            await UpdateAsync(calendarEvent, place, body, fingerprint, wants, ct).ConfigureAwait(false);
            return;
        }

        var failure = result.Failure!;

        switch (failure.Problem)
        {
            // Not made: the same id is inserted, unless the event no longer wants making. A 404 is
            // also what an unshared calendar answers, so the calendar is asked about first.
            case GoogleProblem.NotFound:
                switch (await CalendarThereAsync(ct).ConfigureAwait(false))
                {
                    case null:
                        NoAnswer(place);
                        return;

                    case false:
                        return;
                }

                if (wants != CalendarGoogleWants.There)
                {
                    MarkRemoved(calendarEvent, place);
                    return;
                }

                // Once a pass: a 409 that reads back as a 404 again waits for the next pass.
                if (!_postedAfterReadBack.Add(calendarEvent.Id))
                {
                    NoAnswer(place);
                    return;
                }

                place.ErrorAt = _now;
                place.UpdatedAt = _now;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                await PostAsync(calendarEvent, place, body, fingerprint, ct).ConfigureAwait(false);
                return;

            case GoogleProblem.Gone:
                await GoneOnGoogleAsync(calendarEvent, place, body, fingerprint, wants, ct).ConfigureAwait(false);
                return;

            case GoogleProblem.Unavailable:
                NoAnswer(place);
                return;

            default:
                Refused(calendarEvent, place, "create", failure, fingerprint);
                return;
        }
    }

    private async Task UpdateAsync(
        CalendarEvent calendarEvent,
        CalendarEventPlace place,
        CalendarGoogleBody body,
        string fingerprint,
        CalendarGoogleWants wants,
        CancellationToken ct)
    {
        var id = place.ExternalId!;
        var fresh = place.SentFingerprint == AdoptedFingerprint;
        var answer = await CallAsync(token => _google.UpdateEventAsync(token, _calendarId, id, body.ToJson(id), ct), ct).ConfigureAwait(false);

        if (answer is not { } result)
            return;

        if (result.Value is not null)
        {
            Published(calendarEvent, place, fingerprint, fresh);
            _log.Information("Google Calendar update for the event {EventId}", calendarEvent.Id);
            return;
        }

        var failure = result.Failure!;

        switch (failure.Problem)
        {
            // Deleted on Google while Modbot still wants it there: made again (§3.4). A 404 is also
            // what an unshared calendar answers, so the calendar is asked about first.
            case GoogleProblem.NotFound or GoogleProblem.Gone:
                if (failure.Problem == GoogleProblem.NotFound)
                {
                    switch (await CalendarThereAsync(ct).ConfigureAwait(false))
                    {
                        case null:
                            NoAnswer(place);
                            return;

                        case false:
                            return;
                    }
                }

                await GoneOnGoogleAsync(calendarEvent, place, body, fingerprint, wants, ct).ConfigureAwait(false);
                return;

            case GoogleProblem.Unavailable:
                NoAnswer(place);
                return;

            default:
                Refused(calendarEvent, place, "update", failure, fingerprint);
                return;
        }
    }

    /// <summary>Deleted on Google: made again with the next id when still wanted, otherwise done.</summary>
    private async Task GoneOnGoogleAsync(
        CalendarEvent calendarEvent,
        CalendarEventPlace place,
        CalendarGoogleBody body,
        string fingerprint,
        CalendarGoogleWants wants,
        CancellationToken ct)
    {
        if (wants != CalendarGoogleWants.There)
        {
            MarkRemoved(calendarEvent, place);
            return;
        }

        // Once a pass: an id taken by a deleted copy again waits for the next pass.
        if (!_madeAgain.Add(calendarEvent.Id))
        {
            NoAnswer(place);
            return;
        }

        ForgetDates(calendarEvent);

        _log.Information("Google Calendar event for the event {EventId} was deleted on Google; it is made again", calendarEvent.Id);
        await InsertAsync(calendarEvent, place, body, fingerprint, nextTurn: true, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Takes the event off Google. Already gone (404, 410) is what a removal wanted. An event whose
    /// insert may have gone through is deleted by its id all the same, which is safe either way.
    /// </summary>
    private async Task TakeDownAsync(CalendarEvent calendarEvent, CalendarEventPlace place, CancellationToken ct)
    {
        if (place.State == CalendarPlaceStates.Removed)
            return;

        if (place.ExternalId is null)
        {
            MarkRemoved(calendarEvent, place);
            return;
        }

        if (Held(place, DeleteFingerprint))
            return;

        var id = place.ExternalId;
        var calendarId = place.GoogleCalendarId ?? _calendarId;
        var answer = await CallAsync(token => _google.DeleteEventAsync(token, calendarId, id, ct), ct).ConfigureAwait(false);

        if (answer is not { } result)
            return;

        // A 404 is what Google answers for an event already gone, and also for a calendar no longer
        // shared with Modbot. Only the first is done: the calendar is asked about before the place
        // is marked removed (added 2026-10-03). Not there, the place stays as it is and the lane
        // says why; no answer, it is tried again later.
        if (result.Failure is { Problem: GoogleProblem.NotFound })
        {
            switch (await CalendarThereAsync(ct).ConfigureAwait(false))
            {
                case null:
                    NoAnswer(place);
                    return;

                case false:
                    return;
            }
        }

        if (result.Value is not null || result.Failure!.Problem is GoogleProblem.NotFound or GoogleProblem.Gone)
        {
            MarkRemoved(calendarEvent, place);
            _wrote |= result.Value is not null;
            _log.Information("Google Calendar delete for the event {EventId}", calendarEvent.Id);
            return;
        }

        var failure = result.Failure!;

        if (failure.Problem == GoogleProblem.Unavailable)
        {
            NoAnswer(place);
            return;
        }

        Refused(calendarEvent, place, "delete", failure, DeleteFingerprint);
    }

    /// <summary>
    /// Whether the calendar in Settings is there for Modbot to change, by the read Check makes
    /// (<c>events.list</c> for one event). Asked once a pass at most, after a 404 that could mean
    /// either "this event is gone" or "this calendar is not shared with Modbot any more". False
    /// writes the lane's problem, so nothing more is sent until a good Check; null is no answer.
    /// </summary>
    private async Task<bool?> CalendarThereAsync(CancellationToken ct)
    {
        if (_calendarThere is { } known)
            return known;

        var answer = await CallAsync(token => _google.CalendarAsync(token, _calendarId, ct), ct).ConfigureAwait(false);

        if (answer is not { } result)
            return null;

        if (result.Value is { } info)
        {
            if (info.CanChangeEvents)
                return _calendarThere = true;

            LaneProblem(info.CanRead ? GoogleErrors.ReadOnly : GoogleErrors.CannotSee);
            return _calendarThere = false;
        }

        var failure = result.Failure!;

        if (failure.Problem is GoogleProblem.NotFound or GoogleProblem.Forbidden)
        {
            // A 403 for who Modbot is has written the lane's problem already (CallAsync).
            if (_settings.GoogleProblem is null)
                LaneProblem(failure.Problem == GoogleProblem.NotFound ? GoogleErrors.CannotSee : GoogleErrors.Sentence(failure));

            return _calendarThere = false;
        }

        return null;
    }

    /// <summary>
    /// Settings names another calendar: the copy on the old one is deleted (§3.6). Modbot may not
    /// see the old calendar any more, so a refusal there is only noted and must not stop the lane for
    /// the new one. No answer (a timeout, Google's 5xx) is tried again <see cref="NoAnswerWait"/>
    /// later, for up to <see cref="OldCalendarTriesFor"/> from the first try; then it is noted and
    /// given up (added 2026-10-03).
    /// </summary>
    private async Task MoveOffAsync(CalendarEvent calendarEvent, CalendarEventPlace place, string oldCalendarId, CancellationToken ct)
    {
        var retrying = place.FailedFingerprint == OldCalendarFingerprint && place.ErrorAt is not null;

        if (retrying && _now - place.UpdatedAt < NoAnswerWait)
            return;

        var id = place.ExternalId!;
        var answer = await CallAsync(
            token => _google.DeleteEventAsync(token, oldCalendarId, id, ct), ct, laneProblems: false).ConfigureAwait(false);

        if (answer is not { } result)
            return;

        if (result.Failure is { Problem: GoogleProblem.Unavailable })
        {
            var firstTry = retrying ? place.ErrorAt!.Value : _now;

            if (_now - firstTry < OldCalendarTriesFor)
            {
                // Waiting, with the first try's time kept, so the day is counted from it.
                place.State = CalendarPlaceStates.Waiting;
                place.FailedFingerprint = OldCalendarFingerprint;
                place.Error = null;
                place.ErrorAt = firstTry;
                place.UpdatedAt = _now;
                return;
            }

            _log.Warning(
                "Gave up taking the event {EventId} off the Google calendar Modbot used before: no answer since {FirstTry}",
                calendarEvent.Id, firstTry);
        }
        else if (result.Failure is { } failure && failure.Problem is not (GoogleProblem.NotFound or GoogleProblem.Gone))
        {
            _log.Warning(
                "Could not take the event {EventId} off the Google calendar Modbot used before: {Status} {Reason}",
                calendarEvent.Id, failure.Status, GoogleErrors.Sentence(failure));
        }

        MarkRemoved(calendarEvent, place);
    }

    /// <summary>How long a delete from the calendar Modbot used before is tried again when Google does not answer.</summary>
    public static readonly TimeSpan OldCalendarTriesFor = TimeSpan.FromDays(1);

    /// <summary>The store marker for a delete from the calendar Modbot used before that is being tried again.</summary>
    public const string OldCalendarFingerprint = "old-calendar";

    // ── One date of a series (§3.5) ──────────────────────────────────────────────────────

    private async Task DatesAsync(CalendarEvent calendarEvent, CalendarEventPlace place, CancellationToken ct)
    {
        if (place.State != CalendarPlaceStates.Published
            || place.ExternalId is null
            || calendarEvent.Repeat == CalendarRepeats.None
            || calendarEvent.DateChanges.Count == 0
            || !string.Equals(place.GoogleCalendarId, _calendarId, StringComparison.Ordinal)
            || place.SentFingerprint != CalendarGoogleBody.For(calendarEvent, _worldNames, _groupName).Fingerprint(_calendarId))
        {
            return;
        }

        var length = CalendarRepeat.LengthOf(calendarEvent);

        foreach (var change in calendarEvent.DateChanges.OrderBy(c => c.PlannedStartsAt).ToList())
        {
            if (OutOfCalls)
                return;

            if (IsOver(change, length))
                continue;

            var date = CalendarGoogleBody.DateFor(calendarEvent, change, _worldNames);

            // Not on Google as its own: a cancelled date is an EXDATE in the series already, and a
            // date put back as planned has nothing to put back.
            if (!CalendarDates.MayBeOnGoogle(change))
            {
                if (date.Kind == CalendarGoogleDateKind.Planned && CalendarDates.CanForget(change, length))
                    calendarEvent.DateChanges.Remove(change);

                if (date.Kind != CalendarGoogleDateKind.Changed)
                    continue;
            }

            if (change.GoogleSentFingerprint == date.Fingerprint || DateHeld(change, date.Fingerprint))
                continue;

            if (_now - change.UpdatedAt < SettleFor)
                continue;

            await WriteDateAsync(calendarEvent, place, change, date, ct).ConfigureAwait(false);
        }
    }

    private async Task WriteDateAsync(
        CalendarEvent calendarEvent, CalendarEventPlace place, CalendarDateChange change, CalendarGoogleDate date, CancellationToken ct)
    {
        var seriesId = place.ExternalId!;
        var length = CalendarRepeat.LengthOf(calendarEvent);

        // Looked up before every write: Google's own copy of the date, by when it was planned.
        var lookup = await CallAsync(
            token => _google.InstanceAsync(token, _calendarId, seriesId, change.PlannedStartsAt, ct), ct).ConfigureAwait(false);

        if (lookup is not { } found)
            return;

        if (found.Failure is { } lookFailure)
        {
            DateRefused(calendarEvent, change, lookFailure, date.Fingerprint);
            return;
        }

        if (found.Value is not { } instance || instance["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var instanceId))
        {
            // Not in the series any more: for a cancel, that is what it wanted.
            if (date.Kind == CalendarGoogleDateKind.Cancel)
                DateSent(change, date);
            else
                DateFailed(calendarEvent, change, DateNotFoundError, date.Fingerprint);

            return;
        }

        var answer = await CallAsync(
            token => _google.UpdateEventAsync(token, _calendarId, instanceId, date.ApplyTo(instance), ct), ct).ConfigureAwait(false);

        if (answer is not { } result)
            return;

        // A cancel answered 404 is done only while the calendar itself is still there (see TakeDownAsync).
        if (date.Kind == CalendarGoogleDateKind.Cancel && result.Failure is { Problem: GoogleProblem.NotFound })
        {
            switch (await CalendarThereAsync(ct).ConfigureAwait(false))
            {
                case null:
                    DateRefused(calendarEvent, change, GoogleErrors.NoAnswer(), date.Fingerprint);
                    return;

                case false:
                    return;
            }
        }

        if (result.Value is not null
            || (date.Kind == CalendarGoogleDateKind.Cancel && result.Failure!.Problem is GoogleProblem.NotFound or GoogleProblem.Gone))
        {
            _wrote |= result.Value is not null;

            if (date.Kind == CalendarGoogleDateKind.Planned)
            {
                // Google has the planned date back: nothing of it is Google's own any more.
                CalendarDates.ForgetOnGoogle(change);

                if (CalendarDates.CanForget(change, length))
                    calendarEvent.DateChanges.Remove(change);
            }
            else
            {
                DateSent(change, date);
            }

            _log.Information("Google Calendar {Kind} for one date of the event {EventId}", date.Kind, calendarEvent.Id);
            return;
        }

        DateRefused(calendarEvent, change, result.Failure!, date.Fingerprint);
    }

    private void DateRefused(CalendarEvent calendarEvent, CalendarDateChange change, GoogleFailure failure, string fingerprint)
    {
        if (failure.Problem == GoogleProblem.Unavailable)
        {
            change.GoogleError = null;
            change.GoogleErrorAt = _now;
            change.GoogleFailedFingerprint = null;
            return;
        }

        DateFailed(calendarEvent, change, failure.Problem == GoogleProblem.NotFound ? DateNotFoundError : GoogleErrors.Sentence(failure), fingerprint);
    }

    private void DateFailed(CalendarEvent calendarEvent, CalendarDateChange change, string error, string fingerprint)
    {
        change.GoogleError = Trim(error);
        change.GoogleErrorAt = _now;
        change.GoogleFailedFingerprint = fingerprint;

        _failures.Add((calendarEvent, new JsonObject
        {
            ["place"] = CalendarPlaces.Google,
            ["action"] = "date",
            ["date"] = change.PlannedStartsAt.ToString("O", CultureInfo.InvariantCulture),
            ["error"] = change.GoogleError,
        }));

        _log.Warning("Google Calendar write for one date of the event {EventId} failed: {Reason}", calendarEvent.Id, change.GoogleError);
    }

    private static void DateSent(CalendarDateChange change, CalendarGoogleDate date)
    {
        change.GoogleSentFingerprint = date.Fingerprint;
        change.GoogleFailedFingerprint = null;
        change.GoogleError = null;
        change.GoogleErrorAt = null;
    }

    private bool DateHeld(CalendarDateChange change, string fingerprint)
    {
        if (change.GoogleErrorAt is not { } at)
            return false;

        if (change.GoogleFailedFingerprint == fingerprint)
            return true;

        return change.GoogleFailedFingerprint is null && _now - at < NoAnswerWait;
    }

    private bool IsOver(CalendarDateChange change, TimeSpan length) =>
        change.PlannedStartsAt + length <= _now && (change.EndsAt is not { } ends || ends <= _now);

    // ── States ───────────────────────────────────────────────────────────────────────────

    /// <summary>Written: Google has the event as <paramref name="fingerprint"/> says.</summary>
    /// <param name="fresh">
    /// A new Google event (an insert, or one found after an insert with no answer): it holds no date
    /// changed on its own. Otherwise the series was written again, and every date Google may hold as
    /// changed is sent again, since whether Google keeps them is not known (§3.5).
    /// </param>
    private void Published(CalendarEvent calendarEvent, CalendarEventPlace place, string fingerprint, bool fresh)
    {
        place.State = CalendarPlaceStates.Published;
        place.SentFingerprint = fingerprint;
        place.GoogleCalendarId = _calendarId;
        place.FailedFingerprint = null;
        place.Error = null;
        place.ErrorAt = null;
        place.UpdatedAt = _now;
        _wrote = true;

        var length = CalendarRepeat.LengthOf(calendarEvent);

        if (fresh)
        {
            ForgetDates(calendarEvent);
            return;
        }

        foreach (var change in calendarEvent.DateChanges.Where(c => CalendarDates.MayBeOnGoogle(c) && !IsOver(c, length)))
        {
            change.GoogleSentFingerprint = AgainFingerprint;
            change.GoogleFailedFingerprint = null;
            change.GoogleError = null;
            change.GoogleErrorAt = null;
        }
    }

    /// <summary>Not on Google, and holding nothing there.</summary>
    private void MarkRemoved(CalendarEvent calendarEvent, CalendarEventPlace place)
    {
        place.State = CalendarPlaceStates.Removed;
        place.SentFingerprint = null;
        place.GoogleCalendarId = null;
        place.FailedFingerprint = null;
        place.Error = null;
        place.ErrorAt = null;
        place.UpdatedAt = _now;

        // The id stays: the next time the event is made it gets the turn after it.
        ForgetDates(calendarEvent);
    }

    /// <summary>Google holds no date of this event as its own: each is forgotten there, and a row left with nothing goes.</summary>
    private static void ForgetDates(CalendarEvent calendarEvent)
    {
        var length = CalendarRepeat.LengthOf(calendarEvent);

        foreach (var change in calendarEvent.DateChanges.ToList())
        {
            CalendarDates.ForgetOnGoogle(change);

            if (CalendarDates.CanForget(change, length))
                calendarEvent.DateChanges.Remove(change);
        }
    }

    /// <summary>No answer: the place waits, and is tried again <see cref="NoAnswerWait"/> later.</summary>
    private void NoAnswer(CalendarEventPlace place)
    {
        place.State = CalendarPlaceStates.Waiting;
        place.FailedFingerprint = null;
        place.Error = null;
        place.ErrorAt = _now;
        place.UpdatedAt = _now;
    }

    private void Refused(CalendarEvent calendarEvent, CalendarEventPlace place, string action, GoogleFailure failure, string fingerprint)
    {
        // Not allowed on the calendar: held until the next good Check, not until the event changes.
        if (GoogleErrors.IsNotAllowed(failure))
        {
            Failed(calendarEvent, place, action, GoogleErrors.CannotChange, NotAllowedFingerprint);
            return;
        }

        Failed(calendarEvent, place, action, GoogleErrors.Sentence(failure), fingerprint);
    }

    private void Failed(CalendarEvent calendarEvent, CalendarEventPlace place, string action, string error, string fingerprint)
    {
        place.State = CalendarPlaceStates.Failed;
        place.Error = Trim(error);
        place.ErrorAt = _now;
        place.FailedFingerprint = fingerprint;
        place.UpdatedAt = _now;

        _failures.Add((calendarEvent, new JsonObject
        {
            ["place"] = CalendarPlaces.Google,
            ["action"] = action,
            ["error"] = place.Error,
        }));

        _log.Warning("Google Calendar {Action} for the event {EventId} failed: {Reason}", action, calendarEvent.Id, place.Error);
    }

    /// <summary>A failure that should not be sent again yet.</summary>
    private bool Held(CalendarEventPlace place, string fingerprint)
    {
        if (place.State == CalendarPlaceStates.Failed)
        {
            if (place.FailedFingerprint == NotAllowedFingerprint)
                return _settings.GoogleCheckedAt is not { } checkedAt || place.ErrorAt is not { } at || checkedAt <= at;

            return place.FailedFingerprint == fingerprint;
        }

        // No answer: again after a while, not every pass.
        return place.FailedFingerprint is null && place.ErrorAt is { } sent && _now - sent < NoAnswerWait;
    }

    private bool Settling(CalendarEvent calendarEvent) => _now - calendarEvent.UpdatedAt < SettleFor;

    // ── Calls ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One call to the Calendar API with the kept token. A 401 gets one new token and one more try;
    /// a limit stops the lane (never retried); a key, clock or sharing refusal stops it until the next
    /// good Check. Null when nothing came of it that the caller should act on: no call was made, or
    /// the lane stopped.
    /// </summary>
    /// <param name="laneProblems">
    /// Whether a refusal of who Modbot is stops the lane. Not for the calendar Modbot used before.
    /// </param>
    private async Task<GoogleResult<T>?> CallAsync<T>(Func<string, Task<GoogleResult<T>>> call, CancellationToken ct, bool laneProblems = true)
    {
        if (OutOfCalls)
            return null;

        if (await TokenAsync(fresh: false, ct).ConfigureAwait(false) is not { } token)
            return null;

        // A token request counts as a call: the probe after a limit's wait may be that request
        // alone, and the event call then waits for the next pass.
        if (OutOfCalls)
            return null;

        _calls++;
        var result = await call(token).ConfigureAwait(false);

        if (result.Failure is { Problem: GoogleProblem.Unauthorized })
        {
            // A new token once; then a 401 is a failure like any other, tried again later.
            if (_refreshedToken)
                return GoogleResult<T>.Failed(GoogleErrors.NoAnswer());

            _refreshedToken = true;
            _signIn.Refused(token);
            _token = null;

            if (await TokenAsync(fresh: true, ct).ConfigureAwait(false) is not { } renewed || OutOfCalls)
                return null;

            _calls++;
            result = await call(renewed).ConfigureAwait(false);

            if (result.Failure is { Problem: GoogleProblem.Unauthorized })
                return GoogleResult<T>.Failed(GoogleErrors.NoAnswer());
        }

        if (result.Failure is { StopsTheLane: true } limited)
        {
            Stop(limited);
            return null;
        }

        if (laneProblems && result.Failure is { } refused && GoogleErrors.IsNotAllowed(refused))
            LaneProblem(GoogleErrors.CannotChange);

        // An answer after a limit's wait: the probe went through, and the lane is open again.
        if (result.Failure is null || result.Failure.Problem != GoogleProblem.Unavailable)
        {
            if (_settings.GoogleStoppedUntil is not null)
            {
                _settings.GoogleStoppedUntil = null;
                _budget = MostCallsAPass;
            }
        }

        return result;
    }

    private async Task<string?> TokenAsync(bool fresh, CancellationToken ct)
    {
        if (!fresh && _token is not null)
            return _token;

        // A request to Google's token address is a call like any other, and counted as one.
        if (fresh || !_signIn.HasToken(_key))
        {
            if (OutOfCalls)
                return null;

            _calls++;
        }

        var answer = await _signIn.TokenAsync(_key, fresh, ct).ConfigureAwait(false);

        if (answer.Value is { } token)
            return _token = token;

        var failure = answer.Failure!;

        if (failure.StopsTheLane)
            Stop(failure);
        else if (failure.Problem is GoogleProblem.KeyRefused or GoogleProblem.ClockOff)
            LaneProblem(GoogleErrors.Sentence(failure));
        else
            _stopped = true;

        return null;
    }

    /// <summary>A limit: no call of any kind before it ends (CLAUDE.md: never retry a 429).</summary>
    private void Stop(GoogleFailure failure)
    {
        var until = _now + GoogleErrors.StopFor(failure);

        if (_settings.GoogleStoppedUntil is not { } already || already < until)
            _settings.GoogleStoppedUntil = until;

        _stopped = true;

        _log.Warning(
            "Google is limiting Modbot ({Status} {Reason}); nothing is sent to Google until {Until}",
            failure.Status, failure.Reason, _settings.GoogleStoppedUntil);
    }

    /// <summary>
    /// The lane cannot work as set up: written where Check writes its problem, so Settings, the
    /// Integrations card and Health say so, and nothing more is tried until a new key or a good Check.
    /// </summary>
    private void LaneProblem(string sentence)
    {
        _settings.GoogleProblem = sentence;
        _stopped = true;

        _log.Warning("Google Calendar sending stopped: {Problem}", sentence);
    }

    // ── Facts ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fact for each place that got onto Google for the first time or came off it in this pass,
    /// and one for each failure. An edit writes none.
    /// </summary>
    private async Task RecordFactsAsync(
        List<CalendarEvent> events,
        Dictionary<Guid, CalendarEventPlace> places,
        Dictionary<Guid, (string State, bool OnGoogle)> was,
        CancellationToken ct)
    {
        foreach (var calendarEvent in events)
        {
            if (!places.TryGetValue(calendarEvent.Id, out var place))
                continue;

            var (before, onGoogle) = was.TryGetValue(calendarEvent.Id, out var seen) ? seen : (null, false);
            if (place.State == before)
                continue;

            if (place.State == CalendarPlaceStates.Published && !onGoogle)
            {
                await _facts.RecordAsync(
                    FactType.PlannedEventPublished,
                    calendarEvent,
                    new JsonObject { ["place"] = CalendarPlaces.Google },
                    ct: ct).ConfigureAwait(false);
            }
            else if (place.State == CalendarPlaceStates.Removed && onGoogle)
            {
                await _facts.RecordAsync(
                    FactType.PlannedEventTakenDown,
                    calendarEvent,
                    new JsonObject { ["place"] = CalendarPlaces.Google, ["was"] = before },
                    ct: ct).ConfigureAwait(false);
            }
        }

        foreach (var (calendarEvent, data) in _failures)
            await _facts.RecordAsync(FactType.PlannedEventPublishFailed, calendarEvent, data, ct: ct).ConfigureAwait(false);
    }

    // ── Try again and edits (calendar design §17.3, §17.4) ───────────────────────────────

    /// <summary>
    /// A moderator's Try again on a failed Google place: the next pass sends it again, as it is. An
    /// insert whose outcome is not known is still read back first. False, changing nothing, for a
    /// place that has not failed.
    /// </summary>
    public static bool TryAgain(CalendarEventPlace place, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(place);

        if (place.Place != CalendarPlaces.Google || place.State != CalendarPlaceStates.Failed)
            return false;

        place.State = CalendarPlaceStates.Waiting;
        place.FailedFingerprint = null;
        place.Error = null;
        place.ErrorAt = null;
        place.UpdatedAt = now;
        return true;
    }

    /// <summary>An edit clears an old failure at once, as for the other places.</summary>
    public static bool ClearAfterEdit(CalendarEventPlace place, DateTimeOffset now) => TryAgain(place, now);

    /// <summary>A moderator's Try again on one date whose Google write failed. False for a date with no failure.</summary>
    public static bool TryDateAgain(CalendarDateChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (change.GoogleErrorAt is null && change.GoogleError is null)
            return false;

        change.GoogleError = null;
        change.GoogleErrorAt = null;
        change.GoogleFailedFingerprint = null;
        return true;
    }

    private static string Trim(string text) => text.Length <= 1024 ? text : text[..1023] + "…";
}
