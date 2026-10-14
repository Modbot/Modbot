using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Core.Supporters;

/// <summary>
/// What Patreon sends when a membership changes: the fields Modbot reads from its message. The
/// member is in <c>data</c>; the tiers and the user it points to are in <c>included</c>. Fields
/// Modbot does not use are ignored, and every field may be missing or null.
/// </summary>
public sealed class PatreonMessage
{
    [JsonPropertyName("data")]
    public PatreonResource? Data { get; set; }

    [JsonPropertyName("included")]
    public List<PatreonResource>? Included { get; set; }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The message as JSON, or null when it is not one Modbot can read.</summary>
    public static PatreonMessage? Parse(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty)
            return null;

        try
        {
            return JsonSerializer.Deserialize<PatreonMessage>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The member's id, or null when the message names none.</summary>
    [JsonIgnore]
    public string? MemberId => Data?.Id?.Trim() is { Length: > 0 } id ? id : null;

    /// <summary>Patreon's word for where the patron stands: active_patron, declined_patron or former_patron.</summary>
    [JsonIgnore]
    public string? PatronStatus => Data?.Attributes?.PatronStatus?.Trim().ToLowerInvariant();

    /// <summary>The name the patron goes by: the member's own, or else the user's.</summary>
    [JsonIgnore]
    public string? FullName
    {
        get
        {
            if (Data?.Attributes?.FullName?.Trim() is { Length: > 0 } own)
                return own;

            var userId = Data?.Relationships?.User?.Data?.Id;

            return Included?.FirstOrDefault(r => r.Type == "user" && r.Id == userId)?.Attributes?.FullName?.Trim() is { Length: > 0 } theirs
                ? theirs
                : null;
        }
    }

    /// <summary>When the last charge was tried, whatever its outcome, or null if there was none.</summary>
    [JsonIgnore]
    public DateTimeOffset? LastChargeDate
        => DateTimeOffset.TryParse(Data?.Attributes?.LastChargeDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    /// <summary>When the last charge was paid, or null if the last charge was not (declined, pending, refunded) or there was none.</summary>
    [JsonIgnore]
    public DateTimeOffset? LastPaidCharge
        => string.Equals(Data?.Attributes?.LastChargeStatus?.Trim(), "Paid", StringComparison.OrdinalIgnoreCase) ? LastChargeDate : null;

    /// <summary>
    /// The tier to count the patron at: of the tiers they are entitled to now, the one that costs the
    /// most (the first, if the message does not say what they cost). Null when they are entitled to none.
    /// </summary>
    public PatreonTier? EntitledTier()
    {
        var ids = Data?.Relationships?.EntitledTiers?.Data?
            .Select(t => t.Id?.Trim())
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct()
            .ToList();

        if (ids is null || ids.Count == 0)
            return null;

        PatreonTier? best = null;

        foreach (var id in ids)
        {
            var found = Included?.FirstOrDefault(r => r.Type == "tier" && r.Id == id);
            var title = found?.Attributes?.Title?.Trim();
            var tier = new PatreonTier(id, string.IsNullOrEmpty(title) ? null : title, found?.Attributes?.AmountCents ?? -1);

            if (best is null || tier.AmountCents > best.AmountCents)
                best = tier;
        }

        return best;
    }
}

/// <summary>A tier a patron is entitled to. <see cref="Title"/> is null when the message did not carry the tier itself.</summary>
public sealed record PatreonTier(string Id, string? Title, long AmountCents);

/// <summary>One thing in Patreon's message: the member, a tier or a user.</summary>
public sealed class PatreonResource
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("attributes")]
    public PatreonAttributes? Attributes { get; set; }

    [JsonPropertyName("relationships")]
    public PatreonRelationships? Relationships { get; set; }
}

/// <summary>The few attributes Modbot reads, from whichever kind of thing it is.</summary>
public sealed class PatreonAttributes
{
    [JsonPropertyName("full_name")]
    public string? FullName { get; set; }

    [JsonPropertyName("patron_status")]
    public string? PatronStatus { get; set; }

    [JsonPropertyName("last_charge_date")]
    public string? LastChargeDate { get; set; }

    [JsonPropertyName("last_charge_status")]
    public string? LastChargeStatus { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("amount_cents")]
    public long? AmountCents { get; set; }
}

public sealed class PatreonRelationships
{
    [JsonPropertyName("currently_entitled_tiers")]
    public PatreonLinks? EntitledTiers { get; set; }

    [JsonPropertyName("user")]
    public PatreonLink? User { get; set; }
}

public sealed class PatreonLinks
{
    [JsonPropertyName("data")]
    public List<PatreonReference>? Data { get; set; }
}

public sealed class PatreonLink
{
    [JsonPropertyName("data")]
    public PatreonReference? Data { get; set; }
}

public sealed class PatreonReference
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}
