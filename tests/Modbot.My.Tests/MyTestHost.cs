using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Modbot.My.Tests;

/// <summary>
/// The app that ships, built through <see cref="MyApp"/> over a test server and the real database.
/// </summary>
/// <remarks>
/// <para>
/// Requests go through the helpers here, which carry the test's cancellation token, rather than
/// through <see cref="HttpClient"/> directly.
/// </para>
/// <para>
/// The test server has no connection address, so a test gives a request its client address as an
/// <c>X-Forwarded-For</c> header, the way Railway's edge does.
/// </para>
/// </remarks>
public sealed class MyTestHost : IAsyncDisposable
{
    public const string RootKey = "a-root-key-for-tests-only-0123456789";

    /// <summary>Stands in for the Vite build, which the .NET tests do not run.</summary>
    public const string AppHtml =
        "<!doctype html><html><head><title>my.modbot.co</title></head><body><div id=\"root\"></div></body></html>";

    public const string AssetPath = "/assets/app-test.js";

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly DirectoryInfo _webRoot;

    private MyTestHost(WebApplication app, HttpClient client, ManualTime time, DirectoryInfo webRoot)
    {
        _app = app;
        _client = client;
        _webRoot = webRoot;
        Time = time;
    }

    public ManualTime Time { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="rootApiKey">The server's ROOT_API_KEY. Null starts a server with none set.</param>
    /// <param name="resetDatabase">False keeps what an earlier host in the same test wrote.</param>
    public static async Task<MyTestHost> StartAsync(PostgresFixture db, string? rootApiKey = RootKey, bool resetDatabase = true)
    {
        if (resetDatabase)
            await db.ResetAsync();

        var source = SourceDirectory();
        var time = new ManualTime(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));

        var webRoot = Directory.CreateTempSubdirectory("modbot-my-tests-");
        await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "index.html"), AppHtml, Ct);
        Directory.CreateDirectory(Path.Combine(webRoot.FullName, "assets"));
        await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "assets", "app-test.js"), "export {}", Ct);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = source,
            WebRootPath = webRoot.FullName,
            EnvironmentName = "Testing",
        });

        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(time);

        MyApp.AddServices(builder.Services, db.ConnectionString, rootApiKey);

        var app = builder.Build();
        MyApp.MapEndpoints(app);
        await app.StartAsync(Ct);

        return new MyTestHost(app, app.GetTestClient(), time, webRoot);
    }

    /// <summary>A request with an optional JSON body, client address, cookie, bearer key and headers.</summary>
    public Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string? ip = null,
        string? cookie = null,
        string? bearer = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(method, path);

        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (ip is not null)
            request.Headers.TryAddWithoutValidation("X-Forwarded-For", ip);
        if (cookie is not null)
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, value);

        return _client.SendAsync(request, Ct);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? ip = null) => SendAsync(HttpMethod.Get, path, ip: ip);

    public Task<string> GetStringAsync(string path) => _client.GetStringAsync(path, Ct);

    public Task<HttpResponseMessage> PostJsonAsync(string path, object body, string? ip = null) =>
        SendAsync(HttpMethod.Post, path, body, ip);

    /// <summary>A GET carrying <c>Authorization: Bearer &lt;key&gt;</c>, or no header when the key is null.</summary>
    public Task<HttpResponseMessage> GetWithKeyAsync(string path, string? key = RootKey) =>
        SendAsync(HttpMethod.Get, path, bearer: key);

    /// <summary>A GET with the root key, read as JSON.</summary>
    public async Task<JsonElement> ReadWithKeyAsync(string path)
    {
        using var response = await GetWithKeyAsync(path);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
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

    /// <summary>The Modbot.My source folder, so the test server serves the real <c>termlists</c>.</summary>
    private static string SourceDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Modbot.slnx")))
                return Path.Combine(dir.FullName, "src", "Modbot.My");
        }

        throw new InvalidOperationException("Could not find the repository root above the test output.");
    }
}

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
