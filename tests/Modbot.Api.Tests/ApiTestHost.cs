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
using Modbot.Core.Time;
using Modbot.TestSupport;

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

    private ApiTestHost(WebApplication app, HttpClient client, FakeClock clock)
    {
        _app = app;
        Client = client;
        Clock = clock;
    }

    public HttpClient Client { get; }

    public FakeClock Clock { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<ApiTestHost> StartAsync(PostgresFixture db)
    {
        var clock = new FakeClock();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddDbContext<ModbotContext>(o => o.UseNpgsql(db.ConnectionString));
        builder.Services.AddSingleton<IModbotClock>(clock);
        builder.Services.AddModbotAuth();
        builder.Services.AddModbotApi();

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

        return new ApiTestHost(app, client, clock);
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

    public async Task<ModbotUser> CreateUserAsync(
        string username, string password, ModbotPermissions permissions, CancellationToken ct)
    {
        using var scope = Services.CreateScope();
        var accounts = scope.ServiceProvider.GetRequiredService<Core.Users.UserAccountService>();
        return await accounts.CreateAsync(username, password, permissions, ct);
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
