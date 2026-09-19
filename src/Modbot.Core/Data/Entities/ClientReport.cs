using System.Text.Json;
using System.Text.Json.Nodes;

namespace Modbot.Core.Data.Entities;

/// <summary>
/// The one field the server itself puts on a client-reported fact: which client reported it.
/// </summary>
/// <remarks>
/// Written by the companion ingest endpoint and read by the Live page, the audit log and the team
/// figures. The name lives here so those four agree on it by construction rather than by four
/// string literals agreeing by luck.
/// </remarks>
public static class ClientReport
{
    public const string DeviceIdKey = "deviceId";

    /// <summary>The client named on a fact's payload, or null when it names none.</summary>
    public static Guid? DeviceIdOf(JsonObject? data)
        => data is not null
            && data.TryGetPropertyValue(DeviceIdKey, out var value)
            && value?.GetValueKind() == JsonValueKind.String
            && Guid.TryParse(value.GetValue<string>(), out var parsed)
                ? parsed
                : null;

    /// <summary>
    /// The same, from a payload still in its stored form. A payload that will not parse names no
    /// client, rather than taking the write that is reading it down.
    /// </summary>
    public static Guid? DeviceIdOf(string? storedData)
    {
        if (storedData is not { Length: > 2 })
            return null;

        try
        {
            using var document = JsonDocument.Parse(storedData);

            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(DeviceIdKey, out var value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out var parsed)
                    ? parsed
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
