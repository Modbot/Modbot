using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Modbot.Cloud.Features.Mail;

using Modbot.Cloud.Features.Showcase;
using Modbot.Cloud.Features.Updates;

namespace Modbot.Cloud.Tests;

/// <summary>
/// The app that ships, built through <see cref="CloudApp"/> over a test server and the real databases,
/// with a clock the test moves.
/// </summary>
/// <remarks>
/// The test server has no connection address, so a test gives a request its client address as an
/// <c>X-Forwarded-For</c> header, the way Railway's edge does.
/// </remarks>
public sealed class CloudTestHost : IAsyncDisposable
{
    public const string RootKey = "a-root-key-for-tests-only-0123456789";

    /// <summary>The read-only key for the public instances feed, which is all the landing page holds.</summary>
    public const string InstancesKey = "a-instances-key-for-tests-only-0123456789";

    public const string AppHtml = "<!doctype html><html><head><title>Modbot Cloud</title></head><body><div id=\"root\"></div></body></html>";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>What my.modbot.co and the landing page send.</summary>
    public const string ProxyKey = "a-proxy-key-for-tests-only-0123456789";

    public static readonly Uri PublicAddress = new("https://cloud.modbot.test");

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly DirectoryInfo _webRoot;

    private CloudTestHost(WebApplication app, HttpClient client, ManualTime time, DirectoryInfo webRoot, TestMailer mail)
    {
        _app = app;
        _client = client;
        _webRoot = webRoot;
        Time = time;
        Mail = mail;
    }

    public ManualTime Time { get; }

    /// <summary>Every message Cloud sent, in order, instead of a network call to Resend.</summary>
    public TestMailer Mail { get; }

