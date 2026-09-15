using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Modbot.My.Features.Admin;

namespace Modbot.My.Tests;

/// <summary>
/// Signing in to /admin with ROOT_API_KEY: the cookie, logging out, the attempt limit, and changing
/// the key.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class AdminSessionTests(PostgresFixture db)
{
    private const string Ip = "203.0.113.90";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> LoginAsync(MyTestHost host, string key, string ip = Ip) =>
        host.PostJsonAsync("/api/admin/login", new { key }, ip);

    private static string SessionCookie(HttpResponseMessage response)
    {
        var header = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        return header.Split(';')[0];
    }

    private static async Task<HttpStatusCode> StatsWithCookieAsync(MyTestHost host, string cookie)
    {
        using var response = await host.SendAsync(HttpMethod.Get, "/api/stats", cookie: cookie);
        return response.StatusCode;
    }

    [Fact]
    public async Task TheRightKeySetsAnHttpOnlySecureStrictCookieThatOpensAdmin()
    {
        await using var host = await MyTestHost.StartAsync(db);

        using var response = await LoginAsync(host, MyTestHost.RootKey);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var header = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith($"{AdminSessions.CookieName}=", header, StringComparison.Ordinal);
        Assert.DoesNotContain(MyTestHost.RootKey, header, StringComparison.Ordinal);

        var attributes = header.Split(';', StringSplitOptions.TrimEntries).Skip(1).ToList();
        var flags = attributes.Select(a => a.ToLowerInvariant()).ToList();
        Assert.Contains("httponly", flags);
        Assert.Contains("secure", flags);
        Assert.Contains("samesite=strict", flags);
        Assert.Contains("path=/", flags);

        var expires = attributes.Single(a => a.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))["expires=".Length..];
        Assert.Equal(
            host.Time.GetUtcNow() + AdminSessions.Lifetime,
            DateTimeOffset.ParseExact(expires, "r", CultureInfo.InvariantCulture));

        var cookie = SessionCookie(response);
        Assert.Equal(HttpStatusCode.OK, await StatsWithCookieAsync(host, cookie));
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/admin/session", cookie: cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, "/api/register-page-instances", cookie: cookie)).StatusCode);
    }

    [Fact]
    public async Task AWrongKeyIsRefusedAndSetsNoCookie()
    {
        await using var host = await MyTestHost.StartAsync(db);

        using var response = await LoginAsync(host, MyTestHost.RootKey + "x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("Wrong key.", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task LoggingOutEndsTheSession()
    {
        await using var host = await MyTestHost.StartAsync(db);
        var cookie = SessionCookie(await LoginAsync(host, MyTestHost.RootKey));

        using var logout = await host.SendAsync(HttpMethod.Post, "/api/admin/logout", cookie: cookie);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("expires=Thu, 01 Jan 1970", Assert.Single(logout.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);

        // The same cookie, replayed, no longer works: the session is gone on the server, not only in
        // the browser.
        Assert.Equal(HttpStatusCode.Unauthorized, await StatsWithCookieAsync(host, cookie));

        await using var context = db.NewContext();
        Assert.Empty(await context.AdminSessions.ToListAsync(Ct));
    }

    [Fact]
    public async Task ASessionEndsWhenItExpires()
    {
        await using var host = await MyTestHost.StartAsync(db);
        var cookie = SessionCookie(await LoginAsync(host, MyTestHost.RootKey));

        host.Time.Advance(AdminSessions.Lifetime - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.OK, await StatsWithCookieAsync(host, cookie));

        host.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatsWithCookieAsync(host, cookie));
    }

    [Fact]
    public async Task AForgedOrAlteredCookieIsRefused()
    {
        await using var host = await MyTestHost.StartAsync(db);
        var cookie = SessionCookie(await LoginAsync(host, MyTestHost.RootKey));
        var random = cookie[(cookie.IndexOf('=', StringComparison.Ordinal) + 1)..cookie.IndexOf('.', StringComparison.Ordinal)];

        var wrongSignature = $"{AdminSessions.CookieName}={random}.{Base64Url.EncodeToString(new byte[32])}";

        Assert.Equal(HttpStatusCode.Unauthorized, await StatsWithCookieAsync(host, wrongSignature));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatsWithCookieAsync(host, $"{AdminSessions.CookieName}=nonsense"));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatsWithCookieAsync(host, $"{AdminSessions.CookieName}={random}"));
    }

    [Fact]
    public async Task ChangingTheRootKeyEndsEverySession()
    {
        string cookie;
        await using (var before = await MyTestHost.StartAsync(db))
        {
            cookie = SessionCookie(await LoginAsync(before, MyTestHost.RootKey));
            Assert.Equal(HttpStatusCode.OK, await StatsWithCookieAsync(before, cookie));
        }

        const string newKey = "a-different-root-key-9876543210";
        await using var after = await MyTestHost.StartAsync(db, rootApiKey: newKey, resetDatabase: false);

        await using (var context = db.NewContext())
            Assert.Single(await context.AdminSessions.ToListAsync(Ct));

        Assert.Equal(HttpStatusCode.Unauthorized, await StatsWithCookieAsync(after, cookie));
        Assert.Equal(HttpStatusCode.OK, (await after.GetWithKeyAsync("/api/stats", newKey)).StatusCode);
    }

    [Fact]
    public async Task LoginAttemptsAreLimitedPerAddress()
    {
        await using var host = await MyTestHost.StartAsync(db);

        for (var i = 0; i < LoginAttempts.MaxFailures; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(host, "wrong")).StatusCode);

        using var blocked = await LoginAsync(host, MyTestHost.RootKey);
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        Assert.NotNull(blocked.Headers.RetryAfter);
        Assert.False(blocked.Headers.Contains("Set-Cookie"));

        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync(host, MyTestHost.RootKey, ip: "198.51.100.91")).StatusCode);

        host.Time.Advance(LoginAttempts.Window);
        Assert.Equal(HttpStatusCode.NoContent, (await LoginAsync(host, MyTestHost.RootKey)).StatusCode);
    }

    [Fact]
    public async Task WithNoRootKeySetNobodyCanSignIn()
    {
        await using var host = await MyTestHost.StartAsync(db, rootApiKey: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(host, "anything")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await host.SendAsync(HttpMethod.Get, "/api/admin/session", cookie: $"{AdminSessions.CookieName}=a.b")).StatusCode);
    }

    [Fact]
    public async Task TheBearerKeyStillWorksForScripts()
    {
        await using var host = await MyTestHost.StartAsync(db);

        Assert.Equal(HttpStatusCode.OK, (await host.GetWithKeyAsync("/api/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.GetWithKeyAsync("/api/admin/session")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await host.SendAsync(HttpMethod.Delete, "/api/instances/nobody", bearer: MyTestHost.RootKey)).StatusCode);
    }

    [Theory]
    [InlineData("/api/instances/nobody")]
    [InlineData("/api/register-page-instances?url=https://a.example")]
    public async Task DeletingNeedsAdmin(string path)
    {
        await using var host = await MyTestHost.StartAsync(db);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.SendAsync(HttpMethod.Delete, path)).StatusCode);
    }

    [Fact]
    public async Task DeletingARegisterPageEntryAlsoForgetsItsIpHistory()
    {
        await using var host = await MyTestHost.StartAsync(db);
        await host.GetAsync("/register?url=https://a.example", Ip);
        await host.GetAsync("/register?url=https://b.example", Ip);
        var cookie = SessionCookie(await LoginAsync(host, MyTestHost.RootKey));

        var path = $"/api/register-page-instances?url={Uri.EscapeDataString("https://a.example")}";
        Assert.Equal(HttpStatusCode.NoContent, (await host.SendAsync(HttpMethod.Delete, path, cookie: cookie)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Delete, path, cookie: cookie)).StatusCode);

        await using var context = db.NewContext();
        Assert.Equal("https://b.example", Assert.Single(await context.RegisterPageInstances.ToListAsync(Ct)).InstanceUrl);
        Assert.Equal("https://b.example", Assert.Single(await context.VisitorInstances.ToListAsync(Ct)).InstanceUrl);
    }
}
