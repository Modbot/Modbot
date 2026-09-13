using System.Text.Json.Nodes;
using Modbot.Core.Data.Entities;

namespace Modbot.Analytics.Facts;

/// <summary>
/// One fact, as the caller describes it. What <see cref="IFactWriter"/> takes.
/// </summary>
/// <remarks>
/// Deliberately not the entity. <c>observed_at</c> is missing because the server sets it from
/// <c>IModbotClock</c> and never accepts it from a caller (spec 4.4) -- a moderator PC with a
/// wrong clock must not be able to reorder the log -- and the id is missing because the database
/// assigns it.
/// </remarks>
public sealed record FactRecord
{
    public required string Type { get; init; }

    /// <summary>
    /// The upstream system's own word for the event, when <see cref="Type"/> is
    /// <see cref="FactType.Unrecognised"/>. Null when Modbot understood it.
    /// </summary>
    public string? TypeRaw { get; init; }

    /// <summary>When it happened; the lower bound when the time is not exactly known.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// Null for an exact time. Set it whenever the time is an inference -- a sync diff knows only
    /// that something happened between two polls, and recording that as an instant invents
    /// precision that later shows up as fake spikes in hourly charts (spec 5.3).
    /// </summary>
    public DateTimeOffset? OccurredBefore { get; init; }

    public required FactPlatform SubjectPlatform { get; init; }

    /// <summary>Opaque. Never validated, never normalised, never generated (spec 3.1.1).</summary>
    public required string SubjectId { get; init; }

    public FactPlatform? ActorPlatform { get; init; }

    public string? ActorId { get; init; }

    public string? WorldId { get; init; }

    /// <summary>Unique only within <see cref="WorldId"/>, so the two travel together.</summary>
    public string? InstanceId { get; init; }

    public required FactSource Source { get; init; }

    /// <summary>
    /// Type-specific payload. Null becomes an empty object.
    /// </summary>
    /// <remarks>
    /// Never put a secret here, or a masked one (spec 5.9.3): a config-change fact records which
    /// setting changed and by whom, and for secret-bearing fields nothing more.
    /// </remarks>
    public JsonObject? Data { get; init; }
}

/// <param name="Id">
/// The fact this report belongs to -- the surviving one when it was a duplicate, so a client can
/// still correlate what it sent.
/// </param>
/// <param name="WasDeduplicated">
/// True when an existing fact already covered this event and nothing new was written.
/// </param>
public readonly record struct FactWriteResult(long Id, bool WasDeduplicated);
