using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Modbot.Core.Supporters;

/// <summary>
/// What Ko-fi sends when somebody pays: the fields Modbot reads from its message. Ko-fi puts the
/// message in a form field called <c>data</c> holding this as JSON. Fields Modbot does not use are
/// ignored, and every field may be missing or null.
/// </summary>
public sealed class KofiPayment
{
    [JsonPropertyName("verification_token")]
    public string? VerificationToken { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("from_name")]
    public string? FromName { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("is_subscription_payment")]
    public bool IsSubscriptionPayment { get; set; }

    [JsonPropertyName("is_first_subscription_payment")]
    public bool IsFirstSubscriptionPayment { get; set; }

    [JsonPropertyName("kofi_transaction_id")]
    public string? TransactionId { get; set; }

    [JsonPropertyName("tier_name")]
    public string? TierName { get; set; }

    /// <summary>
    /// What the supporter wrote in Ko-fi's message box. Missing or null when they wrote nothing, and
    /// possibly left out when they marked the message private (not checked with Ko-fi).
    /// </summary>
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    /// <summary>What Ko-fi sent for the Discord account: text, or a number if Ko-fi ever sends it as one.</summary>
    [JsonPropertyName("discord_userid")]
    public JsonElement? DiscordUserIdRaw { get; set; }

    /// <summary>The Discord account the supporter linked on Ko-fi, when they did.</summary>
    [JsonIgnore]
    public string? DiscordUserId => DiscordUserIdRaw is { } raw
        ? raw.ValueKind switch
        {
            JsonValueKind.String => raw.GetString()?.Trim() is { Length: > 0 } text ? text : null,
            JsonValueKind.Number => raw.GetRawText(),
            _ => null,
        }
        : null;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The message as JSON, or null when it is not one Modbot can read.</summary>
    public static KofiPayment? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<KofiPayment>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>When the payment happened, if Ko-fi said and it can be read.</summary>
    public DateTimeOffset? PaidAt()
        => DateTimeOffset.TryParse(Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
}
