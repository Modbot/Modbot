using System.Text.Json.Serialization;
using Modbot.Companion.Time;

namespace Modbot.Companion.Ingest;

/// <summary>
/// One POST to one server: <c>POST /api/v{n}/companion/events</c>.
/// </summary>
/// <remarks>
/// <para><strong>This is the entire outbound surface of the client.</strong> There is no other
/// request that carries observations. Overlay reads are separate, read-only and small, and there is
/// no server-to-client command channel at all — the server never tells this client to do anything,
/// which is what keeps the client's behaviour fully described by its own source.</para>
/// <para>A batch goes to exactly one server, and carries only events whose owning group that server
/// declared it manages. Events for another group are not filtered out on receipt; they are never
/// put in the batch.</para>
/// </remarks>
public sealed record EventBatch
{
    /// <summary>Stable across retries of this same batch.</summary>
    [JsonPropertyName("batchId")]
    public required string BatchId { get; init; }

    [JsonPropertyName("companionVersion")]
    public required string CompanionVersion { get; init; }

    /// <summary>
    /// This machine's measured correction to the server's clock, and how much to trust it. Sent so
    /// the server can judge the timestamps rather than guess at them.
    /// </summary>
    [JsonPropertyName("clockOffsetMs")]
    public required long ClockOffsetMs { get; init; }

    [JsonPropertyName("clockConfidence")]
    public required string ClockConfidence { get; init; }

    [JsonPropertyName("events")]
    public required IReadOnlyList<CompanionEvent> Events { get; init; }

    /// <summary>Protocol section 4.4. Bigger batches are split before sending.</summary>
    public const int MaxEvents = 500;

    public static EventBatch Create(
        string batchId,
        string companionVersion,
        ServerClock clock,
        IReadOnlyList<CompanionEvent> events) => new()
        {
            BatchId = batchId,
            CompanionVersion = companionVersion,
            ClockOffsetMs = (long)clock.Offset.TotalMilliseconds,
            ClockConfidence = clock.Confidence.ToWire(),
            Events = events,
        };
}
