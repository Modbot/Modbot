using System.Text.Json;

namespace Modbot.VRChat;

/// <summary>
/// Reads VRChat's own reason out of the body of a call it refused.
/// </summary>
/// <remarks>
/// <para>
/// VRChat answers a refusal with <c>{"error":{"message":"…","status_code":400}}</c>. The gate's
/// <see cref="VRChatResult{T}.ErrorMessage"/> carries only the HTTP reason ("Bad Request"), which
/// says nothing about what VRChat did not like, so a moderator looking at a failed write learns
/// nothing from it.
/// </para>
/// <para>
/// The message is for showing, never for matching: VRChat writes it with look-alike punctuation
/// (see <c>GroupAuditLogSync.AuditLogOffsetCap</c>).
/// </para>
/// </remarks>
public static class VRChatRefusal
{
    /// <summary>VRChat's <c>error.message</c>, or null when the body has none.</summary>
    public static string? MessageOf(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error))
                return null;

            // Usually an object with a message; now and then just a string.
            var message = error.ValueKind switch
            {
                JsonValueKind.Object when error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String => m.GetString(),
                JsonValueKind.String => error.GetString(),
                _ => null,
            };

            return string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        }
        catch (JsonException)
        {
            // An HTML error page from a proxy or Cloudflare, not VRChat's JSON.
            return null;
        }
    }
}
