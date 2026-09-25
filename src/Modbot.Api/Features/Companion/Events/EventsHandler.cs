using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Companion.Alerts;
using Modbot.Api.Features.Companion.Context;
using Modbot.Api.Features.Companion.Devices;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Companion.Events;

public sealed record CompanionEventDto(
    [property: JsonPropertyName("companionEventId")] string? CompanionEventId,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("occurredAt")] DateTimeOffset OccurredAt,
    [property: JsonPropertyName("occurredBefore")] DateTimeOffset? OccurredBefore,
    [property: JsonPropertyName("subjectId")] string? SubjectId,
    [property: JsonPropertyName("worldId")] string? WorldId,
    [property: JsonPropertyName("instanceId")] string? InstanceId,
    [property: JsonPropertyName("groupId")] string? GroupId,
    [property: JsonPropertyName("data")] Dictionary<string, string>? Data);

public sealed record EventBatchDto(
    [property: JsonPropertyName("batchId")] string? BatchId,
    [property: JsonPropertyName("companionVersion")] string? CompanionVersion,
    [property: JsonPropertyName("clockOffsetMs")] long ClockOffsetMs,
    [property: JsonPropertyName("clockConfidence")] string? ClockConfidence,
    [property: JsonPropertyName("events")] IReadOnlyList<CompanionEventDto>? Events);

/// <param name="Index">Which event in the submitted batch. Position, so the client can point at it.</param>
public sealed record RejectedEvent(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("reason")] string Reason);

public sealed record EventBatchResponse(
    [property: JsonPropertyName("accepted")] int Accepted,
    [property: JsonPropertyName("deduplicated")] int Deduplicated,
    [property: JsonPropertyName("rejected")] IReadOnlyList<RejectedEvent> Rejected);

/// <summary>
/// Presence ingest: one batch, from one moderator's client, about one group.
/// </summary>
/// <remarks>
/// <para><strong>Partial acceptance is the normal case, not an error.</strong> Four to six
/// moderators in one instance all see the same join and all report it, so a batch where a quarter
/// of the events were already known is a completely successful request. Deduplication happens
/// inside <see cref="IFactWriter"/> as a windowed range check under a transaction-scoped advisory
/// lock; its failure mode is silent, and every time-spent metric would simply be wrong by six.</para>
/// <para><strong>Events for another group are rejected, not stored.</strong> The client is meant
/// to have decided routing locally and never sent them, so one arriving is a client bug or a
/// hostile caller; either way it is named in the response rather than quietly dropped.</para>
/// <para><strong>The server stamps its own <c>observed_at</c>.</strong> A moderator's PC with a
/// wrong clock must not be able to reorder the log, so the client's timestamp is what it claims
/// and the server's is what orders it.</para>
/// </remarks>
public static class EventsHandler
{
    /// <summary>Protocol 4.4. Above this the client is told to halve and retry.</summary>
    public const int MaxEventsPerBatch = 500;

    /// <summary>
    /// How far in the past a client may claim something happened.
    /// </summary>
    /// <remarks>
    /// Generous enough to cover any realistic offline buffer -- the client's own bound is three
    /// days -- and short enough to stay inside the fact log's monthly partitions, which exist for
    /// the previous month, this one, and two ahead. Thirty days back is at worst the previous
    /// calendar month, whatever day of the month it is now.
    /// </remarks>
    public static readonly TimeSpan MaxBackdate = TimeSpan.FromDays(30);

    /// <summary>
    /// How far into the future a client may claim something happened: transport delay and a little
    /// slack, and nothing more. The future is not somewhere observations come from.
    /// </summary>
    public static readonly TimeSpan MaxSkewAhead = TimeSpan.FromMinutes(5);

    public static async Task<IResult> HandleAsync(
        int apiVersion,
        EventBatchDto? batch,
        HttpContext context,
        DeviceAuthenticator authenticator,
        IFactWriter facts,
        ICompanionDeviceStore devices,
        AlertHub alerts,
        DeviceLocations locations,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!CompanionApiVersion.IsSupported(apiVersion))
            return CompanionApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (batch?.Events is not { Count: > 0 } events)
            return CompanionApiErrors.Malformed("A batch must carry at least one event.");

