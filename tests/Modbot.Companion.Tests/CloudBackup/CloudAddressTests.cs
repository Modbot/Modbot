using System.IO.Compression;
using System.Net;
using System.Text;
using Modbot.Companion.CloudBackup;
using Modbot.Companion.Ingest;
using Modbot.Companion.Pairing;
using Modbot.Companion.Presentation;
using Modbot.TestSupport;

namespace Modbot.Companion.Tests.CloudBackup;

public sealed class CloudAddressTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("modbot-cloud-address-").FullName;
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 15, 8, 0, 0, TimeSpan.Zero));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string SettingsFile(string json)
    {
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static Func<string, string?> Environment(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void TheDefaultIsOnToModbotCloud()
    {
        var missing = ClientSettings.Load(Path.Combine(_directory, "missing.json"), Environment());

        Assert.Equal(CloudSettings.Default, missing.Cloud);
        Assert.Equal(new Uri("https://cloud.modbot.co"), missing.Cloud.Endpoint);
        Assert.False(missing.Cloud.Disabled);

        var other = ClientSettings.Load(SettingsFile("""{ "pairingPage": "https://modbot.example/pair" }"""), Environment());
        Assert.Equal(CloudSettings.Default, other.Cloud);
    }

    [Fact]
    public void SettingsJsonCanNameAnotherCloudOrTurnItOff()
    {
        var path = SettingsFile("""{ "cloud": { "endpoint": "https://cloud.group.example", "disabled": true } }""");

        var cloud = ClientSettings.Load(path, Environment()).Cloud;

        Assert.Equal(new Uri("https://cloud.group.example"), cloud.Endpoint);
        Assert.True(cloud.Disabled);
    }

    [Fact]
    public void TheEnvironmentWinsOverSettingsJson()
    {
        var path = SettingsFile("""{ "cloud": { "endpoint": "https://cloud.file.example", "disabled": false } }""");

        var cloud = ClientSettings.Load(path, Environment(
            (CloudSettings.EndpointVariable, "https://cloud.env.example"),
            (CloudSettings.DisabledVariable, "yes"))).Cloud;

        Assert.Equal(new Uri("https://cloud.env.example"), cloud.Endpoint);
        Assert.True(cloud.Disabled);

        // Turned off in the file, and back on in the environment.
        var offInFile = SettingsFile("""{ "cloud": { "disabled": true } }""");
        Assert.False(ClientSettings.Load(offInFile, Environment((CloudSettings.DisabledVariable, "0"))).Cloud.Disabled);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("YES")]
    [InlineData(" on ")]
    public void TheEnvironmentAloneCanTurnItOff(string value)
    {
        var cloud = ClientSettings.Load(Path.Combine(_directory, "missing.json"), Environment((CloudSettings.DisabledVariable, value))).Cloud;

        Assert.True(cloud.Disabled);
        Assert.Equal(CloudSettings.DefaultEndpoint, cloud.Endpoint);
    }

    [Fact]
    public void AnEnvironmentWordThatIsNotOnOrOffLeavesSettingsJsonToDecide()
    {
        var offInFile = SettingsFile("""{ "cloud": { "disabled": true } }""");

        Assert.True(ClientSettings.Load(offInFile, Environment((CloudSettings.DisabledVariable, "ture"))).Cloud.Disabled);
        Assert.True(ClientSettings.Load(offInFile, Environment((CloudSettings.DisabledVariable, ""))).Cloud.Disabled);
    }

    [Theory]
    [InlineData("cloud.example.org")]
    [InlineData("not an address")]
    [InlineData("ftp://cloud.example.org")]
    [InlineData("http://cloud.example.org")]
    public void AnInvalidEndpointFallsBackToTheDefault(string endpoint)
    {
        var fromEnvironment = CloudSettings.Resolve(null, null, Environment((CloudSettings.EndpointVariable, endpoint)));
        Assert.Equal(CloudSettings.DefaultEndpoint, fromEnvironment.Endpoint);
        Assert.Equal(endpoint, fromEnvironment.RejectedEndpoint);
        Assert.False(fromEnvironment.Disabled);

        var fromFile = CloudSettings.Resolve(endpoint, null, Environment());
        Assert.Equal(CloudSettings.DefaultEndpoint, fromFile.Endpoint);
    }

    [Fact]
    public void PlainHttpToThisPcIsAllowedForTesting()
    {
        var cloud = CloudSettings.Resolve("http://localhost:5080", null, Environment());

        Assert.Equal(new Uri("http://localhost:5080"), cloud.Endpoint);
        Assert.Null(cloud.RejectedEndpoint);
    }

    [Fact]
    public void AnUnreadableSettingsFileStillHonoursTheEnvironment()
    {
        var path = SettingsFile("{ not json");

        var cloud = ClientSettings.Load(path, Environment(
            (CloudSettings.EndpointVariable, "https://cloud.env.example"),
            (CloudSettings.DisabledVariable, "1"))).Cloud;

        Assert.Equal(new Uri("https://cloud.env.example"), cloud.Endpoint);
        Assert.True(cloud.Disabled);
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
