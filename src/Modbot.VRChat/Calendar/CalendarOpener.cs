using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Calendar;
using Modbot.Core.Calendar;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Serilog;
using VRChat.API.Model;
using CalendarEvent = Modbot.Core.Data.Entities.CalendarEvent;

namespace Modbot.VRChat.Calendar;

/// <summary>What one pass of the opener did.</summary>
/// <param name="Opened">Instances opened, or found after an attempt with no answer.</param>
/// <param name="Failed">Attempts that did not open one this pass, whatever comes next.</param>
public sealed record CalendarOpenerResult(int Opened, int Failed, bool NotConfigured = false);

/// <summary>How one attempt to open an instance ended.</summary>
public enum CalendarOpenOutcome
{
    /// <summary>VRChat made the instance.</summary>
    Opened,

    /// <summary>VRChat refused with a 4xx other than 408 and 429. Final for that time.</summary>
    Refused,

    /// <summary>
    /// Certainly no instance: the gate never sent it, Cloudflare stopped it, or VRChat answered 429.
    /// A later pass sends it again while the time has not ended.
    /// </summary>
    TryAgain,

    /// <summary>
    /// The request went out and VRChat failed on its side or did not answer, so it may have made an
    /// instance anyway. Never sent again on its own; Modbot looks for the instance instead.
    /// </summary>
    Checking,
}

/// <summary>Why Open now did not open anything, or that it tried.</summary>
public enum CalendarOpenNowOutcome
{
    /// <summary>An attempt was made; <see cref="CalendarOpenNowResult.Opened"/> says how it went.</summary>
    Tried,

    NotConfigured,
    NoSuchEvent,
    NoWorld,
    TooEarly,
    Over,
    AlreadyOpen,

    /// <summary>An earlier attempt is in flight, or its outcome is still being checked.</summary>
    Checking,
}

/// <param name="Opened">How the attempt ended, when one was made.</param>
public sealed record CalendarOpenNowResult(CalendarOpenNowOutcome Outcome, CalendarOpenOutcome? Opened = null);

/// <summary>
/// Opens an event's group instance a few minutes before it starts (calendar design §4), and when
/// somebody presses Open now.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Once per occurrence, across restarts.</strong> The <see cref="CalendarOpening"/> row is
/// saved before the request is sent, and its key is the event and the occurrence's start. A crash
/// between the two leaves a row with no outcome, and no second instance.
/// </para>
/// <para>
/// <strong>Sent again only when it certainly made nothing</strong> (changed 2026-10-01). Until then
/// any failure gave up on the time, even when nothing had been sent, and the event went without its
/// instance. Now:
/// </para>
/// <list type="bullet">
/// <item>Nothing left Modbot (the gate waiting out a rate limit or a sign-in), Cloudflare stopped it,
/// or VRChat answered 429: sent again on a later pass, a minute on at the soonest; a 429 only once
/// the limiter's cold stop allows (foundation §4.3.1).</item>
/// <item>VRChat refused with any other 4xx (408 aside): final, shown, recorded as a fact.</item>
/// <item>VRChat failed on its side (5xx), answered 408, or nothing came back: VRChat has answered 500
/// while still making things, so a second request could open a second instance. It is
/// <strong>never sent again on its own</strong>. Modbot looks, in what the group instance poll has
/// already recorded, for an instance of the event's world in the group made since the attempt, and
/// takes it as the event's if there is one. If a poll that ran at least <see cref="PollAfterAttempt"/>
/// after the unclear answer came back shows none, the attempt is shown as failed, recorded as a fact
/// like a refusal, and Open now is left to the moderator.</item>
/// </list>
/// <para>
/// The instance is recorded through <see cref="PlaceStore"/> the same way a sighting is, so Live, the
/// instance cards and the calendar's Discord posts find it without knowing where it came from.
/// </para>
/// </remarks>
public sealed class CalendarOpener
{
    /// <summary>The soonest an attempt that certainly made nothing is sent again.</summary>
    public static readonly TimeSpan TryAgainAfter = TimeSpan.FromMinutes(1);