        if (events.Count > MaxEventsPerBatch)
        {
            // 413, so the client halves and retries rather than dropping. Nothing is lost: the
            // events are still in its buffer.
            return Results.Json(
                new CompanionError(
                    CompanionApiErrors.BatchTooLarge,
                    $"A batch may carry at most {MaxEventsPerBatch} events; this one carried {events.Count}."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var settings = await database.GetSettingsAsync(ct);
        if (settings.ManagedGroupId is not { Length: > 0 } managedGroupId)
            return CompanionApiErrors.NotReady();

        var rejected = new List<RejectedEvent>();
        var candidates = new List<FactRecord>(events.Count);

        for (var index = 0; index < events.Count; index++)
        {
            if (ToFact(events[index], managedGroupId, authentication.Device!.Id, clock.UtcNow) is { } fact)
                candidates.Add(fact);
            else
                rejected.Add(new RejectedEvent(index, Reason(events[index], managedGroupId)));
        }

        if (candidates.Count == 0)
            return Results.Ok(new EventBatchResponse(0, 0, rejected));

        // Where this batch says its reporter is standing, which is what lets a flagged-join alert
        // go to the moderators who can act on it instead of to every paired device. It is read out
        // of a request the client was making anyway -- no extra field, no extra call, and nothing
        // a paused client discloses, because a paused client sends no batches.
        // A stopped log is the opposite: this device can no longer see anywhere, so it is forgotten
        // rather than credited with the instance it stopped in, and is offered no more alerts for it.
        if (Newest(candidates) is { Type: FactType.InstanceLogStopped })
            locations.Forget(authentication.Device!.Id);
        else if (Newest(candidates)?.InstanceId is { Length: > 0 } here)
            locations.Record(authentication.Device!.Id, here, clock.UtcNow);

        var results = await facts.WriteManyAsync(candidates, ct);

        await RaiseAlertsAsync(
            events, results, candidates, authentication.Device!.Id, devices, alerts, database, clock, ct);

        return Results.Ok(new EventBatchResponse(
            results.Count(r => !r.WasDeduplicated),
            results.Count(r => r.WasDeduplicated),
            rejected));
    }

    /// <summary>
    /// Wakes every other moderator's overlay when somebody with prior actions walks in.
    /// </summary>
    /// <remarks>
    /// <para><strong>Only genuine arrivals, and only the first report of one.</strong> A
    /// presence-observed is somebody who was already there when a moderator arrived, so alerting
    /// on it would fire a card for the whole instance every time any moderator walked in -- the same
    /// mistake, one layer up, that the type distinction exists to prevent. A deduplicated join is
    /// one another client already reported and already alerted on, so alerting again would
    /// interrupt six times for one arrival. An overlay that interrupts constantly gets disabled,
    /// and a disabled overlay notifies nobody.</para>
    /// <para><strong>Only the moderators in that instance.</strong> Every live device is offered
    /// to the hub, which keeps for itself the decision about which of them are standing there.
    /// A moderator in a different instance cannot act on the card and has no business being told
    /// which instance a colleague is in or who just walked into it.</para>
    /// <para>A failure here is swallowed: an alert is a convenience on top of ingest, and losing
    /// one must never cost a fact that cannot be filled in later.</para>
    /// </remarks>
    private static async Task RaiseAlertsAsync(
        IReadOnlyList<CompanionEventDto> submitted,
        IReadOnlyList<FactWriteResult> results,
        IReadOnlyList<FactRecord> written,
        Guid reportingDeviceId,
        ICompanionDeviceStore devices,
        AlertHub alerts,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        var arrivals = written
            .Where((fact, index) => fact.Type == FactType.InstanceJoined && !results[index].WasDeduplicated)
            .ToList();

        if (arrivals.Count == 0)
            return;

        var priorActions = await ContextHandler.CountPriorActionsAsync(
            database, [.. arrivals.Select(a => a.SubjectId).Distinct(StringComparer.Ordinal)], ct);

        if (priorActions.Count == 0)
            return;

        var ranks = await ContextHandler.TrustRanksAsync(database, [.. priorActions.Keys], ct);

        var paired = await devices.ListDevicesAsync(ct);
        var recipients = paired.Where(d => !d.IsRevoked).Select(d => d.Id).ToList();

        foreach (var arrival in arrivals)
        {
            if (priorActions.GetValueOrDefault(arrival.SubjectId) is not (> 0 and var count))
                continue;

            var name = submitted
                .FirstOrDefault(e => e.SubjectId == arrival.SubjectId)?
                .Data?.GetValueOrDefault("displayName");

            alerts.Raise(
                AlertHub.ForFlaggedJoin(
                    clock, arrival.SubjectId, name, arrival.InstanceId!, count, ranks.GetValueOrDefault(arrival.SubjectId)),
                reportingDeviceId,
                recipients,
                clock.UtcNow);
        }
    }

    /// <summary>
    /// The latest thing in the batch, which is where the reporting moderator was standing when
    /// they observed it.
    /// </summary>
    /// <remarks>
    /// A batch can span two instances, because a moderator may walk from one into another between
    /// flushes. The newest event names the one they ended up in; ties break towards the end of the
    /// list, which is the order the client observed them in.
    /// </remarks>
    private static FactRecord? Newest(IReadOnlyList<FactRecord> facts)
    {
        FactRecord? newest = null;
        foreach (var fact in facts)
        {
            if (newest is null || fact.OccurredAt >= newest.OccurredAt)
                newest = fact;
        }

        return newest;
    }

    private static string Reason(CompanionEventDto submitted, string managedGroupId)
        => submitted.GroupId is { Length: > 0 } group && !string.Equals(group, managedGroupId, StringComparison.Ordinal)
            ? "unknown_group"
            : "malformed_event";

    /// <summary>
    /// Turns one submitted event into a fact, or returns null when it is not one this server will
    /// record.
    /// </summary>
    /// <remarks>
    /// Ids are never validated for shape — legacy VRChat ids follow no structure — so the checks
    /// here are about presence and about ownership, never about format.
    /// </remarks>
    private static FactRecord? ToFact(
        CompanionEventDto submitted,
        string managedGroupId,
        Guid deviceId,
        DateTimeOffset now)
    {
        if (submitted is not { SubjectId.Length: > 0, WorldId.Length: > 0, InstanceId.Length: > 0 })
            return null;

        // The one authorisation check on the payload itself. A pairing sees exactly one group,
        // and an event for another one is a client bug or a hostile caller.
        if (!string.Equals(submitted.GroupId, managedGroupId, StringComparison.Ordinal))
            return null;

        if (ToFactType(submitted.Type) is not { } type)
            return null;

        var data = new JsonObject
        {
            // Which device reported it, so a misbehaving client's facts are revocable as a set.
            [ClientReport.DeviceIdKey] = deviceId.ToString(),
        };

        // Clamped and flagged, never rejected and never trusted. A moderator whose PC clock is
        // years out still sees genuine arrivals, and their observations are worth keeping -- but
        // an unclamped timestamp would land outside the fact log's partitions and fail the insert,
        // which the client reads as server trouble and retries forever, filling its buffer while
        // nothing is ever recorded. The claim is kept beside the fact so the correction is visible
        // in the data rather than being a silent rewrite.
        var occurredAt = Clamp(submitted.OccurredAt, now);
        if (occurredAt != submitted.OccurredAt)
        {
            data["clockClamped"] = true;
            data["claimedOccurredAt"] = submitted.OccurredAt.ToString("O");
        }

        foreach (var (key, value) in submitted.Data ?? [])
        {
            // Only the fields the protocol declares. An unknown key is dropped rather than stored:
            // an ingest endpoint that writes whatever it is handed is a storage surface for
            // anything holding a device token.
            if (key is "displayName" or "avatarName" && value.Length > 0)
                data[key] = value;
        }

        return new FactRecord
        {
            Type = type,
            OccurredAt = occurredAt,

            // "Already here when I arrived" has an unknown *lower* bound, not an upper one. The
            // person was present at this instant and arrived at some earlier, unknown time, so
            // there is no occurredBefore to state and inventing one would add precision in the
            // wrong direction.
            OccurredBefore = submitted.OccurredBefore is { } before ? Clamp(before, now) : null,
            SubjectPlatform = FactPlatform.VRChat,
            SubjectId = submitted.SubjectId,
            WorldId = submitted.WorldId,
            InstanceId = submitted.InstanceId,
            Source = FactSource.Companion,
            Data = data,
        };
    }

    private static DateTimeOffset Clamp(DateTimeOffset claimed, DateTimeOffset now)
        => claimed < now - MaxBackdate ? now - MaxBackdate
         : claimed > now + MaxSkewAhead ? now + MaxSkewAhead
         : claimed;

    private static string? ToFactType(string? wireType) => wireType switch
    {
        "InstanceJoined" => FactType.InstanceJoined,
        "InstancePresenceObserved" => FactType.InstancePresenceObserved,
        "InstanceLeft" => FactType.InstanceLeft,
        "AvatarChanged" => FactType.AvatarChanged,
        "LogStopped" => FactType.InstanceLogStopped,
        _ => null,
    };
}
