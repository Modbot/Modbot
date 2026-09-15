using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;

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
/// A refusal is not sent again until the event changes. A write with no answer -- a timeout, or
/// VRChat's own 5xx -- is tried again after <see cref="RetryUnansweredAfter"/>.
/// </para>
/// </remarks>
public sealed class CalendarVRChatPublisher
{
    public static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(20);

    public static readonly TimeSpan RetryUnansweredAfter = TimeSpan.FromMinutes(15);

    /// <summary>The fingerprint a refused delete is kept under, so it is not repeated every pass.</summary>
    private const string DeleteFingerprint = "delete";

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly IModbotClock _clock;
    private readonly CalendarFacts _facts;
    private readonly ILogger _log;

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
                }

                var fingerprint = CalendarVRChatRequests.Fingerprint(calendarEvent);

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

            if (place.ExternalId is null)
            {
                place.State = CalendarPlaceStates.Removed;
                place.UpdatedAt = now;
                continue;
            }

            if (Held(place, DeleteFingerprint, now))
                continue;

            due ??= (calendarEvent, place, "delete", DeleteFingerprint);
        }

        if (due is not { } work)
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new CalendarPublishResult(CalendarPublishOutcome.NothingToDo);
        }

        var outcome = await WriteAsync(work.Event, work.Place, work.Action, work.Fingerprint, groupId, now, ct)
            .ConfigureAwait(false);

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        if (outcome == CalendarPublishOutcome.Failed)
        {
            await _facts.RecordAsync(
                FactType.PlannedEventPublishFailed,
                work.Event,
                new JsonObject { ["place"] = CalendarPlaces.VRChat, ["action"] = work.Action, ["error"] = work.Place.Error },
                ct: ct).ConfigureAwait(false);
        }

        return new CalendarPublishResult(outcome, work.Event.Id, work.Action);
    }

    /// <summary>Whether an event belongs on VRChat's calendar right now.</summary>
    public static bool Wants(CalendarEvent calendarEvent) =>
        calendarEvent.PublishToVRChat
        && calendarEvent.DeletedAt is null
        && CalendarEventStates.IsLive(calendarEvent.State);

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
        VRChatFailureKind kind;
        string? createdId = null;

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

                (status, success, error, kind) = (result.StatusCode, result.Success, result.ErrorMessage, result.Kind);
                createdId = result.Value?.Id;

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

                (status, success, error, kind) = (result.StatusCode, result.Success, result.ErrorMessage, result.Kind);

                // Deleted on VRChat's side. Forget the id; the next pass creates it again.
                if (status == 404)
                {
                    place.ExternalId = null;
                    place.SentFingerprint = null;
                    place.State = CalendarPlaceStates.Waiting;
                    place.UpdatedAt = now;
                    return CalendarPublishOutcome.NothingToDo;
                }

                break;
            }

            default:
            {
                var id = place.ExternalId!;
                var result = await _gate.ExecuteAsync(
                    endpoint,
                    (client, token) => client.Calendar.DeleteGroupCalendarEventWithHttpInfoAsync(groupId, id, token),
                    VRChatCallPriority.Background,
                    ct).ConfigureAwait(false);

                (status, success, error, kind) = (result.StatusCode, result.Success, result.ErrorMessage, result.Kind);

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
            }
            else
            {
                place.ExternalId = createdId ?? place.ExternalId;
                place.SentFingerprint = fingerprint;
                place.State = CalendarPlaceStates.Published;
            }

            place.FailedFingerprint = null;
            place.Error = null;
            place.ErrorAt = null;

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

        place.State = CalendarPlaceStates.Failed;
        place.Error = Trim(error ?? $"VRChat answered {status}.");
        place.ErrorAt = now;

        // Nothing came back, or VRChat's own trouble: try again later. Anything else is a refusal of
        // this content, and sending it again would get the same answer.
        place.FailedFingerprint = status == 0 || status >= 500 ? null : fingerprint;

        _log.Warning(
            "VRChat calendar {Action} for the event {EventId} failed: {Status} {Reason}",
            action, calendarEvent.Id, status, place.Error);

        return CalendarPublishOutcome.Failed;
    }

    private static string Trim(string text) => text.Length <= 1024 ? text : text[..1023] + "…";
}
