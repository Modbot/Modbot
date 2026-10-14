using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Modbot.Analytics.Facts;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Discord;
using Modbot.Core.Logging;
using Modbot.Core.Time;
using Modbot.VRChat.Calendar;
using Serilog;
using VRChat.API.Model;

namespace Modbot.VRChat.Instances;

/// <summary>
/// Opens the second instance an "open a second instance" alert asks for: one more group instance of
/// the same world, with the same access and region as the full one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>One second instance per alert.</strong> The press is claimed on the alert's row before
/// anything is sent, the way the calendar claims an attempt, so two staff pressing together make one
/// instance. A press after it opened gets the same link back and opens nothing. A press after VRChat
/// certainly made nothing (the gate waiting out the 1-per-5-seconds lane, a 429, a refusal) gives
/// the claim back so the button can be pressed again; after an answer that may have made an instance
/// (a 5xx, no answer) the claim stays for <see cref="TryAgainAfter"/>, because a second request could
/// open a second one.
/// </para>
/// <para>
/// Goes through <see cref="IVRChatGate"/> on the same <c>instances.create</c> lane as the calendar,
/// and a 429 is never retried here: it is reported and the button is pressed again.
/// </para>
/// <para>
/// What is copied from the full instance is its world, group access type and region. The instance
/// is sent with persistence off, as the calendar's are.
/// </para>
/// </remarks>
public sealed class SecondInstanceOpener : ISecondInstances
{
    public const string NoPermission = "You need the \"Manage calendar\" permission in Modbot.";
    public const string NotSetUp = "This deployment is not set up to act in VRChat.";
    public const string NoInstance = "That alert has no instance to copy.";
    public const string BeingOpened = "A second instance is being opened already. Try again in a minute.";
    public const string TryAgainSoon = "VRChat did not take it yet. Press the button again in a few seconds.";
    public const string NoAnswer = "VRChat did not say whether it opened the instance. Look in Live before pressing again.";

    /// <summary>How long a press that may have made an instance blocks another.</summary>
    public static readonly TimeSpan TryAgainAfter = TimeSpan.FromMinutes(2);

    private readonly IVRChatGate _gate;
    private readonly ModbotContext _db;
    private readonly PlaceStore _places;
    private readonly IFactWriter _facts;
    private readonly EventPartitionMaintainer _partitions;
    private readonly IModbotClock _clock;
    private readonly ILogger _log;

    public SecondInstanceOpener(
        IVRChatGate gate,
        ModbotContext db,
        PlaceStore places,
        IFactWriter facts,
        EventPartitionMaintainer partitions,
        IModbotClock clock,
        ILogger? log = null)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(places);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(partitions);
        ArgumentNullException.ThrowIfNull(clock);

