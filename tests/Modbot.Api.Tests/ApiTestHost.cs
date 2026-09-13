using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api;
using Modbot.Api.Auth;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.Core.Security;
using Modbot.Core.Time;
using Modbot.Api.Tests.Fakes;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Scheduling;

namespace Modbot.Api.Tests;

/// <summary>
/// A real ASP.NET Core pipeline over the real database, driven in-process.
/// </summary>
/// <remarks>
/// Built by hand rather than through <c>WebApplicationFactory</c> because Modbot.Api is a class
/// library with no entry point of its own -- composition is the host's job (spec 2.5). Probe
/// endpoints guarded by <c>RequiresFlag</c> live here rather than in the shipped surface, so the
/// authorisation tests do not depend on which real endpoints happen to exist yet.
/// </remarks>
public sealed class ApiTestHost : IAsyncDisposable
{
    public const string OpenProbe = "/probe/open";
    public const string AuditProbe = "/probe/audit";
    public const string TwoFlagProbe = "/probe/audit-and-settings";

    private readonly WebApplication _app;

    private ApiTestHost(WebApplication app, HttpClient client, FakeClock clock, FakeVRChatGate gate)
    {
        _app = app;
        Client = client;
        Clock = clock;
        VRChat = gate;
    }

    public HttpClient Client { get; }

    public FakeClock Clock { get; }

    /// <summary>The scripted gate. Nothing in these tests reaches the real VRChat API.</summary>
    public FakeVRChatGate VRChat { get; }

    public IServiceProvider Services => _app.Services;

    /// <param name="configure">
    /// Last word on the container, after every registration the host makes: a test substitutes
    /// the email sender or the delay scheduler here, and the later registration wins.
    /// </param>
    public static async Task<ApiTestHost> StartAsync(
        PostgresFixture db,
        FakeVRChatGate? gate = null,
        Action<IServiceCollection>? configure = null)
    {
        var clock = new FakeClock();
        gate ??= new FakeVRChatGate();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddDbContext<ModbotContext>(o => o.UseNpgsql(db.ConnectionString));
        builder.Services.AddSingleton<IModbotClock>(clock);

        // Elapsed time and wall-clock time are different questions (spec 4.4). The connection
        // check reports how long the attempt took, which is the monotonic one.
        builder.Services.AddSingleton<IMonotonicClock>(new StopwatchMonotonicClock());

        // The real protector, over the real database: the onboarding slices store encrypted
        // secrets and a test that substituted a passthrough would not prove they round-trip.
        builder.Services.AddSingleton<ISecretProtector>(services =>
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<ModbotContext>();
            return AesGcmSecretProtector.CreateAsync(context).GetAwaiter().GetResult();
        });

        builder.Services.AddSingleton<IVRChatGate>(gate);

        // Account events are facts, so the account slices write through the real writer against
        // real partitions -- without the hosted maintenance services, which would race the tests.
        builder.Services.AddScoped<Modbot.Analytics.Facts.IFactWriter, Modbot.Analytics.Facts.FactWriter>();
        builder.Services.AddScoped<Modbot.Analytics.Facts.EventPartitionMaintainer>();

        builder.Services.AddModbotAuth();
        builder.Services.AddModbotApi();

        configure?.Invoke(builder.Services);

        var app = builder.Build();

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapModbotApi();
        app.MapGet(OpenProbe, () => Results.Ok("open"));
        app.MapGet(AuditProbe, () => Results.Ok("audit"))
           .RequiresFlag(ModbotPermissions.ViewAuditLog);
        app.MapGet(TwoFlagProbe, () => Results.Ok("both"))
           .RequiresFlag(ModbotPermissions.ViewAuditLog | ModbotPermissions.ManageSettings);

        await app.StartAsync();

        // https, because the session cookie is marked Secure and a cookie jar would refuse to
        // store it over plain http -- exactly as a browser would.
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost/");

