using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Core.Configuration;

namespace Modbot.Core.Cloud;

/// <param name="ServerId">The id Cloud assigned. Kept in settings.</param>
/// <param name="Secret">Handed over once. Kept in settings, encrypted.</param>
public sealed record CloudRegistration(
    [property: JsonPropertyName("serverId")] Guid ServerId,
    [property: JsonPropertyName("secret")] string Secret);

/// <summary>What happened when this server called Modbot Cloud.</summary>
/// <param name="Ok">Cloud took it.</param>
/// <param name="Forgotten">Cloud does not know this server any more, so it must register again.</param>
/// <param name="Problem">One short sentence for the Health page, or null when it worked.</param>
public sealed record CloudCallResult(bool Ok, bool Forgotten, string? Problem)
{
    public static readonly CloudCallResult Success = new(true, false, null);
}

/// <summary>
/// Every call this Modbot server makes to Modbot Cloud.
/// </summary>
/// <remarks>
/// <para>
/// Through the ordinary <see cref="IHttpClientFactory"/> client. <strong>Never the VRChat gate and
/// never the egress proxy</strong>: Cloud is not VRChat, so a call to it must not spend a VRChat
/// rate-limit budget or leave by an address the operator set aside for VRChat.
/// </para>
/// <para>
/// Nothing in Modbot waits on any of this, and a Cloud that is slow, unreachable or permanently gone
/// changes nothing about how the deployment behaves.
/// </para>
/// </remarks>
public sealed class CloudServerClient(ModbotCloudAddress cloud, IHttpClientFactory factory)
{
    public const string HttpClientName = "Modbot.Cloud";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>For the one call a person is waiting on. See <see cref="SubscribeAsync"/>.</summary>
    private static readonly TimeSpan SubscribeTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Asks Cloud for an id and a secret. Null when it could not be done.</summary>
    public async Task<CloudRegistration?> RegisterAsync(ServerReport report, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            using var client = Client();
            using var response = await client.PostAsJsonAsync(
                "api/v1/servers",
                new { report.PublicAddress, report.Version, report.HostPlatform },
                Json,
                ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync<CloudRegistration>(Json, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (Transient(e, ct))
        {
            return null;
        }
    }

    public async Task<CloudCallResult> ReportAsync(string bearer, ServerReport report, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/servers/report")
        {
            Content = JsonContent.Create(report, options: Json),
        };

        return await SendAsync(bearer, request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks Cloud to send this address the project's news about new features and updates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever called for somebody who ticked the box while making their account (server info and
    /// account email design §5). Nothing waits on it: the answer is the status code and nothing
    /// else, and the caller logs a refusal rather than failing anything.
    /// </para>
    /// <para>
    /// <paramref name="bearer"/> is null on a deployment that has not registered with Cloud yet,
    /// which is every deployment during its own setup wizard -- the first administrator's account
    /// is made minutes before the first report goes out. Cloud takes the address either way; the
    /// bearer, when there is one, is what tells it which server the person came from.
    /// </para>
    /// <para>
    /// Its own short timeout rather than the twenty seconds the report gets. A person is waiting
    /// on the other side of this call, even though the account they asked for is already made.
    /// </para>
    /// </remarks>
    /// <returns>The HTTP status Cloud answered, or null when it could not be reached.</returns>
    public async Task<int?> SubscribeAsync(string? bearer, string email, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        try
        {
            using var client = Client(SubscribeTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/subscribers")
            {
                Content = JsonContent.Create(new { email, source = "server" }, options: Json),
            };

            if (!string.IsNullOrWhiteSpace(bearer))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            return (int)response.StatusCode;
        }
        catch (Exception e) when (Transient(e, ct))
        {
            return null;
        }
    }

    /// <summary>Tells Cloud the hash of the link code this server is showing its owner.</summary>
    public async Task<CloudCallResult> SendLinkCodeAsync(string bearer, string codeHash, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/servers/link-code")
        {
            Content = JsonContent.Create(new { codeHash }, options: Json),
        };

        return await SendAsync(bearer, request, ct).ConfigureAwait(false);
    }

    private async Task<CloudCallResult> SendAsync(string bearer, HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bearer);
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            using var client = Client();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return CloudCallResult.Success;

            // Cloud has forgotten this server, or never knew it: register again rather than
            // reporting into a void forever.
            var forgotten = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound;

            return new CloudCallResult(
                false, forgotten, $"Modbot Cloud answered {(int)response.StatusCode}.");
        }
        catch (Exception e) when (Transient(e, ct))
        {
            return new CloudCallResult(false, false, $"Could not reach Modbot Cloud at {cloud.Endpoint.Host}.");
        }
    }

    private HttpClient Client(TimeSpan? timeout = null)
    {
        var client = factory.CreateClient(HttpClientName);

        // A base address without a trailing slash drops its last segment when a relative path is
        // appended, which would quietly send every call to the wrong place.
        client.BaseAddress = cloud.Endpoint.AbsoluteUri.EndsWith('/')
            ? cloud.Endpoint
            : new Uri(cloud.Endpoint.AbsoluteUri + "/");

        client.Timeout = timeout ?? Timeout;
        return client;
    }

    private static bool Transient(Exception e, CancellationToken ct) =>
        e is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException
        && !ct.IsCancellationRequested;
}
