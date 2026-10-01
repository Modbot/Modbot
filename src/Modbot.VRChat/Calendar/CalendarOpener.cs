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

namespace Modbot.VRChat.Calendar;

/// <summary>What one pass of the opener did.</summary>
/// <param name="Failed">Attempts VRChat really refused, or that will be tried again.</param>
public sealed record CalendarOpenerResult(int Opened, int Failed, bool NotConfigured = false);

/// <summary>How one attempt to open an instance ended.</summary>
public enum CalendarOpenOutcome
{
    /// <summary>VRChat made the instance.</summary>
    Opened,

    /// <summary>VRChat refused with a 4xx other than 429. Final for that time.</summary>
    Refused,

    /// <summary>
    /// No answer to act on: nothing was sent, VRChat asked Modbot to slow down, or it failed on its
    /// side. A later pass may try again while the time has not ended.
    /// </summary>
    TryAgain,
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
/// <strong>Only a real refusal is final</strong> (changed 2026-10-01). Until then any failure gave
/// up on the time, even when nothing had been sent: an attempt made while the gate was waiting out a
/// rate limit or a sign-in never reached VRChat, and the event went without its instance. Now a 4xx
/// other than 429 is final, shown on the event and on Health and recorded as a fact; anything else is
/// marked to try again, on a later pass, while the time has not ended. A 429 is never tried again in
/// the same pass: it cold-stops <c>instances.create</c>, and the gate sends nothing on it until the
/// limiter allows (foundation §4.3.1).
/// </para>
/// <para>
/// The instance is recorded through <see cref="PlaceStore"/> the same way a sighting is, so Live, the
/// instance cards and the calendar's Discord posts find it without knowing where it came from.
/// </para>
/// </remarks>
public sealed class CalendarOpener
{
    /// <summary>How long after a failed try the next one may go, for a failure that is not a 429.</summary>
    public static readonly TimeSpan TryAgainAfter = TimeSpan.FromMinutes(1);

    /// <summary>How long before a time's start Open now is offered: the most an event may open early.</summary>
    public static readonly TimeSpan OpenNowEarliest = TimeSpan.FromMinutes(120);

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

        var candidates = await _db.CalendarEvents
            .Where(e => e.AutoOpen
                && e.DeletedAt == null
                && e.WorldId != null
                && (e.State == CalendarEventStates.Scheduled || e.State == CalendarEventStates.Open))
            .ToListAsync(ct).ConfigureAwait(false);

        var opened = 0;
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
            else if (attempt.TryAgain && attempt.Location is null && !TooSoon(attempt, now))
            {
                // Marked as tried again before it is sent, for the same reason the row is.
                attempt.TryAgain = false;
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
    /// instance of that time is open. An instance that has closed, or an attempt that failed, is
    /// replaced: the row is the time's, and it now says what this attempt did. People who were still
    /// to be invited when the old instance closed are put back on the queue, and the first-person
    /// posts may go again for the new instance.
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

        var replacingClosed = false;

        if (attempt is not null)
        {
            if (attempt.Location is not null)
            {
                var closed = attempt.InstanceId is { } instanceId
                    && await _db.VRChatInstances.AsNoTracking().AnyAsync(i => i.Id == instanceId && i.ClosedAt != null, ct).ConfigureAwait(false);

                if (!closed)
                    return new CalendarOpenNowResult(CalendarOpenNowOutcome.AlreadyOpen);

                replacingClosed = true;
            }
            else if (attempt.Error is null && !attempt.TryAgain && now - attempt.AttemptedAt < TryAgainAfter)
            {
                // Another attempt went out a moment ago and has no answer yet.
                return new CalendarOpenNowResult(CalendarOpenNowOutcome.AlreadyOpen);
            }

            attempt.Location = null;
            attempt.InstanceId = null;
            attempt.Error = null;
            attempt.TryAgain = false;
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
            return new CalendarOpenNowResult(CalendarOpenNowOutcome.AlreadyOpen);
        }

        var outcome = await OpenAsync(calendarEvent, attempt, groupId, now, VRChatCallPriority.Interactive, actorUserId, ct)
            .ConfigureAwait(false);

        if (outcome == CalendarOpenOutcome.Opened && replacingClosed)
            await InviteAgainAsync(attempt, now, ct).ConfigureAwait(false);

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

        if (location is not { Length: > 0 })
        {
            var reason = result.Success
                ? "VRChat created the instance but did not say where it is."
                : result.ErrorMessage ?? $"VRChat answered {result.StatusCode}.";

            if (!IsFinal(result))
            {
                // Nothing to act on yet. A request the gate never sent leaves no error to show: it
                // is waiting, the way a place waiting on a rate limit is.
                attempt.TryAgain = true;
                attempt.Error = result.WasNotSent && IsWaiting(result.Kind) ? null : Trim(reason);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                _log.Warning(
                    "Could not open the instance for the event {EventId} yet; it will be tried again: {Reason}",
                    calendarEvent.Id, reason);

                return CalendarOpenOutcome.TryAgain;
            }

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

        var instance = await _places.RecordSightingAsync(location, now, fromGroupList: false, ct: ct).ConfigureAwait(false);

        attempt.Location = location;
        attempt.InstanceId = instance?.Id;
        attempt.Error = null;
        attempt.TryAgain = false;
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

        return CalendarOpenOutcome.Opened;
    }

    /// <summary>
    /// People still to be invited when the time's earlier instance closed go back on the queue for
    /// the new one. Everybody already reached stays reached.
    /// </summary>
    private async Task InviteAgainAsync(CalendarOpening attempt, DateTimeOffset now, CancellationToken ct)
    {
        var stopped = _db.CalendarInvites.Where(i => i.EventId == attempt.EventId
            && i.OccurrenceStartsAt == attempt.OccurrenceStartsAt
            && i.State == CalendarInviteStates.Stopped);

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

    /// <summary>
    /// Whether VRChat really refused: a 4xx other than 429. Everything else -- nothing sent, a 429,
    /// VRChat's own 5xx, no answer in time -- leaves the time to be tried again.
    /// </summary>
    internal static bool IsFinal<T>(VRChatResult<T> result) =>
        result.Success || (result.StatusCode is >= 400 and < 500 && result.StatusCode != 429);

    private static bool IsWaiting(VRChatFailureKind kind) =>
        kind is VRChatFailureKind.RateLimited or VRChatFailureKind.SignInWaiting or VRChatFailureKind.NotConfigured;

    /// <summary>A 429 waits for the limiter, which the gate keeps; anything else waits a minute.</summary>
    private static bool TooSoon(CalendarOpening attempt, DateTimeOffset now) => now - attempt.AttemptedAt < TryAgainAfter;

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
