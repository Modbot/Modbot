using System.IO.Compression;
using System.Net;
using System.Text;
using Modbot.Client.Ingest;
using Modbot.Client.Time;
using Modbot.TestSupport;

namespace Modbot.Client.Tests.Ingest;

public class HttpIngestTransportTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public byte[]? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            return respond(request);
        }
    }

    private static readonly ServerPairing Pairing =
        new("cats", new Uri("https://modbot.example"), "SECRET-TOKEN", "grp_cats");

    private static HttpResponseMessage Respond(HttpStatusCode status, string body = "{}")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static EventBatch Batch(int events = 1)
    {
        var clock = new ServerClock(new FakeClock());

        return EventBatch.Create("batch-1", "2026.9.0", clock,
        [
            .. Enumerable.Range(0, events).Select(i => new ClientEvent
            {
                ClientEventId = $"event-{i}",
                Type = ClientEventType.InstanceJoined,
                OccurredAt = new DateTimeOffset(2026, 9, 12, 20, 14, 7, TimeSpan.Zero),
                SubjectId = $"usr_{i}",
                WorldId = "wrld_w",
                InstanceId = "85019",
                GroupId = "grp_cats",
            }),
        ]);
    }

    private static async Task<(IngestResult Result, StubHandler Handler)> SendAsync(
        Func<HttpRequestMessage, HttpResponseMessage> respond,
        EventBatch? batch = null)
    {
        var handler = new StubHandler(respond);
        using var http = new HttpClient(handler);
        var transport = new HttpIngestTransport(http);

        var result = await transport.SendAsync(
            Pairing, batch ?? Batch(), TestContext.Current.CancellationToken);

        return (result, handler);
    }

    [Fact]
    public async Task PostsToTheNegotiatedEndpointWithTheDeviceToken()
    {
        var (_, handler) = await SendAsync(_ => Respond(HttpStatusCode.OK));

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://modbot.example/api/v1/client/events", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal("SECRET-TOKEN", handler.LastRequest.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task SendsTheBatchAsJson()
    {
        var (_, handler) = await SendAsync(_ => Respond(HttpStatusCode.OK));

        var body = Encoding.UTF8.GetString(handler.LastBody!);
        Assert.Contains("\"batchId\":\"batch-1\"", body);
        Assert.Contains("\"clockConfidence\":\"unknown\"", body);
        Assert.Contains("\"subjectId\":\"usr_0\"", body);
    }

    [Fact]
    public async Task CompressesLargeBatches()
    {
        var (_, handler) = await SendAsync(_ => Respond(HttpStatusCode.OK), Batch(events: 200));

        Assert.Contains("gzip", handler.LastRequest!.Content!.Headers.ContentEncoding);

        using var compressed = new MemoryStream(handler.LastBody!);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);

        Assert.Contains("\"batchId\":\"batch-1\"", await reader.ReadToEndAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DoesNotCompressASmallBatch()
    {
        var (_, handler) = await SendAsync(_ => Respond(HttpStatusCode.OK));

        Assert.Empty(handler.LastRequest!.Content!.Headers.ContentEncoding);
    }

    [Fact]
    public async Task ReadsTheAcceptanceCounts()
    {
        var (result, _) = await SendAsync(_ => Respond(
            HttpStatusCode.OK,
            """{"accepted":37,"deduplicated":11,"rejected":[{"index":4,"reason":"unknown_group"}]}"""));

        Assert.Equal(IngestOutcome.Accepted, result.Outcome);
        Assert.Equal(37, result.Accepted);
        Assert.Equal(11, result.Deduplicated);
        Assert.Equal(1, result.Rejected);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, IngestOutcome.Malformed)]
    [InlineData(HttpStatusCode.Unauthorized, IngestOutcome.Unauthorised)]
    [InlineData(HttpStatusCode.Forbidden, IngestOutcome.Unauthorised)]
    [InlineData(HttpStatusCode.Conflict, IngestOutcome.VersionUnsupported)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, IngestOutcome.TooLarge)]
    [InlineData(HttpStatusCode.TooManyRequests, IngestOutcome.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, IngestOutcome.ServerTrouble)]
    [InlineData(HttpStatusCode.BadGateway, IngestOutcome.ServerTrouble)]
    [InlineData(HttpStatusCode.ServiceUnavailable, IngestOutcome.ServerTrouble)]
    public async Task MapsEachStatusOntoWhatTheClientShouldDo(HttpStatusCode status, IngestOutcome expected)
    {
        var (result, _) = await SendAsync(_ => Respond(status));

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task BranchesOnTheMachineReadableCodeAndNotTheMessage()
    {
        // Message text is for humans and will be reworded; code is the contract.
        var (result, _) = await SendAsync(_ => Respond(
            HttpStatusCode.Conflict,
            """{"code":"api_version_unsupported","message":"anything at all, reworded next week"}"""));

        Assert.Equal("api_version_unsupported", result.Code);
    }

    [Fact]
    public async Task HonoursRetryAfter()
    {
        var (result, _) = await SendAsync(_ =>
        {
            var response = Respond(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                TimeSpan.FromSeconds(90));
            return response;
        });

        Assert.Equal(TimeSpan.FromSeconds(90), result.RetryAfter);
    }

    [Fact]
    public async Task AProxysHtmlErrorPageIsNotAParseFailure()
    {
        // Captive portals and corporate proxies do this. The status line is enough to act on.
        var (result, _) = await SendAsync(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><body>502 Bad Gateway</body></html>", Encoding.UTF8, "text/html"),
        });

        Assert.Equal(IngestOutcome.ServerTrouble, result.Outcome);
        Assert.Null(result.Code);
    }

    [Fact]
    public async Task NoNetworkIsATransientFailureRatherThanAnException()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("no such host"));
        using var http = new HttpClient(handler);

        var result = await new HttpIngestTransport(http).SendAsync(
            Pairing, Batch(), TestContext.Current.CancellationToken);

        Assert.Equal(IngestOutcome.NetworkFailure, result.Outcome);
    }
}