        return new ApiTestHost(app, client, clock, gate);
    }

    /// <summary>
    /// Puts the deployment back to "nobody has ever set this up".
    /// </summary>
    /// <remarks>
    /// The settings row is a singleton and the user table is shared by every test in this
    /// assembly, so onboarding tests -- which are almost entirely about the difference between
    /// "no staff account exists" and "one does" -- have to establish that difference rather than
    /// inherit whatever the previous test left. Safe because the whole assembly shares one
    /// collection and therefore runs serially.
    /// </remarks>
    public static async Task ResetDeploymentAsync(PostgresFixture db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        await using var context = db.NewContext();
        await context.Users.ExecuteDeleteAsync(ct);
        await context.Settings.ExecuteDeleteAsync(ct);
    }

    /// <summary>Signs in and returns the raw session cookie, ready for a <c>Cookie</c> header.</summary>
    public async Task<string> LoginAsync(string username, string password, CancellationToken ct)
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login", new { username, password }, ct);

        response.EnsureSuccessStatusCode();

        return SessionCookie(response);
    }

    public static string SessionCookie(HttpResponseMessage response)
    {
        var setCookie = Assert.Single(
            response.Headers.GetValues("Set-Cookie"),
            v => v.StartsWith(ModbotAuth.CookieName, StringComparison.Ordinal));

        return setCookie.Split(';')[0];
    }

    public HttpRequestMessage Authenticated(HttpMethod method, string path, string cookie)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        return request;
    }

    /// <summary>One request with an optional JSON body and an optional session.</summary>
    public Task<HttpResponseMessage> SendJsonAsync(
        HttpMethod method, string path, object? body, string? cookie, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);

        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);

        if (body is not null)
        {
            request.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(body),
                System.Text.Encoding.UTF8,
                "application/json");
        }

        return Client.SendAsync(request, ct);
    }

    public static async Task<System.Text.Json.JsonElement> BodyOf(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        return System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>
    /// A fact's payload, parsed. PostgreSQL rewrites jsonb on the way in -- spaces after colons,
    /// keys reordered -- so asserting on the raw string is asserting on Postgres's formatter.
    /// </summary>
    public static System.Text.Json.JsonElement DataOf(ModbotEvent fact)
        => System.Text.Json.JsonDocument.Parse(fact.Data).RootElement.Clone();

    /// <summary>The facts of one type about one subject, newest first, straight from the log.</summary>
    public async Task<List<ModbotEvent>> FactsAsync(string type, string subjectId, CancellationToken ct)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();

        return await db.Events.AsNoTracking()
            .Where(e => e.Type == type && e.SubjectId == subjectId)
            .OrderByDescending(e => e.Id)
            .ToListAsync(ct);
    }

    /// <summary>Signs in as a freshly created, linked account holding these permissions.</summary>
    public async Task<(ModbotUser User, string Cookie)> SignedInAsync(
        ModbotPermissions permissions, CancellationToken ct, bool linked = true)
    {
        var name = $"u_{Guid.NewGuid():N}";
        var user = await CreateUserAsync(name, TestAccounts.Password, permissions, ct, linked);
        return (user, await LoginAsync(name, TestAccounts.Password, ct));
    }

    /// <summary>
    /// An account holding exactly these permissions, linked to a VRChat account unless a test
    /// says otherwise -- the link is required everywhere but the link endpoints themselves.
    /// </summary>
    public async Task<ModbotUser> CreateUserAsync(
        string username, string password, ModbotPermissions permissions, CancellationToken ct, bool linked = true)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ModbotContext>();
        return await TestAccounts.CreateAsync(db, username, password, permissions, linked, ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

internal static class HttpContentJson
{
    public static Task<HttpResponseMessage> PostAsJsonAsync<T>(
        this HttpClient client, string path, T value, CancellationToken ct)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        var content = new StringContent(json);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return client.PostAsync(path, content, ct);
    }
}