        _gate = gate;
        _db = db;
        _places = places;
        _facts = facts;
        _partitions = partitions;
        _clock = clock;
        _log = (log ?? Log.Logger).ForContext(LogArea.Name, LogArea.Sync);
    }

    public async Task<SecondInstanceAnswer> OpenAsync(Guid alertId, StaffMember by, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(by);

        if (!by.Held.HasFlag(ModbotPermissions.Administrator) && !by.Held.HasFlag(ModbotPermissions.ManageCalendar))
            return SecondInstanceAnswer.Refused(NoPermission);

        var alert = await _db.Alerts.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == alertId && a.Watcher == AlertWatchers.InstanceSecond, ct).ConfigureAwait(false);

        if (alert?.InstanceId is not { } fullId)
            return SecondInstanceAnswer.Refused(NoInstance);

        if (alert.SecondLocation is { Length: > 0 } opened)
            return new SecondInstanceAnswer(true, "A second instance is open already.", LinkTo(opened), Already: true);

        var settings = await _db.GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.ManagedGroupId is not { Length: > 0 } groupId)
            return SecondInstanceAnswer.Refused(NotSetUp);

        var full = await _db.VRChatInstances.AsNoTracking().FirstOrDefaultAsync(i => i.Id == fullId, ct).ConfigureAwait(false);

        if (full is null)
            return SecondInstanceAnswer.Refused(NoInstance);

        // Claimed before anything is sent: a second press sees it and waits.
        var now = _clock.UtcNow;
        var stale = now - TryAgainAfter;

        var claimed = await _db.Alerts
            .Where(a => a.Id == alertId && a.SecondLocation == null && (a.SecondTriedAt == null || a.SecondTriedAt < stale))
            .ExecuteUpdateAsync(
                u => u.SetProperty(a => a.SecondTriedAt, now).SetProperty(a => a.SecondOpenedByUserId, by.UserId), ct)
            .ConfigureAwait(false);

        if (claimed == 0)
        {
            // Somebody finished between the read and the claim.
            var location = await _db.Alerts.AsNoTracking()
                .Where(a => a.Id == alertId).Select(a => a.SecondLocation).FirstOrDefaultAsync(ct).ConfigureAwait(false);

            return location is { Length: > 0 }
                ? new SecondInstanceAnswer(true, "A second instance is open already.", LinkTo(location), Already: true)
                : SecondInstanceAnswer.Refused(BeingOpened);
        }

        var request = new CreateInstanceRequest(
            worldId: full.WorldId,
            type: InstanceType.Group,
            region: CalendarOpener.Region(full.Region ?? string.Empty),
            ownerId: groupId,
            groupAccessType: CalendarOpener.Access(full.GroupAccessType ?? string.Empty),
            instancePersistenceEnabled: false);

        var result = await _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.InstancesCreate, Operation: "CreateInstance"),
            (client, token) => client.Instances.CreateInstanceWithHttpInfoAsync(request, token),
            VRChatCallPriority.Interactive,
            ct).ConfigureAwait(false);

        var newLocation = result.Success ? result.Value?.Location : null;

        if (newLocation is { Length: > 0 })
        {
            await OpenedAsync(alert, full, newLocation, by, ct).ConfigureAwait(false);
            return new SecondInstanceAnswer(true, "Second instance open.", LinkTo(newLocation, full.WorldId));
        }

        var reason = result.Success
            ? "VRChat created the instance but did not say where it is."
            : result.ErrorMessage ?? $"VRChat answered {result.StatusCode}.";

        _log.Warning("Could not open the second instance for the alert {AlertId}: {Reason}", alertId, reason);

        switch (CalendarOpener.Classify(result))
        {
            case CalendarOpenOutcome.TryAgain:
                await GiveBackAsync(alertId, ct).ConfigureAwait(false);
                return SecondInstanceAnswer.Refused(TryAgainSoon);

            case CalendarOpenOutcome.Checking:
                // It may exist: the claim stays, so a press now cannot make another.
                return SecondInstanceAnswer.Refused(NoAnswer);

            default:
                await GiveBackAsync(alertId, ct).ConfigureAwait(false);
                return SecondInstanceAnswer.Refused(reason.Length <= 300 ? reason : reason[..300]);
        }
    }

    private async Task GiveBackAsync(Guid alertId, CancellationToken ct)
        => await _db.Alerts.Where(a => a.Id == alertId && a.SecondLocation == null)
            .ExecuteUpdateAsync(
                u => u.SetProperty(a => a.SecondTriedAt, (DateTimeOffset?)null).SetProperty(a => a.SecondOpenedByUserId, (Guid?)null), ct)
            .ConfigureAwait(false);

    private async Task OpenedAsync(Alert alert, VRChatInstance full, string location, StaffMember by, CancellationToken ct)
    {
        var now = _clock.UtcNow;

        await _db.Alerts.Where(a => a.Id == alert.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.SecondLocation, location), ct).ConfigureAwait(false);

        // Recorded the way the calendar's are, so Live and the instance cards find it.
        var instance = await _places.RecordSightingAsync(location, now, fromGroupList: false, ct: ct).ConfigureAwait(false);

        _log.Information("Opened a second instance for the alert {AlertId}", alert.Id);

        await _partitions.EnsureForAsync(now, ct).ConfigureAwait(false);
        await _facts.WriteAsync(
            new FactRecord
            {
                Type = FactType.SecondInstanceOpened,
                OccurredAt = now,
                SubjectPlatform = FactPlatform.Modbot,
                SubjectId = alert.Id.ToString(),
                ActorPlatform = FactPlatform.Modbot,
                ActorId = by.UserId.ToString(),
                WorldId = full.WorldId,
                InstanceId = instance?.VRChatInstanceId,
                Source = FactSource.Modbot,
                Data = new JsonObject
                {
                    ["location"] = location,
                    ["fullLocation"] = full.Location,
                    ["people"] = alert.Now,
                },
            },
            ct).ConfigureAwait(false);
    }

    private static string? LinkTo(string location, string? worldId = null) => InstanceJoinLink.For(location, worldId);
}
