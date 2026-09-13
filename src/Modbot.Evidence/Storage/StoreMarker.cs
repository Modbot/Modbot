using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Evidence.Storage;

/// <summary>
/// The one object in the store that says which store this is (design section 8.2).
/// </summary>
/// <param name="StoreId">
/// Generated once, when the backend was set up, and recorded in <c>Settings</c> as well as
/// here. Two copies in two places is the entire mechanism.
/// </param>
/// <param name="CreatedAt">When it was written. Supplied by <c>IModbotClock</c>.</param>
/// <param name="Deployment">
/// A human-readable name for the deployment that wrote it, so an operator staring at two buckets
/// can tell which is which without decoding a GUID.
/// </param>
public sealed record StoreMarker(
    [property: JsonPropertyName("storeId")] Guid StoreId,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("deployment")] string? Deployment)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Small on purpose. Every backend has to be able to write it in one operation, and the
    /// database backend keeps it in a single row.
    /// </summary>
    public byte[] Serialise() => JsonSerializer.SerializeToUtf8Bytes(this, Json);

    /// <summary>
    /// Returns null when the bytes are not a store marker at all.
    /// </summary>
    /// <remarks>
    /// A malformed store marker is not treated as "no store marker". Something wrote a file at Modbot's
    /// well-known key and it is not Modbot's, which is a finding of the same kind as an id that
    /// does not match — see <see cref="StoreProbeOutcome.Malformed"/>.
    /// </remarks>
    public static StoreMarker? TryParse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<StoreMarker>(utf8, Json);
            return parsed is null || parsed.StoreId == Guid.Empty ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
