using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Cloud.Features.LogBackup;

/// <summary>
/// One <c>POST /api/v1/logs</c> body, as the client sends it (cloud log backup spec 2).
/// </summary>
/// <remarks>
/// Every field is nullable here because this is untrusted input; <see cref="LogBatchCheck"/> decides
/// what is required.
/// </remarks>
/// <param name="SentAt">The client PC's clock when it sent this, uncorrected.</param>
/// <param name="ClockOffsetMs">The client's own measure of how far its clock is from Cloud's.</param>
/// <param name="ModbotServerId">The paired server's id, when the client has one.</param>
public sealed record LogBatch(
    [property: JsonPropertyName("batchId")] string? BatchId,
    [property: JsonPropertyName("clientVersion")] string? ClientVersion,
    [property: JsonPropertyName("sentAt")] DateTimeOffset? SentAt,
    [property: JsonPropertyName("clockOffsetMs")] long? ClockOffsetMs,
    [property: JsonPropertyName("clockConfidence")] string? ClockConfidence,
    [property: JsonPropertyName("modbotServerId")] string? ModbotServerId,
    [property: JsonPropertyName("lines")] IReadOnlyList<LogBatchLine?>? Lines);

/// <param name="File">VRChat's file name, without a folder.</param>
/// <param name="Offset">Byte offset of the line's first byte in the file.</param>
/// <param name="LoggedAt">The line's timestamp as written, with no offset.</param>
/// <param name="UtcOffsetMinutes">The PC's UTC offset at that time.</param>
public sealed record LogBatchLine(
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("offset")] long? Offset,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("loggedAt")] DateTime? LoggedAt,
    [property: JsonPropertyName("utcOffsetMinutes")] int? UtcOffsetMinutes,
    [property: JsonPropertyName("event")] LogBatchEvent? Event);

/// <param name="Type">The client's own name for the event, e.g. <c>PlayerJoined</c>.</param>
public sealed record LogBatchEvent(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("data")] JsonElement? Data);

/// <summary>What happened to a batch's lines.</summary>
public sealed record LogBatchResponse(
    [property: JsonPropertyName("stored")] int Stored,
    [property: JsonPropertyName("duplicates")] int Duplicates);
