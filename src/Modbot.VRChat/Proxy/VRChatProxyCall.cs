using System.Net.Http.Headers;
using System.Text;

namespace Modbot.VRChat.Proxy;

/// <summary>
/// Turns a <see cref="VRChatProxyRequest"/> into the HTTP request that goes to VRChat, and
/// VRChat's answer into a <see cref="VRChatProxyResponse"/>, applying the header rules the
/// VRChat proxy design sets out.
/// </summary>
/// <remarks>
/// <para>
/// Headers are allowed through by name, not refused by name. A denylist of hop-by-hop headers
/// would still have forwarded whatever a caller's client library adds next year -- a tracing
/// header, a second credential -- and the request goes out under Modbot's own name and, on the
/// service account, with Modbot's own session. What a caller may say to VRChat is what an API
/// call says: what it accepts, what it sends, and what it already has.
/// </para>
/// <para>
/// Two things are never forwarded whatever the list says. The caller's <c>Authorization</c>
/// header, because on this route it is a Modbot API key. And, when the request goes out as the
/// service account, the caller's <c>Cookie</c> header -- which is where a VRChat client library
/// puts the Modbot key it was given as an <c>auth</c> cookie. The session cookies come from the
/// gate's own jar, and a <c>Set-Cookie</c> VRChat sends back is kept in that jar and never
/// returned, because it is the service account's session.
/// </para>
/// </remarks>
public static class VRChatProxyCall
{
    /// <summary>Request headers a caller may send on. Everything else is dropped.</summary>
    public static readonly IReadOnlySet<string> AllowedRequestHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Accept",
        "Accept-Language",
        "If-None-Match",
        "If-Modified-Since",
    };

    /// <summary>Response headers passed back. Everything else stays on this side.</summary>
    public static readonly IReadOnlySet<string> AllowedResponseHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Type",
        "ETag",
        "Last-Modified",
        "Cache-Control",
        "Retry-After",
        "CF-Ray",
    };

    /// <summary>
    /// The most of a body the proxy reads in either direction. VRChat's API answers in
    /// kilobytes; a request this size is an upload, and uploads are not what the proxy is for.
    /// </summary>
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    /// <summary>The URL a forwarded path becomes: the API's host, the path, the query.</summary>
    /// <remarks>
    /// Built with <see cref="UriBuilder"/> rather than by joining strings or resolving a relative
    /// URI, so a path beginning <c>//other.host/</c> stays a path on VRChat's host rather than
    /// becoming a request to somebody else.
    /// </remarks>
    public static Uri Destination(Uri apiHost, string path, string query)
    {
        ArgumentNullException.ThrowIfNull(apiHost);
        ArgumentNullException.ThrowIfNull(path);

        var builder = new UriBuilder(apiHost)
        {
            Path = "/" + path.TrimStart('/'),
            Query = string.IsNullOrEmpty(query) ? string.Empty : query.TrimStart('?'),
        };

        return builder.Uri;
    }

    /// <summary>The request that goes to VRChat.</summary>
    /// <param name="apiHost">Scheme and host of VRChat's API, from the gate's client configuration.</param>
    /// <param name="request">What the caller sent.</param>
    /// <param name="userAgent">The gate's own User-Agent. Always Modbot's, never the caller's.</param>
    /// <param name="defaultHeaders">The gate's fixed headers: the developer contact.</param>
    /// <param name="account">
    /// On <see cref="VRChatProxyAccount.Caller"/> the caller's <c>Cookie</c> header goes through;
    /// on the service account it never does.
    /// </param>
    public static HttpRequestMessage Build(
        Uri apiHost,
        VRChatProxyRequest request,
        string userAgent,
        IEnumerable<KeyValuePair<string, string>>? defaultHeaders,
        VRChatProxyAccount account)
    {
        ArgumentNullException.ThrowIfNull(request);

        var message = new HttpRequestMessage(
            new HttpMethod(request.Method.ToUpperInvariant()),
            Destination(apiHost, request.Path, request.Query));

        foreach (var (name, value) in request.Headers)
        {
            if (AllowedRequestHeaders.Contains(name)
                || (account == VRChatProxyAccount.Caller && name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
            message.Headers.TryAddWithoutValidation("User-Agent", userAgent);

        foreach (var (name, value) in defaultHeaders ?? [])
            message.Headers.TryAddWithoutValidation(name, value);

        if (request.Body is { } body)
        {
            message.Content = new ByteArrayContent(body);

            if (!string.IsNullOrWhiteSpace(request.ContentType)
                && MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType))
            {
                message.Content.Headers.ContentType = contentType;
            }
        }

        return message;
    }

    /// <summary>VRChat's answer, with only the headers worth passing back.</summary>
    /// <param name="account">
    /// On <see cref="VRChatProxyAccount.Caller"/> a <c>Set-Cookie</c> is theirs and goes back to
    /// them; on the service account it is Modbot's session and never leaves.
    /// </param>
    public static async Task<VRChatProxyResponse> ReadAsync(
        HttpResponseMessage response, VRChatProxyAccount account, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        var headers = new List<KeyValuePair<string, string>>();

        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            var allowed = AllowedResponseHeaders.Contains(name)
                || (account == VRChatProxyAccount.Caller && name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase));

            if (!allowed)
                continue;

            foreach (var value in values)
                headers.Add(new KeyValuePair<string, string>(name, value));
        }

        var body = await ReadBodyAsync(response.Content, ct).ConfigureAwait(false);

        return new VRChatProxyResponse(
            (int)response.StatusCode,
            headers,
            body,
            response.Content.Headers.ContentType?.ToString());
    }

    /// <summary>The body as text for the gate's classifier, when it is text at all.</summary>
    public static string? TextOf(VRChatProxyResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.Body.Length == 0)
            return null;

        var type = response.ContentType ?? string.Empty;
        var textual = type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                      || type.Contains("json", StringComparison.OrdinalIgnoreCase)
                      || type.Contains("xml", StringComparison.OrdinalIgnoreCase);

        if (!textual)
            return null;

        try
        {
            return Encoding.UTF8.GetString(response.Body);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[16 * 1024];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
                throw new InvalidOperationException($"VRChat's answer is larger than {MaxBodyBytes} bytes.");

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
