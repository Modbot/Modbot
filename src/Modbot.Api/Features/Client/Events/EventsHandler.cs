using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Modbot.Analytics.Facts;
using Modbot.Api.Features.Client.Alerts;
using Modbot.Api.Features.Client.Context;
using Modbot.Api.Features.Client.Devices;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Time;

namespace Modbot.Api.Features.Client.Events;

public sealed record ClientEventDto(
    [property: JsonPropertyName("clientEventId")] string? ClientEventId,
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
    [property: JsonPropertyName("clientVersion")] string? ClientVersion,
    [property: JsonPropertyName("clockOffsetMs")] long ClockOffsetMs,
    [property: JsonPropertyName("clockConfidence")] string? ClockConfidence,
    [property: JsonPropertyName("events")] IReadOnlyList<ClientEventDto>? Events);

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
    /// The fact type for "this person was already here when I arrived".
    /// </summary>
    /// <remarks>
    /// <para><strong>This is a placeholder for a missing enum member.</strong>
    /// <c>FactType</c> carries <c>InstanceJoined</c>, <c>InstanceLeft</c> and
    /// <c>AvatarChanged</c> but has no member for presence-observed, even though M3 7's table and
    /// the client protocol both list it as one of the four things a client reports. It is the type
    /// that stops VRChat's phantom bursts becoming fake joins, so it cannot simply be mapped onto
    /// <c>InstanceJoined</c>: doing that would inflate arrivals by the instance population every
    /// time any moderator walked into a room, which is the exact failure the distinction
    /// exists to prevent.</para>
    /// <para>203 continues the presence block. The column is a <c>smallint</c> with no database
    /// constraint, so adding the member later changes nothing already written — delete this
    /// constant and use the enum the moment it exists.</para>
    /// </remarks>
    public const FactType InstancePresenceObserved = (FactType)203;

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
        IClientDeviceStore devices,
        AlertHub alerts,
        ModbotContext database,
        IModbotClock clock,
        CancellationToken ct)
    {
        if (!ClientApiVersion.IsSupported(apiVersion))
            return ClientApiErrors.VersionUnsupported(apiVersion);

        var authentication = await authenticator.AuthenticateAsync(context, ct);
        if (!authentication.Succeeded)
            return authentication.Failure!;

        if (batch?.Events is not { Count: > 0 } events)
            return ClientApiErrors.Malformed("A batch must carry at least one event.");

        if (events.Count > MaxEventsPerBatch)
        {
            // 413, so the client halves and retries rather than dropping. Nothing is lost: the
            // events are still in its buffer.
            return Results.Json(
                new ClientError(
                    ClientApiErrors.BatchTooLarge,
                    $"A batch may carry at most {MaxEventsPerBatch} events; this one carried {events.Count}."),
                statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        var settings = await database.GetSettingsAsync(ct);
        if (settings.ManagedGroupId is not { Length: > 0 } managedGroupId)
            return ClientApiErrors.NotReady();

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
    /// on it would fire a card for the whole room every time any moderator walked in -- the same
    /// mistake, one layer up, that the type distinction exists to prevent. A deduplicated join is
    /// one another client already reported and already alerted on, so alerting again would
    /// interrupt six times for one arrival. An overlay that interrupts constantly gets disabled,
    /// and a disabled overlay notifies nobody.</para>
    /// <para>A failure here is swallowed: an alert is a convenience on top of ingest, and losing
    /// one must never cost a fact that cannot be backfilled.</para>
    /// </remarks>
    private static async Task RaiseAlertsAsync(
        IReadOnlyList<ClientEventDto> submitted,
        IReadOnlyList<FactWriteResult> results,
        IReadOnlyList<FactRecord> written,
        Guid reportingDeviceId,
        IClientDeviceStore devices,
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
                AlertHub.ForFlaggedJoin(clock, arrival.SubjectId, name, arrival.InstanceId!, count),
                reportingDeviceId,
                recipients);
        }
    }

    private static string Reason(ClientEventDto submitted, string managedGroupId)
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
        ClientEventDto submitted,
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
            ["deviceId"] = deviceId.ToString(),
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
            Source = FactSource.Client,
            Data = data,
        };
    }

    private static DateTimeOffset Clamp(DateTimeOffset claimed, DateTimeOffset now)
        => claimed < now - MaxBackdate ? now - MaxBackdate
         : claimed > now + MaxSkewAhead ? now + MaxSkewAhead
         : claimed;

    private static FactType? ToFactType(string? wireType) => wireType switch
    {
        "InstanceJoined" => FactType.InstanceJoined,
        "InstancePresenceObserved" => InstancePresenceObserved,
        "InstanceLeft" => FactType.InstanceLeft,
        "AvatarChanged" => FactType.AvatarChanged,
        _ => null,
    };
}