    public IServiceProvider Services => _app.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static readonly DateTimeOffset Start = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    /// <param name="canSendMail">False builds a Cloud with no Resend key, which sends nothing.</param>
    /// <param name="updates">
    /// The release news, already built over whatever GitHub and Docker Hub the test wants. Left out,
    /// the suite gets one whose fetches are refused, which is a cold cache that cannot be filled.
    /// </param>
    public static async Task<CloudTestHost> StartAsync(
        PostgresFixture db,
        string? rootApiKey = RootKey,
        DateTimeOffset? now = null,
        string? instancesApiKey = InstancesKey,
        string? proxyApiKey = ProxyKey,
        bool canSendMail = true,
        LatestReleases? updates = null)
    {
        await db.ResetAsync();

        var time = new ManualTime(now ?? Start);

        var webRoot = Directory.CreateTempSubdirectory("modbot-cloud-tests-");
        await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "index.html"), AppHtml, Ct);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            // The project folder, so the test server serves the real term lists from src/.
            ContentRootPath = SourceDirectory(),
            WebRootPath = webRoot.FullName,
            EnvironmentName = "Testing",
        });

        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(time);

        // Registered first, so CloudApp's TryAdd leaves it alone and no test ever reaches Resend.
        var mailer = new TestMailer(canSendMail);
        builder.Services.AddSingleton<ICloudMailer>(mailer);

        // Same reason, for GitHub: the suite must never reach it, and "GitHub said no" is the
        // answer a private repository gives anyway.
        builder.Services.AddSingleton(_ => new GitHubContributors(
            new HttpClient(new RefusingHandler()),
            token: null,
            repository: "example/none",
            time));

        // And for the showcase pictures Cloud fetches when an administrator saves a row: a handful
        // of known addresses, and 404 for everything else, so no test reaches a picture host.
        builder.Services.AddSingleton(new ShowcasePictures(new HttpClient(new PictureHost())));
        // The release news, for the same reason. A test that says nothing gets a Cloud that could
        // not read GitHub at all -- which is the cold-cache case worth having as the default.
        builder.Services.AddSingleton(updates ?? new LatestReleases(
            new GitHubReleases(new HttpClient(new RefusingHandler()), token: null, repository: "example/none"),
            new DockerHubTags(new HttpClient(new RefusingHandler()), image: "example/none"),
            time));

        CloudApp.AddServices(
            builder.Services,
            db.ConnectionString,
            db.EngineConnectionString,
            rootApiKey,
            instancesApiKey,
            proxyApiKey,
            new MailSettings(canSendMail ? "test-key" : null, "Modbot <noreply@modbot.test>", PublicAddress),
            runDailyUpkeep: false,
            // The tests run the instance checks themselves, against the fake clock.
            watchInstances: false,
            // Same: a test refreshes the release news itself, and no test reaches GitHub.
            refreshUpdates: false);

        var app = builder.Build();

        var problem = await CloudApp.PrepareAsync(app.Services, NullLogger.Instance, Ct);
        Assert.Null(problem);

        CloudApp.MapEndpoints(app);
        await app.StartAsync(Ct);

        return new CloudTestHost(app, app.GetTestClient(), time, webRoot, mailer);
    }

    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string? ip = null,
        string? cookie = null,
        string? bearer = null)
    {
        var request = new HttpRequestMessage(method, path);

        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        if (ip is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return _client.SendAsync(request, Ct);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? bearer = null) => SendAsync(HttpMethod.Get, path, bearer: bearer);

    /// <summary>Registers an install and returns its bearer value, <c>id.secret</c>.</summary>
    public async Task<(Guid Id, string Bearer)> RegisterAsync(string ip = "203.0.113.10")
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/installs", new { companionVersion = "2026.9.0", platform = "windows" }, ip);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = body.GetProperty("installId").GetGuid();
        return (id, $"{id}.{body.GetProperty("secret").GetString()}");
    }

    /// <summary>Posts a batch, gzipped the way the client sends it.</summary>
    public Task<HttpResponseMessage> PostBatchAsync(string? bearer, object batch)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(batch, Json);

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(json);

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/events") { Content = content };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return _client.SendAsync(request, Ct);
    }

    /// <summary>
    /// Registers a Modbot deployment in the server registry and returns its bearer. The same
    /// credential it reports with, and the one its log batches carry.
    /// </summary>
    public async Task<(Guid Id, string Bearer)> RegisterServerAsync(string ip = "203.0.113.20")
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/v1/servers",
            new { version = "2026.9.0", hostPlatform = "linux-x64" },
            ip);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = body.GetProperty("serverId").GetGuid();
        return (id, $"{id}.{body.GetProperty("secret").GetString()}");
    }

    /// <summary>Posts a log batch, gzipped the way a deployment sends it.</summary>
    public Task<HttpResponseMessage> PostLogsAsync(string? bearer, object batch)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(batch, Json);

        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(json);

        var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        content.Headers.ContentEncoding.Add("gzip");

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/logs") { Content = content };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return _client.SendAsync(request, Ct);
    }

    /// <summary>One log line in the shape a Modbot deployment sends.</summary>
    public static object LogLine(
        DateTimeOffset at,
        string message = "Stored 42 facts",
        string level = "Information",
        string? source = "Modbot.VRChat.Sync.AuditLogProducer",
        string? area = "Sync",
        string? exception = null,
        object? properties = null) =>
        new
        {
            at,
            level,
            message,
            template = message,
            source,
            area,
            service = "Modbot",
            exception,
            // A JsonElement, not an anonymous object: Serilog's own property names are whatever the
            // code that logged them wrote (foundation, structured logging carries no naming policy),
            // and an anonymous object here would go through the Web camelCase policy `Json` uses for
            // everything else, sending "count" for a real deployment's "Count".
            properties = properties ?? DefaultProperties,
        };

    private static readonly JsonElement DefaultProperties = JsonDocument.Parse("""{"Count":42}""").RootElement;

    public object LogBatch(params object[] lines) =>
        new { sentAt = Time.GetUtcNow(), serverVersion = "2026.9.0", lines };

    /// <summary>One event in the client protocol's shape, for any instance.</summary>
    public static object Event(
        string id,
        DateTimeOffset occurredAt,
        string type = "InstanceJoined",
        string subjectId = "usr_1",
        string worldId = "wrld_1",
        string instanceId = "12345",
        string? groupId = null,
        object? data = null) =>
        new { companionEventId = id, type, occurredAt, occurredBefore = (DateTimeOffset?)null, subjectId, worldId, instanceId, groupId, data = data ?? new { displayName = "Rin" } };

    public object Batch(params object[] events) => Batch(Time.GetUtcNow(), null, "unknown", events);

    public static object Batch(DateTimeOffset sentAt, long? clockOffsetMs, string clockConfidence, params object[] events) => new
    {
        batchId = Guid.NewGuid().ToString("n"),
        companionVersion = "2026.9.0",
        sentAt,
        clockOffsetMs,
        clockConfidence,
        modbotServerId = (string?)null,
        events,
    };

    /// <summary>The Modbot.Cloud source folder, used as the content root.</summary>
    private static string SourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Modbot.slnx")))
                return Path.Combine(dir.FullName, "src", "Modbot.Cloud");
        }

        throw new InvalidOperationException("Could not find the repository root above the test output.");
    }

    /// <summary>The address of a picture Cloud will accept and keep.</summary>
    public const string PictureAddress = "https://pictures.test/icon.png";

    /// <summary>A second one, so a row can carry a different picture per field.</summary>
    public const string BannerAddress = "https://pictures.test/banner.png";

    /// <summary>An address that answers with a web page while calling itself a picture.</summary>
    public const string NotAPictureAddress = "https://pictures.test/page.png";

    /// <summary>An address that answers with more bytes than Cloud will keep.</summary>
    public const string TooBigAddress = "https://pictures.test/huge.png";

    /// <summary>A PNG: the eight-byte signature and enough after it to be worth serving.</summary>
    public static byte[] PictureBytes { get; } =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Repeat((byte)0x2A, 24)];

    /// <summary>A second PNG, told apart from the first by its tail.</summary>
    public static byte[] BannerBytes { get; } =
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Enumerable.Repeat((byte)0x7B, 32)];

    /// <summary>
    /// The picture hosts the suite is allowed to reach: two pictures, a web page wearing a
    /// picture's name, and something far too big.
    /// </summary>
    private sealed class PictureHost : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (bytes, type) = request.RequestUri?.ToString() switch
            {
                PictureAddress => (PictureBytes, "image/png"),
                BannerAddress => (BannerBytes, "image/png"),
                NotAPictureAddress => (Encoding.UTF8.GetBytes("<!doctype html><html>Not here</html>"), "image/png"),
                TooBigAddress => (TooBig(), "image/png"),
                _ => (null, null),
            };

            if (bytes is null)
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(type!);

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }

        private static byte[] TooBig()
        {
            var bytes = new byte[ShowcasePicture.MaxBytes + 1024];
            ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

            png.CopyTo(bytes);
            return bytes;
        }
    }

    /// <summary>Answers every request with 404, so no test reaches the network.</summary>
    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();

        try
        {
            _webRoot.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is not a test failure.
        }
    }
}

/// <summary>Keeps every message instead of sending it, so a test can read the link out of one.</summary>
public sealed class TestMailer(bool canSend) : ICloudMailer
{
    private readonly List<(string To, string Subject, string Body)> _sent = [];

    public bool CanSend { get; } = canSend;

    public IReadOnlyList<(string To, string Subject, string Body)> Sent => _sent;

    public (string To, string Subject, string Body) Last => _sent[^1];

    public Task<bool> SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        lock (_sent)
            _sent.Add((to, subject, body));

        return Task.FromResult(true);
    }

    /// <summary>The <c>token=</c> value in the newest message's link.</summary>
    public string LastToken()
    {
        var body = Last.Body;
        var at = body.IndexOf("token=", StringComparison.Ordinal);
        Assert.True(at >= 0, "The message carried no token.");

        var value = body[(at + "token=".Length)..];
        var end = value.IndexOfAny([' ', '\r', '\n']);
        return Uri.UnescapeDataString(end < 0 ? value : value[..end]);
    }
}

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset to) => _now = to;
}