    /// <summary>How long before a time's start Open now is offered: the most an event may open early.</summary>
    public static readonly TimeSpan OpenNowEarliest = TimeSpan.FromMinutes(120);

    /// <summary>
    /// How long after an unclear answer came back a group instance poll must have run before "no
    /// instance" is believed: long enough for VRChat to list an instance it was still making.
    /// </summary>
    public static readonly TimeSpan PollAfterAttempt = TimeSpan.FromSeconds(15);

    /// <summary>The words an unanswered attempt that found no instance is shown with.</summary>
    public const string NoAnswer = "VRChat did not answer whether it opened the instance, and none showed up.";

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly PlaceStore _places;
    private readonly IModbotClock _clock;
    private readonly CalendarFacts _facts;
    private readonly ILogger _log;

    public CalendarOpener(
        IVRChatGate gate,
        ModbotContext db,
        PlaceStore places,
        IModbotClock clock,
        CalendarFacts facts,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(facts);

        _gate = gate;
        _db = db;
        _places = places;
        _clock = clock;
        _facts = facts;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<CalendarOpenerResult> RunOnceAsync(CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        // No group, no attempt -- and no row either, so configuring a group later still opens an
        // occurrence that has not ended.
        if (settings.ManagedGroupId is not { Length: > 0 } groupId)
            return new CalendarOpenerResult(0, 0, NotConfigured: true);

        var now = _clock.UtcNow;

        // Every attempt with no answer, whether the timer or Open now made it.
        var found = await CheckAsync(groupId, settings.GroupInstancesPolledAt, now, ct).ConfigureAwait(false);

        var candidates = await _db.CalendarEvents
            .Where(e => e.AutoOpen
                && e.DeletedAt == null
                && e.WorldId != null
                && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open))
            .ToListAsync(ct).ConfigureAwait(false);

        var opened = found;
        var failed = 0;

        foreach (var calendarEvent in candidates)
        {
            if (CalendarRepeat.Next(calendarEvent, now) is not { } occurrence)
                continue;

            if (now < CalendarRepeat.OpensAt(calendarEvent, occurrence) || now >= occurrence.EndsAt)
                continue;

            var attempt = await _db.CalendarOpenings.FirstOrDefaultAsync(
                o => o.EventId == calendarEvent.Id && o.OccurrenceStartsAt == occurrence.StartsAt, ct).ConfigureAwait(false);

            if (attempt is null)
            {
                attempt = new CalendarOpening
                {
                    EventId = calendarEvent.Id,
                    OccurrenceStartsAt = occurrence.StartsAt,
                    AttemptedAt = now,
                };

                _db.CalendarOpenings.Add(attempt);

                try
                {
                    // Saved before anything is sent. This is the whole promise of "once".
                    await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (DbUpdateException)
                {
                    // Another process wrote the row first. It owns this occurrence.
                    _db.Entry(attempt).State = EntityState.Detached;
                    continue;
                }
            }
            else if (attempt.TryAgain && attempt.Location is null && now - attempt.AttemptedAt >= TryAgainAfter)
            {
                // Marked as in flight before it is sent, for the same reason the row is: with no
                // error and no "try again" on it, Open now sees an attempt under way and waits.
                attempt.TryAgain = false;
                attempt.Error = null;
                attempt.AttemptedAt = now;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            else
            {
                continue;
            }

            var outcome = await OpenAsync(calendarEvent, attempt, groupId, now, VRChatCallPriority.Background, actorUserId: null, ct)
                .ConfigureAwait(false);

            if (outcome == CalendarOpenOutcome.Opened)
                opened++;
            else
                failed++;
        }

        return new CalendarOpenerResult(opened, failed);
    }

    /// <summary>
    /// Opens the instance for an event's current or next time now, because somebody pressed Open
    /// now: through the same path, on the same budget, ahead of the sweeps.
    /// </summary>
    /// <remarks>
    /// Offered from <see cref="OpenNowEarliest"/> before the start until the end, and only while no
    /// instance of that time is open, opening, or being looked for after an attempt with no answer.
    /// An instance that has closed, or an attempt that failed, is replaced: the row is the time's, and
    /// it now says what this attempt did. People who were still to be invited when the old instance
    /// closed go back on the queue once the new one is open, and the first-person posts may go again.
    /// </remarks>
    public async Task<CalendarOpenNowResult> OpenNowAsync(Guid eventId, Guid actorUserId, CancellationToken ct = default)
    {
        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);
        if (settings.ManagedGroupId is not { Length: > 0 } groupId)
            return new CalendarOpenNowResult(CalendarOpenNowOutcome.NotConfigured);

        var now = _clock.UtcNow;

        var calendarEvent = await _db.CalendarEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == eventId && e.DeletedAt == null, ct).ConfigureAwait(false);

        if (calendarEvent is null || !CalendarEventStates.IsLive(calendarEvent.State))
            return new CalendarOpenNowResult(CalendarOpenNowOutcome.NoSuchEvent);

        if (calendarEvent.WorldId is null)
            return new CalendarOpenNowResult(CalendarOpenNowOutcome.NoWorld);

        if (CalendarRepeat.Next(calendarEvent, now) is not { } occurrence || now >= occurrence.EndsAt)
            return new CalendarOpenNowResult(CalendarOpenNowOutcome.Over);

        if (now < occurrence.StartsAt - OpenNowEarliest)
            return new CalendarOpenNowResult(CalendarOpenNowOutcome.TooEarly);

        var attempt = await _db.CalendarOpenings.FirstOrDefaultAsync(
            o => o.EventId == eventId && o.OccurrenceStartsAt == occurrence.StartsAt, ct).ConfigureAwait(false);

        if (attempt is not null)
        {
            if (attempt.Location is not null)
            {
                var closed = attempt.InstanceId is { } instanceId
                    && await _db.VRChatInstances.AsNoTracking().AnyAsync(i => i.Id == instanceId && i.ClosedAt != null, ct).ConfigureAwait(false);

                if (!closed)
                    return new CalendarOpenNowResult(CalendarOpenNowOutcome.AlreadyOpen);
            }
            else if (attempt.Checking)
            {
                // VRChat may have made one; a second request could make two.
                return new CalendarOpenNowResult(CalendarOpenNowOutcome.Checking);
            }
            else if (attempt.Error is null && !attempt.TryAgain && now - attempt.AttemptedAt < TryAgainAfter)
            {
                // Another attempt went out a moment ago and has no answer yet.
                return new CalendarOpenNowResult(CalendarOpenNowOutcome.Checking);
            }

            attempt.Location = null;
            attempt.InstanceId = null;
            attempt.Error = null;
            attempt.TryAgain = false;
            attempt.Checking = false;
            attempt.CheckingSince = null;
            attempt.AttemptedAt = now;
            attempt.OpenedByUserId = actorUserId;
            attempt.FirstJoinDiscordPostedAt = null;
            attempt.FirstJoinDiscordPostError = null;
            attempt.FirstJoinVRChatPostedAt = null;
            attempt.FirstJoinVRChatPostError = null;
        }
        else
        {
            attempt = new CalendarOpening
            {
                EventId = eventId,
                OccurrenceStartsAt = occurrence.StartsAt,
                AttemptedAt = now,
                OpenedByUserId = actorUserId,
            };

            _db.CalendarOpenings.Add(attempt);
        }

        try
        {
            // Saved before anything is sent, as on the timer.
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The timer wrote this time's row at the same moment. It owns the attempt.
            return new CalendarOpenNowResult(CalendarOpenNowOutcome.Checking);
        }

        var outcome = await OpenAsync(calendarEvent, attempt, groupId, now, VRChatCallPriority.Interactive, actorUserId, ct)
            .ConfigureAwait(false);

        return new CalendarOpenNowResult(CalendarOpenNowOutcome.Tried, outcome);
    }

