using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Modbot.Api.Features.Audit;
using Modbot.Core.Data.Entities;

namespace Modbot.Api.Features.Events;

/// <param name="Platform"><c>VRChat</c>, <c>Discord</c> or <c>Modbot</c>.</param>
/// <param name="Id">Opaque. Never parsed, never validated (foundation §3.1.1).</param>
/// <param name="Kind">What the subject is: <c>Person</c>, <c>Instance</c>, <c>Group</c>, <c>Role</c>, <c>Account</c>, <c>Other</c>.</param>
public sealed record EventSubject(string Platform, string Id, string Kind);

/// <param name="Name">The name recorded in the fact at the time, or null. Never looked up now.</param>
public sealed record EventActor(string Platform, string Id, string? Name);

/// <summary>
/// One event, as the WebSocket and webhooks send it (API keys design §4.4). Version 1.
/// </summary>
/// <remarks>
/// Serialised in snake_case by <see cref="EventEnvelopes.JsonOptions"/>. <see cref="Id"/> and
/// <see cref="Cursor"/> are strings because fact ids are 64-bit and a JavaScript number is not.
/// A field may be added to version 1; removing or renaming one is version 2.
/// </remarks>
public sealed record EventEnvelope(
    int Version,
    string Id,
    string? Cursor,
    string Type,
    string? TypeRaw,
    string Label,
    string Category,
    string Source,
    DateTimeOffset OccurredAt,
    DateTimeOffset? OccurredBefore,
    DateTimeOffset ObservedAt,
    EventSubject Subject,
    EventActor? Actor,
    string? WorldId,
    string? InstanceId,
    JsonNode? Data);

public static class EventEnvelopes
{
    public const int Version = 1;

    /// <summary>The type of the event "Send test" delivers. Never a fact.</summary>
    public const string TestType = "modbot.webhook.test";

    /// <summary>snake_case, nulls written out, no indentation. Every WebSocket message and webhook body uses it.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
    };

    public static EventEnvelope From(ModbotEvent fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        var id = fact.Id.ToString(CultureInfo.InvariantCulture);
        var data = AuditJson.Parse(fact.Data);

        return new EventEnvelope(
            Version,
            id,
            id,
            fact.Type,
            fact.TypeRaw,
            FactLabels.For(fact.Type),
            AuditVisibility.CategoryOf(fact.Type) == AuditCategory.Moderation ? "moderation" : "operational",
            fact.Source.ToString(),
            fact.OccurredAt,
            fact.OccurredBefore,
            fact.ObservedAt,
            new EventSubject(fact.SubjectPlatform.ToString(), fact.SubjectId, FactSubjects.For(fact.Type).ToString()),
            fact.ActorPlatform is { } platform && fact.ActorId is { } actorId
                ? new EventActor(platform.ToString(), actorId, AuditJson.Text(data, "actorDisplayName"))
                : null,
            fact.WorldId,
            fact.InstanceId,
            data);
    }

    /// <summary>The event "Send test" delivers: shaped like any other, about the webhook itself.</summary>
    public static EventEnvelope Test(Guid webhookId, Guid attemptId, DateTimeOffset now, string actorId, string actorName) => new(
        Version,
        $"test-{attemptId:N}",
        null,
        TestType,
        null,
        "Webhook test",
        "operational",
        FactSource.Modbot.ToString(),
        now,
        null,
        now,
        new EventSubject(FactPlatform.Modbot.ToString(), webhookId.ToString(), SubjectKind.Other.ToString()),
        new EventActor(FactPlatform.Modbot.ToString(), actorId, actorName),
        null,
        null,
        new JsonObject { ["actorDisplayName"] = actorName });

    public static byte[] Serialize(EventEnvelope envelope)
        => JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
}
