namespace Modbot.VRChat;

/// <summary>
/// The outcome of one call through the gate.
/// </summary>
/// <remarks>
/// <para>
/// Spec 4.1's shape. Nothing throws: <c>VRChat.API</c>'s <c>...WithHttpInfoAsync</c> methods catch
/// <c>ApiException</c> internally and return a non-success <c>ApiResponse</c>, so the gate has a
/// status for every outcome and callers get a value to branch on rather than an exception to
/// forget to catch.
/// </para>
/// <para>
/// <see cref="StatusCode"/> is <c>0</c> when no request was issued at all — a cold stop, an
/// unconfigured account, or a transport failure. That is deliberately not <c>429</c>: a caller
/// that reported a fabricated 429 back to the limiter would compound a penalty out of a request
/// that never happened.
/// </para>
/// </remarks>
/// <param name="Success">True only for a 2xx response.</param>
/// <param name="Value">The deserialised body, when there was one.</param>
/// <param name="StatusCode">The HTTP status, or 0 when nothing was sent.</param>
/// <param name="ErrorMessage">A sentence for an operator, not a stack trace.</param>
/// <param name="WafCode">
/// Cloudflare's classification when the response was a WAF block rather than a VRChat error.
/// Distinct because the remedy is completely different: a proxy fixes this and nothing else
/// (spec 2.3.1, 7.1.1).
/// </param>
/// <param name="RawResponse">The body as it arrived, for diagnosis.</param>
/// <param name="Kind">
/// Why it failed, at the granularity a remedy differs on. The status code cannot express this:
/// every transport failure — DNS, timeout, refused connection, a proxy that will not tunnel —
/// arrives with no response and therefore with <see cref="StatusCode"/> <c>0</c>, and spec 7.1.1
/// requires the connection check to tell them apart.
/// </param>
public readonly record struct VRChatResult<T>(
    bool Success,
    T? Value,
    int StatusCode,
    string? ErrorMessage,
    int? WafCode,
    string? RawResponse,
    VRChatFailureKind Kind = VRChatFailureKind.None)
{
    /// <summary>True when VRChat rate limited this call. Never retry it (spec 4.3.1).</summary>
    public bool IsRateLimited => StatusCode == 429;

    /// <summary>True when Cloudflare refused the request before VRChat ever saw it.</summary>
    public bool IsWafBlocked => WafCode is not null;

    /// <summary>True when the gate declined to issue the request at all.</summary>
    public bool WasNotSent => StatusCode == 0;

    public static VRChatResult<T> Ok(T? value, int statusCode, string? rawResponse = null) =>
        new(true, value, statusCode, null, null, rawResponse);

    public static VRChatResult<T> Failure(
        int statusCode,
        string errorMessage,
        int? wafCode = null,
        string? rawResponse = null,
        VRChatFailureKind kind = VRChatFailureKind.Other) =>
        new(false, default, statusCode, errorMessage, wafCode, rawResponse, kind);

    /// <summary>Re-types a failure so it can be returned from a differently-typed call.</summary>
    public static VRChatResult<T> From<TOther>(VRChatResult<TOther> other) =>
        new(false, default, other.StatusCode, other.ErrorMessage, other.WafCode, other.RawResponse, other.Kind);
}

/// <summary>
/// What the gate would tell an operator about itself (spec 4.1, 4.3.3).
/// </summary>
/// <remarks>
/// These are shown in the UI, so each one has to imply a different action. "Slow because it is
/// deliberately waiting out a rate limit" and "broken because Cloudflare is blocking this host"
/// look identical in a log full of failures and could not be more different to fix.
/// </remarks>
public enum VRChatSessionState
{
    /// <summary>No VRChat account configured yet, or the one configured was rejected.</summary>
    Unconfigured,

    /// <summary>Working normally.</summary>
    Healthy,

    /// <summary>Logging in, or refreshing an expired session.</summary>
    Reauthenticating,

    /// <summary>At least one bucket is cold-stopped. Not broken — waiting, on purpose.</summary>
    RateLimited,

    /// <summary>Cloudflare is blocking this host's network. The fix is a proxy (spec 2.3.1).</summary>
    WafBlocked,
}
