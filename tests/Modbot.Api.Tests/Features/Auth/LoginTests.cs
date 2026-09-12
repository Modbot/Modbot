using System.Net;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Auth;

[Collection(nameof(PostgresCollection))]
public class LoginTests
{
    private readonly PostgresFixture _db;

    public LoginTests(PostgresFixture db) => _db = db;

    private static string UniqueName() => $"u_{Guid.NewGuid():N}";

    [Fact]
    public async Task CorrectCredentials_ReturnOkAndASessionCookie()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        await host.CreateUserAsync(name, "hunter2", ModbotPermissions.ViewMembers, ct);

        var response = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = name, password = "hunter2" }, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Set-Cookie", response.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task TheSessionCookie_IsHttpOnlySecureAndSameSiteLax()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        await host.CreateUserAsync(name, "hunter2", ModbotPermissions.None, ct);

        var response = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = name, password = "hunter2" }, ct);

        var raw = Assert.Single(response.Headers.GetValues("Set-Cookie"));

        // Spec 7.2, and the reason each one is there: HttpOnly keeps the session out of reach of
        // any script that gets injected into the SPA, Secure keeps it off plaintext connections,
        // and Lax is what lets an operator follow a link into Modbot and still be signed in.
        Assert.Contains("httponly", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AWrongPassword_Returns401AndNoCookie()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        await host.CreateUserAsync(name, "hunter2", ModbotPermissions.None, ct);

        var response = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = name, password = "wrong" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("Set-Cookie", response.Headers.Select(h => h.Key));
    }

    [Fact]
    public async Task AnUnknownUserAndAWrongPassword_AreIndistinguishable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        await host.CreateUserAsync(name, "hunter2", ModbotPermissions.None, ct);

        var wrongPassword = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = name, password = "wrong" }, ct);
        var noSuchUser = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = UniqueName(), password = "wrong" }, ct);

        // A login form that tells them apart is a membership oracle.
        Assert.Equal(wrongPassword.StatusCode, noSuchUser.StatusCode);
        Assert.Equal(
            await wrongPassword.Content.ReadAsStringAsync(ct),
            await noSuchUser.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task Me_RequiresASession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);

        var response = await host.Client.GetAsync("/api/auth/me", ct);

        // 401, not a redirect to a login page: this is an API, and the SPA needs a status it can
        // act on rather than the HTML of a page that does not exist.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_ReturnsTheSignedInUser()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        await host.CreateUserAsync(name, "hunter2", ModbotPermissions.ViewAuditLog, ct);
        var cookie = await host.LoginAsync(name, "hunter2", ct);

        var response = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, "/api/auth/me", cookie), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains(name, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logout_EndsTheSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        await host.CreateUserAsync(name, "hunter2", ModbotPermissions.None, ct);
        var cookie = await host.LoginAsync(name, "hunter2", ct);

        var logout = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Post, "/api/auth/logout", cookie), ct);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var expired = ApiTestHost.SessionCookie(logout);
        var after = await host.Client.SendAsync(
            host.Authenticated(HttpMethod.Get, "/api/auth/me", expired), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task ADisabledAccount_CannotSignIn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await ApiTestHost.StartAsync(_db);
        var name = UniqueName();
        var user = await host.CreateUserAsync(name, "hunter2", ModbotPermissions.None, ct);

        using (var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                   .CreateScope(host.Services))
        {
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Core.Data.ModbotContext>(scope.ServiceProvider);
            var tracked = await db.Users.FindAsync([user.Id], ct);
            tracked!.IsDisabled = true;
            await db.SaveChangesAsync(ct);
        }

        var response = await host.Client.PostAsJsonAsync(
            "/api/auth/login", new { username = name, password = "hunter2" }, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
