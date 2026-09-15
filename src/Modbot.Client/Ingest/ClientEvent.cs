using System.Text.Json.Serialization;

namespace Modbot.Client.Ingest;

/// <summary>The five things a client ever reports. Protocol section 4.2.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClientEventType>))]
public enum ClientEventType
{
    /// <summary>A genuine arrival, seen while the moderator was already watching. Exact.</summary>
    InstanceJoined,

    /// <summary>
    /// This person was already here when the moderator arrived. Arrival time unknown and earlier.
    /// This is the type that stops VRChat's phantom bursts becoming fake joins.
    /// </summary>
    InstancePresenceObserved,

    /// <summary>A genuine departure. Exact.</summary>
    InstanceLeft,

    /// <summary>An avatar display name. VRChat does not put avatar ids in the log.</summary>
    AvatarChanged,

    /// <summary>
    /// VRChat's log stopped while the moderator was in this instance. The subject is the moderator
    /// and the time is the log's last line. Sent once when the log stops, not repeated while it
    /// stays stopped, and never sent as a "still here" signal.
    /// </summary>
    /// <remarks>
    /// Added after the first servers shipped. A server that does not know it rejects this one
    /// event as malformed and accepts the rest of the batch (protocol section 4.2.1), so sending it
    /// to an older server costs one rejected line and nothing else.
    /// </remarks>
    LogStopped,
}

/// <summary>
/// One fact, as it goes onto the wire.
/// </summary>
/// <remarks>
/// <para><strong>This type is the complete list of what leaves the machine.</strong> If a field is
/// not here, it is not transmitted — and that is the check worth making when reading this client
/// with suspicion. There is no raw log line here, no chat, no friends list, no avatar id, no instance secret, no file path, no machine
/// name, and no process list.</para>
/// <para>Concretely, per event: an opaque VRChat user id, the world and instance it happened in,
/// the group that owns that instance, a timestamp, and — in <c>Data</c> — the display name that
/// user had at the time, plus the avatar name for an avatar change. The display name is carried
/// because the pairing of id to name at a point in time is useful history: it is what lets a
/// moderator searching for a name somebody used six months ago find them. It is never used as
/// identity, because names are mutable and collide.</para>
/// <para><strong>Where it goes.</strong> To a paired Modbot server, only for that server's group's
/// instances. And, unless the moderator turns it off, to Modbot Cloud as a backup for every instance
/// the moderator is in, group or not (<c>CloudEventBackup</c>).</para>
/// </remarks>
public sealed record ClientEvent
{
    /// <summary>
    /// The idempotency key. Generated once, when the event is first created, and kept through every
    /// retry — including across a client restart, because it is written to the durable buffer
    /// alongside the event. A retried batch is therefore safe: the server recognises the repeat.
    /// </summary>
    /// <remarks>
    /// This is not the same mechanism as the server's windowed deduplication, and neither
    /// substitutes for the other. This one handles <em>this</em> client retrying after a timeout;
    /// the window handles six moderators reporting the same join. Protocol section 4.3.
    /// </remarks>
    [JsonPropertyName("clientEventId")]
    public required string ClientEventId { get; init; }

    [JsonPropertyName("type")]
    public required ClientEventType Type { get; init; }

    /// <summary>
    /// When it happened, already corrected to the server's clock. For
    /// <see cref="ClientEventType.InstancePresenceObserved"/> this is the moment the person was
    /// seen, not the moment they arrived — which is unknown and earlier.
    /// </summary>
    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// The upper bound of an uncertainty window, or <c>null</c> when the timestamp is exact.
    /// Always null here: the client's imprecise case is presence-observed, whose <em>lower</em>
    /// bound is the unknown one, and there is no upper bound to state.
    /// </summary>
    [JsonPropertyName("occurredBefore")]
    public DateTimeOffset? OccurredBefore { get; init; }

    /// <summary>The VRChat user this is about. Opaque; never validated for shape.</summary>
    [JsonPropertyName("subjectId")]
    public required string SubjectId { get; init; }

    [JsonPropertyName("worldId")]
    public required string WorldId { get; init; }

    /// <summary>Unique within the world, not globally, which is why both are sent.</summary>
    [JsonPropertyName("instanceId")]
    public required string InstanceId { get; init; }

    /// <summary>
    /// The owning group — the routing decision, already made on this machine. Never null in an event
    /// sent to a Modbot server: an event with no group is never created for one. Null only in the
    /// Modbot Cloud backup, which carries every instance, group or not (cloud event backup spec 2).
    /// </summary>
    [JsonPropertyName("groupId")]
    public required string? GroupId { get; init; }

    /// <summary>Type-specific, small, and enumerated in the remarks on this type.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyDictionary<string, string> Data { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
