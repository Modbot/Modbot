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
public sealed record CalendarOpenerResult(int Opened, int Failed, bool NotConfigured = false);

/// <summary>
/// Opens an event's group instance a few minutes before it starts (calendar design §4).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Once per occurrence, across restarts.</strong> The <see cref="CalendarOpening"/> row is
/// saved before the request is sent, and its key is the event and the occurrence's start. A crash
/// between the two leaves a row with no outcome, and no second instance.
/// </para>
/// <para>
/// <strong>A failure is final for that occurrence.</strong> It is kept on the row, shown on the
/// event and on Health, and recorded as a fact. The next occurrence gets its own attempt. Nothing
/// here loops against a write VRChat has just refused.
/// </para>
/// <para>
/// The room is recorded through <see cref="PlaceStore"/> the same way a sighting is, so Live, the
/// instance cards and the calendar's Discord posts find it without knowing where it came from.
/// </para>
/// </remarks>
public sealed class CalendarOpener
{
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

            var tried = await _db.CalendarOpenings.AnyAsync(
                o => o.EventId == calendarEvent.Id && o.OccurrenceStartsAt == occurrence.StartsAt, ct).ConfigureAwait(false);

            if (tried)
                continue;

            var attempt = new CalendarOpening
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

            if (await OpenAsync(calendarEvent, attempt, groupId, now, ct).ConfigureAwait(false))
                opened++;
            else
                failed++;
        }

        return new CalendarOpenerResult(opened, failed);
    }

    private async Task<bool> OpenAsync(
        CalendarEvent calendarEvent, CalendarOpening attempt, string groupId, DateTimeOffset now, CancellationToken ct)
    {
        var request = new CreateInstanceRequest(
            worldId: calendarEvent.WorldId!,
            type: InstanceType.Group,
            region: Region(calendarEvent.Region),
            ownerId: groupId,
            groupAccessType: Access(calendarEvent.AccessType));

        var result = await _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.InstancesCreate, Operation: "CreateInstance"),
            (client, token) => client.Instances.CreateInstanceWithHttpInfoAsync(request, token),
            VRChatCallPriority.Background,
            ct).ConfigureAwait(false);

        var location = result.Success ? result.Value?.Location : null;

        if (location is not { Length: > 0 })
        {
            var reason = result.Success
                ? "VRChat created the instance but did not say where it is."
                : result.ErrorMessage ?? $"VRChat answered {result.StatusCode}.";

            attempt.Error = Trim(reason);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            _log.Warning(
                "Could not open the instance for the event {EventId}: {Reason}", calendarEvent.Id, reason);

            await _facts.RecordAsync(
                FactType.PlannedEventInstanceFailed,
                calendarEvent,
                new JsonObject { ["error"] = attempt.Error, ["status"] = result.StatusCode },
                worldId: calendarEvent.WorldId,
                ct: ct).ConfigureAwait(false);

            return false;
        }

        var room = await _places.RecordSightingAsync(location, now, fromGroupList: false, ct: ct).ConfigureAwait(false);

        attempt.Location = location;
        attempt.RoomId = room?.Id;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _log.Information("Opened the instance for the event {EventId}", calendarEvent.Id);

        await _facts.RecordAsync(
            FactType.PlannedEventInstanceOpened,
            calendarEvent,
            new JsonObject { ["occurrenceStartsAt"] = attempt.OccurrenceStartsAt.ToString("O") },
            worldId: room?.WorldId ?? calendarEvent.WorldId,
            instanceId: room?.VRChatInstanceId,
            ct: ct).ConfigureAwait(false);

        return true;
    }

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
