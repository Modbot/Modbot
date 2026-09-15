using Modbot.Client.Instances;
using Modbot.Client.Time;

namespace Modbot.Client.Ingest;

/// <summary>Where a <c>clientEventId</c> comes from.</summary>
/// <remarks>
/// An interface so tests can be deterministic. The real one is random rather than derived from the
/// event's contents — protocol open question 3 asks whether content-derived would be better, and
/// the answer depends on how often a restart lands mid-batch, which nobody knows yet. Random is
/// already correct for the case that matters (a retry), because the id is written to the durable
/// buffer with the event and survives a restart.
/// </remarks>
public interface IClientEventIdSource
{
    string Next();
}

/// <summary>Random ids. No clock is read, deliberately: this must not be a covert timestamp.</summary>
public sealed class RandomClientEventIdSource : IClientEventIdSource
{
    public string Next() => Guid.NewGuid().ToString("n");
}

/// <summary>
/// Turns an observation into the exact object that will be sent.
/// </summary>
/// <remarks>
/// <para><strong>This is the last point at which anything is added</strong>, so it is the place to
/// check what is sent. Per event, and nothing else: the VRChat user id, world id, instance id,
/// group id, a corrected timestamp, the display name, and for an avatar change the avatar's display
/// name.</para>
/// <para><strong>What is dropped here, on purpose.</strong> The raw log line, which was never kept
/// this far anyway. The instance's raw location string, which for non-group instances carries the
/// instance secret. Anything about instances belonging to no group. And avatar ids, which VRChat
/// does not put in the log at all.</para>
/// <para><see cref="MapAnyInstance"/> skips the group check, for the Modbot Cloud backup only. It adds
/// nothing: the same fields, with no group id for an instance that has none. The instance secret is
/// still gone, because it is discarded when the location is parsed.</para>
/// <para>The mapping runs once per destination server, because each server has its own measured
/// clock offset and its own idempotency scope.</para>
/// </remarks>
public sealed class PresenceEventMapper
{
    private readonly LogTimestampConverter _timestamps;
    private readonly ServerClock _clock;
    private readonly IClientEventIdSource _ids;

    public PresenceEventMapper(
        LogTimestampConverter timestamps,
        ServerClock clock,
        IClientEventIdSource? ids = null)
    {
        _timestamps = timestamps;
        _clock = clock;
        _ids = ids ?? new RandomClientEventIdSource();
    }

    /// <summary>
    /// Builds the wire event, or returns <c>null</c> when the observation is not something any
    /// server may be told about — which is any instance with no owning group.
    /// </summary>
    public ClientEvent? Map(ObservedPresence observation)
    {
        if (observation.Instance.GroupId is not { } groupId)
            return null;

        return Build(observation, groupId);
    }

    /// <summary>
    /// Builds the same wire event for any instance, group or not. Used only by the Modbot Cloud
    /// backup, which can be turned off on this PC; never by anything that sends to a Modbot server.
    /// </summary>
    public ClientEvent MapAnyInstance(ObservedPresence observation) => Build(observation, observation.Instance.GroupId);

    private ClientEvent Build(ObservedPresence observation, string? groupId)
    {
        // Local wall-clock text to a real instant, then to the server's clock. Two corrections,
        // both necessary before this timestamp can be compared with another machine's.
        var occurredAt = _clock.ToServerTime(_timestamps.ToInstant(observation.OccurredAtLocal));

        var data = new Dictionary<string, string>(StringComparer.Ordinal);

        // The display name at this moment, carried as history rather than as identity. Deliberately
        // omitted when the log did not give one -- an empty string would look like a name.
        if (observation.DisplayName is { Length: > 0 } displayName)
            data["displayName"] = displayName;

        if (observation.AvatarName is { Length: > 0 } avatarName)
            data["avatarName"] = avatarName;

        return new ClientEvent
        {
            ClientEventId = _ids.Next(),
            Type = ToWireType(observation.Kind),
            OccurredAt = occurredAt,

            // Always null. The client's one imprecise fact is presence-observed, and its unknown
            // bound is the lower one -- the person arrived at some earlier, unknown time. There is
            // no upper bound to state, so stating one would invent precision in the wrong
            // direction.
            OccurredBefore = null,
            SubjectId = observation.SubjectId,
            WorldId = observation.Instance.WorldId,
            InstanceId = observation.Instance.InstanceId,
            GroupId = groupId,
            Data = data,
        };
    }

    private static ClientEventType ToWireType(PresenceKind kind) => kind switch
    {
        PresenceKind.Joined => ClientEventType.InstanceJoined,
        PresenceKind.PresenceObserved => ClientEventType.InstancePresenceObserved,
        PresenceKind.Left => ClientEventType.InstanceLeft,
        PresenceKind.AvatarChanged => ClientEventType.AvatarChanged,
        PresenceKind.LogStopped => ClientEventType.LogStopped,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown presence kind."),
    };
}
