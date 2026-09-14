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
/// Requests go through the helpers here, which carry the test's cancellation token, rather than
/// through <see cref="HttpClient"/> directly.
/// </remarks>
public sealed class MyTestHost : IAsyncDisposable
{
    public const string RootKey = "a-root-key-for-tests-only-0123456789";

    private readonly WebApplication _app;
    private readonly HttpClient _client;

    private MyTestHost(WebApplication app, HttpClient client, ManualTime time)
    {
        _app = app;
        _client = client;
        Time = time;
    }

    public ManualTime Time { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="rootApiKey">The server's ROOT_API_KEY. Null starts a server with none set.</param>
    public static async Task<MyTestHost> StartAsync(PostgresFixture db, string? rootApiKey = RootKey)
    {
        await db.ResetAsync();

        var source = SourceDirectory();
        var time = new ManualTime(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = source,
            WebRootPath = Path.Combine(source, "wwwroot"),
            EnvironmentName = "Testing",
        });

        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<TimeProvider>(time);

        MyApp.AddServices(builder.Services, db.ConnectionString, rootApiKey);

        var app = builder.Build();
        MyApp.MapEndpoints(app);
        await app.StartAsync(Ct);

        return new MyTestHost(app, app.GetTestClient(), time);
    }

    public Task<HttpResponseMessage> GetAsync(string path) => _client.GetAsync(path, Ct);

    public Task<string> GetStringAsync(string path) => _client.GetStringAsync(path, Ct);

    public Task<HttpResponseMessage> PostJsonAsync(string path, object body) => _client.PostAsJsonAsync(path, body, Ct);

    /// <summary>A GET carrying <c>Authorization: Bearer &lt;key&gt;</c>, or no header when the key is null.</summary>
    public Task<HttpResponseMessage> GetWithKeyAsync(string path, string? key = RootKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        return _client.SendAsync(request, Ct);
    }

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
    }

    /// <summary>
    /// The Modbot.My source folder, so the test server serves the real <c>wwwroot</c> and
    /// <c>termlists</c> rather than copies.
    /// </summary>
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
