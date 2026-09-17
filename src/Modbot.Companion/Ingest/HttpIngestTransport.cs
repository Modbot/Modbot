using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Modbot.Companion.Ingest;

/// <summary>
/// The real network call: one HTTPS POST carrying one batch to one Modbot server.
/// </summary>
/// <remarks>
/// <para><strong>This is the only place in the client that transmits observations to a Modbot
/// server.</strong> It sends the JSON of an <see cref="EventBatch"/> — whose fields are listed on that
/// type and on <see cref="ClientEvent"/> — to the address the moderator paired with, and nothing
/// else. The one other destination is the event backup to Modbot Cloud, in <c>HttpCloudLogClient</c>,
/// which can be turned off on this PC. No telemetry and no crash reports carrying event data.</para>
/// <para>Batched HTTP rather than a websocket, deliberately: with batching, "buffer and send later"
/// is the same code path as "send now" with a different timer, so replay after an outage is not a
/// separate, rarely exercised implementation that only runs when things are already going wrong.
/// Plain HTTP also crosses the corporate proxies, captive portals and VPNs that break websockets,
/// which are real conditions on a moderator's laptop.</para>
/// </remarks>
public sealed class HttpIngestTransport : IIngestTransport
{
    /// <summary>Above this, the body is gzipped. Below it, compression costs more than it saves.</summary>
    private const int CompressAbove = 4096;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public HttpIngestTransport(HttpClient http) => _http = http;

    public async Task<IngestResult> SendAsync(
        ServerPairing pairing,
        EventBatch batch,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, pairing.EventsEndpoint);

        // The device token. Ingest-scoped: it can submit presence facts and nothing else -- it
        // cannot read the member list, read a profile, or ban anybody.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.DeviceToken);
        request.Content = BuildContent(batch);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // No network, DNS gone, connection refused. Nothing is dropped by the caller.
            return new IngestResult(IngestOutcome.NetworkFailure);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new IngestResult(IngestOutcome.NetworkFailure);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Interpret(response, body);
        }
    }

    private static HttpContent BuildContent(EventBatch batch)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(batch, Json));

        if (payload.Length <= CompressAbove)
        {
            var plain = new ByteArrayContent(payload);
            plain.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            return plain;
        }

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(payload);

        var compressed = new ByteArrayContent(buffer.ToArray());
        compressed.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        compressed.Headers.ContentEncoding.Add("gzip");
        return compressed;
    }

    /// <summary>
    /// Turns a response into one of the handful of cases the client behaves differently about.
    /// </summary>
    /// <remarks>
    /// Branching is on the status and on the stable machine-readable <c>code</c> in the body, never
    /// on the human-readable message — that text is for people and will be reworded.
    /// </remarks>
    private static IngestResult Interpret(HttpResponseMessage response, string body)
    {
        var code = ReadCode(body);
        var retryAfter = response.Headers.RetryAfter?.Delta;

        if (response.IsSuccessStatusCode)
        {
            var (accepted, deduplicated, rejected) = ReadCounts(body);
            return new IngestResult(IngestOutcome.Accepted, accepted, deduplicated, rejected, Code: code);
        }

        var outcome = response.StatusCode switch
        {
            HttpStatusCode.BadRequest => IngestOutcome.Malformed,
            HttpStatusCode.Unauthorized => IngestOutcome.Unauthorised,
            HttpStatusCode.Forbidden => IngestOutcome.Unauthorised,
            HttpStatusCode.Conflict => IngestOutcome.VersionUnsupported,
            HttpStatusCode.RequestEntityTooLarge => IngestOutcome.TooLarge,
            HttpStatusCode.TooManyRequests => IngestOutcome.RateLimited,
            _ => IngestOutcome.ServerTrouble,
        };

        return new IngestResult(outcome, RetryAfter: retryAfter, Code: code);
    }

    private static string? ReadCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            // A proxy's HTML error page, or an empty body. The status line is enough to act on.
            return null;
        }
    }

    private static (int Accepted, int Deduplicated, int Rejected) ReadCounts(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            var accepted = root.TryGetProperty("accepted", out var a) ? a.GetInt32() : 0;
            var deduplicated = root.TryGetProperty("deduplicated", out var d) ? d.GetInt32() : 0;
            var rejected = root.TryGetProperty("rejected", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.GetArrayLength()
                : 0;

            return (accepted, deduplicated, rejected);
        }
        catch (JsonException)
        {
            return (0, 0, 0);
        }
    }
}
