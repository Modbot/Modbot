using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VRChat.API.Client;

namespace Modbot.VRChat.Announcements;

/// <summary>
/// The one call that sends a message to everyone in a group instance:
/// <c>POST /instances/{location}/announce</c>, with <c>title</c> and <c>message</c> in the body.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Written by hand, through the gate.</strong> The VRChat SDK has no method for this
/// endpoint, so the request is built here and sent on the session client's own <c>HttpClient</c>
/// inside <see cref="IVRChatGate.ExecuteAsync{T}"/>, the way the gate forwards a proxied request:
/// the same session and cookie jar, the same User-Agent and developer headers, the same limiter, the
/// same 401 renewal and the same cold stop on a 429. Nothing here holds a cookie or a client of its
/// own. The call shape is the one VRCX sends, and a read-only probe on 2026-10-03 had VRChat answer
/// it (403 while the account lacked the permission).
/// </para>
/// <para>
/// <strong>The location goes in the path as VRChat gave it.</strong> VRChat's firewall answers 400
/// "malformed url" when its colon, tildes or brackets are percent-encoded, so those stay as they
/// are. Only a character that is not allowed in a path segment at all is encoded, so a location
/// can never reach another address: a slash, a question mark, a hash, a percent sign or a space
/// becomes its <c>%XX</c>. Its shape is never checked (foundation §3.1.1).
/// </para>
/// <para>
/// A 429 is a cold stop of <see cref="VRChatEndpointClass.InstancesAnnounce"/> alone, and nothing
/// here sends anything again (spec 4.3.1).
/// </para>
/// </remarks>
public sealed class InstanceAnnounce(IVRChatGate gate)
{
    /// <summary>The operation name the call gives its endpoint, and the key for the permission it needs.</summary>
    public const string Operation = "AnnounceInstance";

    /// <summary>The most of VRChat's answer kept: it answers in a few hundred bytes.</summary>
    public const int MaxAnswerBytes = 64 * 1024;

    private readonly IVRChatGate _gate = gate ?? throw new ArgumentNullException(nameof(gate));

    /// <summary>
    /// Sends one message. The result's <see cref="VRChatResult{T}.RawResponse"/> is VRChat's answer
    /// as text, for its own words when it says no.
    /// </summary>
    /// <param name="groupId">The managed group: the resource the budget is scoped to.</param>
    /// <param name="location">The instance's location exactly as VRChat gave it.</param>
    public Task<VRChatResult<string>> SendAsync(
        string groupId,
        string location,
        string title,
        string message,
        VRChatCallPriority priority,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        if (!CanBePath(location))
        {
            return Task.FromResult(VRChatResult<string>.Failure(
                0, "That instance has no location Modbot can send to.", kind: VRChatFailureKind.Other));
        }

        var body = Body(title, message);

        return _gate.ExecuteAsync(
            new VRChatEndpoint(VRChatEndpointClass.InstancesAnnounce, groupId, Operation),
            async (vrchat, token) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, Address(vrchat.Configuration.BasePath, location));

                if (!string.IsNullOrWhiteSpace(vrchat.Configuration.UserAgent))
                    request.Headers.TryAddWithoutValidation("User-Agent", vrchat.Configuration.UserAgent);

                foreach (var (name, value) in vrchat.Configuration.DefaultHeaders)
                    request.Headers.TryAddWithoutValidation(name, value);

                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                using var response = await vrchat.HttpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                var answer = await ReadAsync(response.Content, token).ConfigureAwait(false);

                // The body as text, so the gate classifies a Cloudflare page and keeps VRChat's own
                // words for a refusal, as it does for an SDK call.
                return new ApiResponse<string>(response.StatusCode, answer, answer);
            },
            priority,
            ct);
    }

    /// <summary>The JSON VRChat is sent: the title and the message, and nothing else (no picture in this build).</summary>
    public static byte[] Body(string title, string message) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = title,
            ["message"] = message,
        });

    /// <summary>
    /// The address: the API's base, <c>/instances/</c>, the location as a path segment, <c>/announce</c>.
    /// </summary>
    public static Uri Address(string basePath, string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        ArgumentNullException.ThrowIfNull(location);

        return new Uri($"{basePath.TrimEnd('/')}/instances/{PathSegment(location)}/announce", UriKind.Absolute);
    }

    /// <summary>
    /// The location as one path segment: every character a path segment allows is left as it is
    /// (letters, digits, <c>-._~</c>, <c>!$&amp;'()*+,;=</c>, <c>:</c> and <c>@</c>, RFC 3986's
    /// <c>pchar</c>), and every other one is percent-encoded as UTF-8.
    /// </summary>
    public static string PathSegment(string location)
    {
        ArgumentNullException.ThrowIfNull(location);

        var segment = new StringBuilder(location.Length);

        foreach (var b in Encoding.UTF8.GetBytes(location))
        {
            var c = (char)b;

            if (b < 0x80 && IsPathCharacter(c))
                segment.Append(c);
            else
                segment.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return segment.ToString();
    }

    /// <summary>
    /// Whether a location can be a path segment at all: not empty, and not <c>.</c> or <c>..</c>,
    /// which an address would read as "here" or "up one" rather than as an instance.
    /// </summary>
    public static bool CanBePath(string? location) =>
        !string.IsNullOrWhiteSpace(location) && location is not "." and not "..";

    private static bool IsPathCharacter(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '-' or '.' or '_' or '~'
            or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '='
            or ':' or '@';

    private static async Task<string> ReadAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[8 * 1024];
        int read;

        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            var room = MaxAnswerBytes - (int)buffer.Length;
            if (room <= 0)
                break;

            buffer.Write(chunk, 0, Math.Min(read, room));
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
