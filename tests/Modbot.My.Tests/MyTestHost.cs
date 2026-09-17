using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Modbot.My.Cloud;
using Modbot.My.Configuration;

namespace Modbot.My.Tests;

/// <summary>
/// The app that ships, built through <see cref="MyApp"/> over a test server, with a stand-in for
/// Modbot Cloud.
/// </summary>
/// <remarks>
/// <para>
/// There is no database: my.modbot.co keeps nothing and reads everything from Cloud (central
/// services spec 2.1.1). <see cref="Cloud"/> answers in its place and records what was asked.
/// </para>
/// <para>
/// The test server has no connection address, so a test gives a request its client address as an
/// <c>X-Forwarded-For</c> header, the way Railway's edge does.
/// </para>
/// </remarks>
public sealed class MyTestHost : IAsyncDisposable
{
    /// <summary>Stands in for the Vite build, which the .NET tests do not run.</summary>
    public const string AppHtml =
        "<!doctype html><html><head><title>my.modbot.co</title></head><body><div id=\"root\"></div></body></html>";

    public const string AssetPath = "/assets/app-test.js";

    public const string ApiKey = "a-cloud-key-for-tests-only-0123456789";

    /// <summary>The Modbot Cloud the test server reads from. No request ever leaves the process.</summary>
    public static readonly Uri CloudEndpoint = new("https://cloud.modbot.test/");

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly DirectoryInfo _webRoot;

    private MyTestHost(WebApplication app, HttpClient client, ManualTime time, DirectoryInfo webRoot, FakeCloud cloud)
    {
        _app = app;
        _client = client;
        _webRoot = webRoot;
        Time = time;
        Cloud = cloud;
    }

    public ManualTime Time { get; }

    /// <summary>What my.modbot.co asked Cloud, and what Cloud answered.</summary>
    public FakeCloud Cloud { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="apiKey">The key sent to Cloud. Null starts a service with none configured.</param>
    public static async Task<MyTestHost> StartAsync(string? apiKey = ApiKey)
    {
        var time = new ManualTime(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));

        var webRoot = Directory.CreateTempSubdirectory("modbot-my-tests-");
        await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "index.html"), AppHtml, Ct);
        Directory.CreateDirectory(Path.Combine(webRoot.FullName, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "assets", "app-test.js"), "export {}", Ct);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            WebRootPath = webRoot.FullName,
            EnvironmentName = "Testing",
        });

        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(time);

        MyApp.AddServices(builder.Services, new CloudAddress(CloudEndpoint, apiKey));

        // After AddServices, so this configures the named client it registered rather than replacing
        // it: nothing in a test reaches the network.
        var cloud = new FakeCloud();
        builder.Services.AddHttpClient(CloudClient.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => cloud);

        var app = builder.Build();
        MyApp.MapEndpoints(app);
        await app.StartAsync(Ct);

        return new MyTestHost(app, app.GetTestClient(), time, webRoot, cloud);
    }

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? ip = null)
    {
        var request = new HttpRequestMessage(method, path);

        if (body is not null)
            request.Content = System.Net.Http.Json.JsonContent.Create(body);
        if (ip is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);

        return _client.SendAsync(request, Ct);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? ip = null) =>
        SendAsync(HttpMethod.Get, path, ip: ip);

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

/// <summary>One call my.modbot.co made to Cloud.</summary>
public sealed record CloudCall(HttpMethod Method, Uri Url, string? Authorization, string Body);

/// <summary>
/// Stands in for Modbot Cloud. Records every call and answers what the test told it to.
/// </summary>
public sealed class FakeCloud : HttpMessageHandler
{
    private readonly List<CloudCall> _calls = [];

    public IReadOnlyList<CloudCall> Calls
    {
        get
        {
            lock (_calls)
                return [.. _calls];
        }
    }

    public CloudCall Last => Calls[^1];

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Body { get; set; } = """{"items":[]}""";

    /// <summary>True to fail the way an unreachable Cloud does.</summary>
    public bool Unreachable { get; set; }

    /// <summary>Set, every call waits here until the test lets it go: a Cloud that hangs.</summary>
    public TaskCompletionSource? Hold { get; set; }

    private readonly SemaphoreSlim _called = new(0);

    /// <summary>Waits for the next call to arrive, for a note the app does not wait on itself.</summary>
    public async Task<CloudCall> NextCallAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await _called.WaitAsync(timeout.Token);
        return Last;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);

        lock (_calls)
        {
            _calls.Add(new CloudCall(
                request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
        }

        _called.Release();

        if (Hold is { } hold)
            await hold.Task.WaitAsync(ct);

        if (Unreachable)
            throw new HttpRequestException("Modbot Cloud is unreachable in this test.");

        return new HttpResponseMessage(Status)
        {
            Content = new StringContent(Body, Encoding.UTF8, "application/json"),
        };
    }
}

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
