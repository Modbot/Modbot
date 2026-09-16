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

    /// <summary>The read-only key for the public rooms feed, which is all the landing page holds.</summary>
    public const string RoomsKey = "a-rooms-key-for-tests-only-0123456789";

    public const string AppHtml = "<!doctype html><html><head><title>Modbot Cloud</title></head><body><div id=\"root\"></div></body></html>";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly DirectoryInfo _webRoot;

    private CloudTestHost(WebApplication app, HttpClient client, ManualTime time, DirectoryInfo webRoot)
    {
        _app = app;
        _client = client;
        _webRoot = webRoot;
        Time = time;
    }

    public ManualTime Time { get; }

    public IServiceProvider Services => _app.Services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static readonly DateTimeOffset Start = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    public static async Task<CloudTestHost> StartAsync(
        PostgresFixture db,
        string? rootApiKey = RootKey,
        DateTimeOffset? now = null,
        string? roomsApiKey = RoomsKey)
    {
        await db.ResetAsync();

        var time = new ManualTime(now ?? Start);

        var webRoot = Directory.CreateTempSubdirectory("modbot-cloud-tests-");
        await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "index.html"), AppHtml, Ct);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            WebRootPath = webRoot.FullName,
            EnvironmentName = "Testing",
        });

        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(time);

        CloudApp.AddServices(
            builder.Services, db.ConnectionString, db.EngineConnectionString, rootApiKey, roomsApiKey, runDailyUpkeep: false);

        var app = builder.Build();

        var problem = await CloudApp.PrepareAsync(app.Services, NullLogger.Instance, Ct);
        Assert.Null(problem);

        CloudApp.MapEndpoints(app);
        await app.StartAsync(Ct);

        return new CloudTestHost(app, app.GetTestClient(), time, webRoot);
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
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/installs", new { clientVersion = "2026.9.0", platform = "windows" }, ip);
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

    /// <summary>Registers a Modbot deployment (rather than a desktop client) and returns its bearer.</summary>
    public async Task<(Guid Id, string Bearer)> RegisterServerAsync(string ip = "203.0.113.20")
    {
        using var response = await SendAsync(
            HttpMethod.Post, "/api/v1/installs", new { clientVersion = "2026.9.0", platform = "server" }, ip);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var id = body.GetProperty("installId").GetGuid();
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
            properties = properties ?? new { Count = 42 },
        };

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
        new { clientEventId = id, type, occurredAt, occurredBefore = (DateTimeOffset?)null, subjectId, worldId, instanceId, groupId, data = data ?? new { displayName = "Rin" } };

    public object Batch(params object[] events) => Batch(Time.GetUtcNow(), null, "unknown", events);

    public static object Batch(DateTimeOffset sentAt, long? clockOffsetMs, string clockConfidence, params object[] events) => new
    {
        batchId = Guid.NewGuid().ToString("n"),
        clientVersion = "2026.9.0",
        sentAt,
        clockOffsetMs,
        clockConfidence,
        modbotServerId = (string?)null,
        events,
    };

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

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset to) => _now = to;
}