    private async Task<CalendarOpenOutcome> OpenAsync(
        CalendarEvent calendarEvent,
        CalendarOpening attempt,
        string groupId,
        DateTimeOffset now,
        VRChatCallPriority priority,
        Guid? actorUserId,
        CancellationToken ct)
    {
        var request = Request(calendarEvent, groupId);

        var result = await _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.InstancesCreate, Operation: "CreateInstance"),
            (client, token) => client.Instances.CreateInstanceWithHttpInfoAsync(request, token),
            priority,
            ct).ConfigureAwait(false);

        var location = result.Success ? result.Value?.Location : null;

        if (location is { Length: > 0 })
        {
            var instance = await _places.RecordSightingAsync(location, now, fromGroupList: false, ct: ct).ConfigureAwait(false);
            await OpenedAsync(calendarEvent, attempt, location, instance, actorUserId, ct).ConfigureAwait(false);
            return CalendarOpenOutcome.Opened;
        }

        var reason = result.Success
            ? "VRChat created the instance but did not say where it is."
            : result.ErrorMessage ?? $"VRChat answered {result.StatusCode}.";

        switch (Classify(result))
        {
            case CalendarOpenOutcome.TryAgain:
                // A request the gate never sent leaves no error to show: it is waiting, the way a
                // place waiting on a rate limit is.
                attempt.TryAgain = true;
                attempt.Error = result.WasNotSent && IsWaiting(result.Kind) ? null : Trim(reason);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                _log.Warning(
                    "Could not open the instance for the event {EventId} yet; it will be sent again: {Reason}",
                    calendarEvent.Id, reason);

                return CalendarOpenOutcome.TryAgain;

            case CalendarOpenOutcome.Checking:
                attempt.Checking = true;

                // When the answer came back, not when the request left: a request can hang for a
                // while, and a poll that ran during it says nothing about what it made.
                attempt.CheckingSince = _clock.UtcNow;
                attempt.Error = null;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                _log.Warning(
                    "VRChat gave no clear answer to opening the instance for the event {EventId}; looking for it instead: {Reason}",
                    calendarEvent.Id, reason);

                return CalendarOpenOutcome.Checking;

            default:
                attempt.TryAgain = false;
                attempt.Error = Trim(reason);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                _log.Warning(
                    "Could not open the instance for the event {EventId}: {Reason}", calendarEvent.Id, reason);

                await _facts.RecordAsync(
                    FactType.PlannedEventInstanceFailed,
                    calendarEvent,
                    new JsonObject { ["error"] = attempt.Error, ["status"] = result.StatusCode },
                    worldId: calendarEvent.WorldId,
                    ct: ct,
                    actorUserId: actorUserId).ConfigureAwait(false);

                return CalendarOpenOutcome.Refused;
        }
    }

    /// <summary>
    /// For each attempt with no answer: takes an instance VRChat made after all, from what the group
    /// instance poll recorded, or, once a poll after the attempt shows none, gives up and shows it.
    /// Sends nothing.
    /// </summary>
    /// <returns>How many instances were found and taken.</returns>
    private async Task<int> CheckAsync(string groupId, DateTimeOffset? polledAt, DateTimeOffset now, CancellationToken ct)
    {
        var checking = await _db.CalendarOpenings
            .Where(o => o.Checking && o.Location == null)
            .ToListAsync(ct).ConfigureAwait(false);

        var found = 0;

        foreach (var attempt in checking)
        {
            var calendarEvent = await _db.CalendarEvents.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == attempt.EventId, ct).ConfigureAwait(false);

            if (calendarEvent?.WorldId is null)
            {
                attempt.Checking = false;
                attempt.Error = NoAnswer;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                if (calendarEvent is not null)
                    await GaveUpAsync(calendarEvent, attempt, ct).ConfigureAwait(false);

                continue;
            }

            if (await MadeAfterAllAsync(calendarEvent, attempt, groupId, ct).ConfigureAwait(false) is { } instance)
            {
                await OpenedAsync(calendarEvent, attempt, instance.Location, instance, attempt.OpenedByUserId, ct).ConfigureAwait(false);
                found++;
                continue;
            }

            var ended = now >= attempt.OccurrenceStartsAt + (calendarEvent.EndsAt - calendarEvent.StartsAt);
            var answeredAt = attempt.CheckingSince ?? attempt.AttemptedAt;
            var pollSince = polledAt is { } at && at >= answeredAt + PollAfterAttempt;

            if (ended || pollSince)
            {
                // Shown as failed; Open now is the moderator's to press. Not sent again from here.
                attempt.Checking = false;
                attempt.Error = NoAnswer;
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                await GaveUpAsync(calendarEvent, attempt, ct).ConfigureAwait(false);
            }
        }

        return found;
    }

    /// <summary>The fact for an attempt that ended with no instance, the same one a refusal writes.</summary>
    private Task GaveUpAsync(CalendarEvent calendarEvent, CalendarOpening attempt, CancellationToken ct) =>
        _facts.RecordAsync(
            FactType.PlannedEventInstanceFailed,
            calendarEvent,
            new JsonObject { ["error"] = attempt.Error, ["status"] = 0 },
            worldId: calendarEvent.WorldId,
            ct: ct,
            actorUserId: attempt.OpenedByUserId);

    /// <summary>
    /// An open instance of the event's world in the group, with its access and region, that Modbot
    /// first saw after the attempt and that no other time has taken.
    /// </summary>
    private async Task<VRChatInstance?> MadeAfterAllAsync(
        CalendarEvent calendarEvent, CalendarOpening attempt, string groupId, CancellationToken ct)
    {
        var taken = _db.CalendarOpenings.Where(o => o.InstanceId != null).Select(o => o.InstanceId!.Value);

        var found = await _db.VRChatInstances.AsNoTracking()
            .Where(i => i.GroupId == groupId
                && i.WorldId == calendarEvent.WorldId
                && i.ClosedAt == null
                && i.OpenedAt >= attempt.AttemptedAt
                && !taken.Contains(i.Id))
            .OrderBy(i => i.OpenedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        return found.FirstOrDefault(i =>
            (i.GroupAccessType is null || string.Equals(i.GroupAccessType, calendarEvent.AccessType, StringComparison.OrdinalIgnoreCase))
            && (i.Region is null || string.Equals(i.Region, calendarEvent.Region, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Records the time's instance, and puts back on the queue whoever its last one left out.</summary>
    private async Task OpenedAsync(
        CalendarEvent calendarEvent,
        CalendarOpening attempt,
        string location,
        VRChatInstance? instance,
        Guid? actorUserId,
        CancellationToken ct)
    {
        var now = _clock.UtcNow;

        attempt.Location = location;
        attempt.InstanceId = instance?.Id;
        attempt.Error = null;
        attempt.TryAgain = false;
        attempt.Checking = false;
        attempt.CheckingSince = null;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.Information("Opened the instance for the event {EventId}", calendarEvent.Id);

        await _facts.RecordAsync(
            FactType.PlannedEventInstanceOpened,
            calendarEvent,
            new JsonObject
            {
                ["occurrenceStartsAt"] = attempt.OccurrenceStartsAt.ToString("O"),
                ["byHand"] = actorUserId is not null,
            },
            worldId: instance?.WorldId ?? calendarEvent.WorldId,
            instanceId: instance?.VRChatInstanceId,
            ct: ct,
            actorUserId: actorUserId).ConfigureAwait(false);

        // Whichever way this time's instance came -- the timer, Open now, a retry, or one found after
        // an attempt with no answer -- people its earlier instance left out go back on the queue.
        await InviteAgainAsync(attempt, now, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// People still to be invited when the time's earlier instance closed go back on the queue for
    /// the new one. Everybody already reached stays reached; nobody stopped for another reason moves.
    /// </summary>
    private async Task InviteAgainAsync(CalendarOpening attempt, DateTimeOffset now, CancellationToken ct)
    {
        var stopped = _db.CalendarInvites.Where(i => i.EventId == attempt.EventId
            && i.OccurrenceStartsAt == attempt.OccurrenceStartsAt
            && i.State == CalendarInviteStates.Stopped
            && i.Problem == CalendarInvites.InstanceClosed);

        await stopped.Where(i => i.VRChatUserId != null).ExecuteUpdateAsync(
            set => set
                .SetProperty(i => i.State, CalendarInviteStates.Waiting)
                .SetProperty(i => i.Problem, (string?)null)
                .SetProperty(i => i.UpdatedAt, now),
            ct).ConfigureAwait(false);

        await stopped.Where(i => i.VRChatUserId == null && i.DiscordUserId != null).ExecuteUpdateAsync(
            set => set
                .SetProperty(i => i.State, CalendarInviteStates.ToMessage)
                .SetProperty(i => i.Problem, (string?)null)
                .SetProperty(i => i.UpdatedAt, now),
            ct).ConfigureAwait(false);
    }

    /// <summary>What a failed attempt means for the next one.</summary>
    internal static CalendarOpenOutcome Classify<T>(VRChatResult<T> result)
    {
        // Certainly nothing made: the gate held it back, Cloudflare stopped it before VRChat saw
        // it, or VRChat said slow down.
        if (result.WasNotSent && IsWaiting(result.Kind))
            return CalendarOpenOutcome.TryAgain;

        if (result.IsWafBlocked || result.StatusCode == 429)
            return CalendarOpenOutcome.TryAgain;

        // VRChat read it and said no.
        if (result.StatusCode is >= 400 and < 500 && result.StatusCode != 408)
            return CalendarOpenOutcome.Refused;

        // A 5xx, a 408, no answer at all, or a success that did not say where: it may exist.
        return CalendarOpenOutcome.Checking;
    }

    private static bool IsWaiting(VRChatFailureKind kind) =>
        kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting or VRChatFailureKind.NotConfigured;

    /// <summary>What is sent to VRChat to open an event's instance.</summary>
    /// <remarks>
    /// <c>instancePersistenceEnabled</c> is sent as <c>false</c>. The SDK sends it as <c>null</c>
    /// when it is left out, and VRChat refuses that with a 400, "instancePersistenceEnabled must be
    /// a boolean: 'null'" (seen 2026-10-01), so every opening failed. False asks for no
    /// persistence, which Modbot never wanted. <c>playerPersistenceEnabled</c> still goes as
    /// <c>null</c>; that refusal named only the instance setting.
    /// </remarks>
    internal static CreateInstanceRequest Request(CalendarEvent calendarEvent, string groupId) => new(
        worldId: calendarEvent.WorldId!,
        type: InstanceType.Group,
        region: Region(calendarEvent.Region),
        ownerId: groupId,
        groupAccessType: Access(calendarEvent.AccessType),
        instancePersistenceEnabled: false);

    internal static InstanceRegion Region(string region) => region switch
    {
        "use" => InstanceRegion.Use,
        "eu" => InstanceRegion.Eu,
        "jp" => InstanceRegion.Jp,
        _ => InstanceRegion.Us,
    };

    internal static GroupAccessType Access(string access) => access switch
    {
        "plus" => GroupAccessType.Plus,
        "public" => GroupAccessType.Public,
        _ => GroupAccessType.Members,
    };

    private static string Trim(string text) => text.Length <= 1024 ? text : text[..1023] + "…";
}
