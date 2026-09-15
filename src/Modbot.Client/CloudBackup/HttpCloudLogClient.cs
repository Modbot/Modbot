using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Modbot.Client.Ingest;
using Modbot.Client.Pairing;
using Modbot.Client.Time;
using Modbot.Core.Time;

namespace Modbot.Client.CloudBackup;

/// <summary>How registering with a Cloud went.</summary>
public sealed record CloudRegistration(IngestOutcome Outcome, Guid InstallId = default, string? Secret = null, TimeSpan? RetryAfter = null);

/// <summary>The three requests the event backup makes to Modbot Cloud.</summary>
public interface ICloudLogClient
{
    Task<CloudRegistration> RegisterAsync(Uri endpoint, string clientVersion, CancellationToken cancellationToken);

    /// <param name="body">A whole batch, gzipped JSON.</param>
    Task<IngestResult> SendAsync(CloudInstall install, byte[] body, CancellationToken cancellationToken);

    Task<ClockSample?> MeasureAsync(Uri endpoint, CancellationToken cancellationToken);
}

/// <summary>
/// The network calls to Modbot Cloud: register, send a batch of presence events, ask the time.
/// </summary>
/// <remarks>
/// <para><strong>What this sends, and where.</strong> Only to the one Cloud address the backup was
/// started with — <c>https://cloud.modbot.co</c> unless <c>settings.json</c> or
/// <c>MODBOT_CLOUD_ENDPOINT</c> on this PC names another — and only over
/// HTTPS (plain HTTP to this PC is allowed for testing):</para>
/// <list type="bullet">
/// <item><c>POST /api/v1/installs</c> with the client's version and the word <c>windows</c>. Nothing
/// else: no machine name, no account, no VRChat id.</item>
/// <item><c>POST /api/v1/events</c> with a gzipped batch of <c>ClientEvent</c> rows — the same presence
/// events a Modbot server gets, but for every instance — and the install id and secret as a bearer
/// header. These name other players and the instances you are in; never a raw log line.</item>
/// <item><c>GET /api/v1/time</c> with no body and no credential.</item>
/// </list>
/// <para>No pairing token or device token is ever sent to Cloud, and nothing from Modbot's own logs.</para>
/// </remarks>
public sealed class HttpCloudLogClient : ICloudLogClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly IModbotClock _clock;

    public HttpCloudLogClient(HttpClient http, IModbotClock clock)
    {
        _http = http;
        _clock = clock;
    }

    public async Task<CloudRegistration> RegisterAsync(Uri endpoint, string clientVersion, CancellationToken cancellationToken)
    {
        if (!ServerAddresses.IsAllowed(endpoint))
            return new CloudRegistration(IngestOutcome.Malformed);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/api/v1/installs"))
        {
            Content = JsonContent.Create(new RegisterBody(clientVersion, "windows"), options: Json),
        };

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new CloudRegistration(Outcome(response.StatusCode), RetryAfter: response.Headers.RetryAfter?.Delta);

            var body = await response.Content.ReadFromJsonAsync<RegisteredBody>(Json, cancellationToken).ConfigureAwait(false);
            return body is { InstallId: var id, Secret.Length: > 0 } && id != Guid.Empty
                ? new CloudRegistration(IngestOutcome.Accepted, id, body.Secret)
                : new CloudRegistration(IngestOutcome.ServerTrouble);
        }
        catch (HttpRequestException)
        {
            return new CloudRegistration(IngestOutcome.NetworkFailure);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CloudRegistration(IngestOutcome.NetworkFailure);
        }
        catch (JsonException)
        {
            return new CloudRegistration(IngestOutcome.ServerTrouble);
        }
    }

    public async Task<IngestResult> SendAsync(CloudInstall install, byte[] body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(install);

        if (!ServerAddresses.IsAllowed(install.Endpoint))
            return new IngestResult(IngestOutcome.Malformed);

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(install.Endpoint, "/api/v1/events")) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", $"{install.InstallId:D}.{install.Secret}");

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? new IngestResult(IngestOutcome.Accepted)
                : new IngestResult(Outcome(response.StatusCode), RetryAfter: response.Headers.RetryAfter?.Delta);
        }
        catch (HttpRequestException)
        {
            return new IngestResult(IngestOutcome.NetworkFailure);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new IngestResult(IngestOutcome.NetworkFailure);
        }
    }

    public async Task<ClockSample?> MeasureAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        if (!ServerAddresses.IsAllowed(endpoint))
            return null;

        var sentAt = _clock.UtcNow;

        try
        {
            using var response = await _http.GetAsync(new Uri(endpoint, "/api/v1/time"), cancellationToken).ConfigureAwait(false);
            var receivedAt = _clock.UtcNow;

            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadFromJsonAsync<TimeBody>(Json, cancellationToken).ConfigureAwait(false);
            return body?.ServerTime is { } serverTime ? new ClockSample(sentAt, serverTime, receivedAt) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static IngestOutcome Outcome(HttpStatusCode status) => status switch
    {
        HttpStatusCode.BadRequest => IngestOutcome.Malformed,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => IngestOutcome.Unauthorised,
        HttpStatusCode.RequestEntityTooLarge => IngestOutcome.TooLarge,
        HttpStatusCode.TooManyRequests => IngestOutcome.RateLimited,
        _ => IngestOutcome.ServerTrouble,
    };

    private sealed record RegisterBody(
        [property: JsonPropertyName("clientVersion")] string ClientVersion,
        [property: JsonPropertyName("platform")] string Platform);

    private sealed record RegisteredBody(
        [property: JsonPropertyName("installId")] Guid InstallId,
        [property: JsonPropertyName("secret")] string? Secret);

    private sealed record TimeBody([property: JsonPropertyName("serverTime")] DateTimeOffset? ServerTime);
}
