using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Modbot.Client.CloudBackup;
using Modbot.Client.Ingest;
using Modbot.Client.Pairing;
using Modbot.Client.Time;
using Modbot.TestSupport;

namespace Modbot.Client.Tests.CloudBackup;

public sealed class CloudAddressTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-cloud-address-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ServerConnection Server(string id, ServerCloudAnswer? answer)
    {
        var connection = ServerConnections.Make(_clock, _directory, id);
        connection.Cloud = answer;
        return connection;
    }

    [Fact]
    public void TheRuleForWhichCloud()
    {
        var named = new ServerCloudAnswer(new Uri("https://cloud.group.example"), false, null);
        var other = new ServerCloudAnswer(new Uri("https://cloud.other.example"), false, "other-id");
        var off = new ServerCloudAnswer(null, true, null);

        Assert.Equal(CloudDestination.Default, CloudDestination.Resolve([]));

        Assert.Equal(CloudDestinationKind.Wait, CloudDestination.Resolve([Server("a", null)]).Kind);

        var noPreference = CloudDestination.Resolve([Server("a", ServerCloudAnswer.NoPreference)]);
        Assert.Equal((CloudDestinationKind.Send, CloudDestination.DefaultEndpoint), (noPreference.Kind, noPreference.Endpoint));

        var first = CloudDestination.Resolve([Server("a", named), Server("b", other)]);
        Assert.Equal(new Uri("https://cloud.group.example"), first.Endpoint);
        Assert.Equal("other-id", first.ModbotServerId);

        // Any one server turning it off wins, even over a server that named an address.
        Assert.Equal(CloudDestinationKind.TurnedOffByServer, CloudDestination.Resolve([Server("a", named), Server("b", off)]).Kind);

        // Plain HTTP to somewhere else counts as off.
        var insecure = new ServerCloudAnswer(new Uri("http://cloud.group.example"), false, null);
        Assert.Equal(CloudDestinationKind.TurnedOffByServer, CloudDestination.Resolve([Server("a", insecure)]).Kind);

        // A paused server is not asked, so it does not hold sending.
        var paused = Server("a", null);
        paused.IsPaused = true;
        Assert.Equal(CloudDestination.Default, CloudDestination.Resolve([paused]));
    }

    [Theory]
    [InlineData("""{ "serverTime": "2026-09-15T08:00:00Z" }""", null, false)]
    [InlineData("""{ "serverTime": "2026-09-15T08:00:00Z", "cloud": { "endpoint": "https://cloud.modbot.co/", "disabled": false } }""", "https://cloud.modbot.co/", false)]
    [InlineData("""{ "serverTime": "2026-09-15T08:00:00Z", "cloud": { "endpoint": null, "disabled": true } }""", null, true)]
    [InlineData("""{ "serverTime": "2026-09-15T08:00:00Z", "cloud": { "endpoint": "not a url", "disabled": false } }""", null, true)]
    public void TheServersAnswerIsRead(string json, string? endpoint, bool disabled)
    {
        using var document = JsonDocument.Parse(json);
        var answer = HttpServerTimeProbe.ReadCloud(document.RootElement);

        Assert.Equal(endpoint, answer.Endpoint?.ToString());
        Assert.Equal(disabled, answer.Disabled);
    }

    [Fact]
    public async Task TheHttpClientAuthenticatesAndGzipsAndReadsRetryAfter()
    {
        var handler = new RecordingHandler();
        var client = new HttpCloudLogClient(new HttpClient(handler), _clock);
        var endpoint = new Uri("https://cloud.modbot.co");

        handler.Next = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{ "installId": "11111111-2222-3333-4444-555555555555", "secret": "s3cret" }""", Encoding.UTF8, "application/json"),
        };
        var registration = await client.RegisterAsync(endpoint, "2026.9.0", Ct);
        Assert.Equal(IngestOutcome.Accepted, registration.Outcome);
        Assert.Equal("s3cret", registration.Secret);
        Assert.Equal("/api/v1/installs", handler.Requests[0].RequestUri!.AbsolutePath);

        handler.Next = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        handler.Next.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));

        using var body = new MemoryStream();
        using (var gzip = new GZipStream(body, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write("{}"u8);

        var result = await client.SendAsync(new CloudInstall(endpoint, registration.InstallId, "s3cret"), body.ToArray(), Ct);

        Assert.Equal(IngestOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(42), result.RetryAfter);

        var sent = handler.Requests[1];
        Assert.Equal("/api/v1/events", sent.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
        Assert.Equal("11111111-2222-3333-4444-555555555555.s3cret", sent.Headers.Authorization.Parameter);
        Assert.Contains("gzip", handler.ContentEncodings[1]);

        // Plain HTTP to anywhere but this PC is refused without a request.
        var refused = await client.SendAsync(new CloudInstall(new Uri("http://cloud.example"), Guid.NewGuid(), "x"), [], Ct);
        Assert.Equal(IngestOutcome.Malformed, refused.Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void TheSecretIsStoredEncrypted()
    {
        var path = Path.Combine(_directory, "cloud-installs.json");
        var store = new DpapiCloudInstallStore(path, new ReversingProtector());
        var endpoint = new Uri("https://cloud.modbot.co");

        store.Save(new CloudInstall(endpoint, Guid.Parse("11111111-2222-3333-4444-555555555555"), "plain-secret"));

        var file = File.ReadAllText(path);
        Assert.DoesNotContain("plain-secret", file, StringComparison.Ordinal);
        Assert.Contains("11111111-2222-3333-4444-555555555555", file, StringComparison.Ordinal);

        Assert.Equal("plain-secret", new DpapiCloudInstallStore(path, new ReversingProtector()).Find(endpoint)!.Secret);
        Assert.Null(store.Find(new Uri("https://cloud.other.example")));

        store.Forget(endpoint);
        Assert.Null(store.Find(endpoint));
    }

    private sealed class ReversingProtector : IPairingSecretProtector
    {
        public byte[] Protect(byte[] plaintext) => [.. plaintext.Reverse()];

        public byte[] Unprotect(byte[] ciphertext) => [.. ciphertext.Reverse()];
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string> ContentEncodings { get; } = [];

        public HttpResponseMessage Next { get; set; } = new(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            ContentEncodings.Add(string.Join(",", request.Content?.Headers.ContentEncoding ?? []));
            return Task.FromResult(Next);
        }
    }
}
