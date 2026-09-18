namespace Modbot.VRChat.Proxy;

/// <summary>Whose VRChat session a forwarded request goes out on.</summary>
public enum VRChatProxyAccount
{
    /// <summary>Modbot's own service account, through the gate's one session.</summary>
    Service,

    /// <summary>
    /// The caller's own, carried in the cookies they sent. Never the service account's session,
    /// never its cookie jar: a different person, on their own allowance.
    /// </summary>
    Caller,
}

/// <summary>
/// One request to forward to VRChat, as the caller wrote it (VRChat proxy design).
/// </summary>
/// <param name="Method">GET, POST, PUT or DELETE.</param>
/// <param name="Path">The path under <c>https://api.vrchat.cloud/</c>, without a leading slash: <c>api/1/users/usr_…</c>.</param>
/// <param name="Query">The query string, <c>?</c> included, or empty.</param>
/// <param name="Headers">
/// The caller's request headers. Only the ones <see cref="VRChatProxyCall"/> allows are sent on;
/// a caller's <c>Authorization</c> and, on the service account, its <c>Cookie</c> never are.
/// </param>
/// <param name="Body">The request body, or null for none.</param>
/// <param name="ContentType">The body's content type, when there is a body.</param>
public sealed record VRChatProxyRequest(
    string Method,
    string Path,
    string Query,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[]? Body,
    string? ContentType);

/// <summary>What VRChat answered, as it answered it.</summary>
/// <param name="StatusCode">VRChat's own status, whatever it was.</param>
/// <param name="Headers">The response headers worth passing back. Never <c>Set-Cookie</c> from the service account.</param>
/// <param name="Body">The body, bytes as received.</param>
/// <param name="ContentType">The body's content type, when VRChat said.</param>
public sealed record VRChatProxyResponse(
    int StatusCode,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    byte[] Body,
    string? ContentType);
