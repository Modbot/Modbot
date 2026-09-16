using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Modbot.Cloud.Features.Accounts;

namespace Modbot.Cloud.Tests;

/// <summary>
/// Registering, verifying, signing in and out, and changing a password or an address
/// (Cloud accounts and registry spec 2).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AccountTests(PostgresFixture db)
{
    private const string Email = "moderator@example.com";
    private const string Password = "a-long-enough-password";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Registering_sends_a_verification_mail_and_does_not_sign_anybody_in()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var registered = await host.SendAsync(HttpMethod.Post, "/api/v1/accounts", new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);

        Assert.Single(host.Mail.Sent);
        Assert.Equal(Email, host.Mail.Last.To);

        // Unverified cannot sign in. That is what stops somebody registering with an address that is
        // not theirs and using it to claim a server.
        using var signIn = await host.SendAsync(HttpMethod.Post, "/api/v1/accounts/session", new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, signIn.StatusCode);
    }

    [Fact]
    public async Task Registering_an_address_that_exists_answers_the_same_and_sends_nothing()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var first = await host.SendAsync(HttpMethod.Post, "/api/v1/accounts", new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        using var second = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts", new { email = Email, password = "another-long-password" }, ip: "203.0.113.7");

        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Single(host.Mail.Sent);
    }

    [Fact]
    public async Task Verifying_then_signing_in_sets_a_strict_cookie()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        var cookie = await SignUpAsync(host);

        Assert.Contains("HttpOnly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/accounts/me", cookie: Cookie(cookie));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        var body = await me.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(Email, body.GetProperty("email").GetString());
        Assert.True(body.GetProperty("emailVerified").GetBoolean());
    }

    [Fact]
    public async Task A_verification_token_works_once()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await host.SendAsync(HttpMethod.Post, "/api/v1/accounts", new { email = Email, password = Password });
        var token = host.Mail.LastToken();

        using var first = await host.SendAsync(HttpMethod.Post, "/api/v1/accounts/verify", new { token });
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        using var again = await host.SendAsync(HttpMethod.Post, "/api/v1/accounts/verify", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task A_verification_token_lapses_after_a_day()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        await host.SendAsync(HttpMethod.Post, "/api/v1/accounts", new { email = Email, password = Password });
        var token = host.Mail.LastToken();

        host.Time.Advance(AccountTokens.VerifyLifetime + TimeSpan.FromMinutes(1));

        using var late = await host.SendAsync(HttpMethod.Post, "/api/v1/accounts/verify", new { token });
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
    }

    [Fact]
    public async Task Signing_out_makes_the_cookie_useless()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var cookie = Cookie(await SignUpAsync(host));

        using var signedOut = await host.SendAsync(HttpMethod.Delete, "/api/v1/accounts/session", cookie: cookie);
        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);

        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/accounts/me", cookie: cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    [Fact]
    public async Task A_forgotten_password_is_reset_by_the_link_and_ends_every_session()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var cookie = Cookie(await SignUpAsync(host));

        using var asked = await host.SendAsync(HttpMethod.Post, "/api/v1/accounts/forgot-password", new { email = Email });
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);

        var token = host.Mail.LastToken();

        using var reset = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts/reset-password", new { token, password = "a-brand-new-password" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // The session an attacker might have left behind is gone with it.
        using var me = await host.SendAsync(HttpMethod.Get, "/api/v1/accounts/me", cookie: cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);

        using var signIn = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts/session", new { email = Email, password = "a-brand-new-password" });
        Assert.Equal(HttpStatusCode.NoContent, signIn.StatusCode);
    }

    [Fact]
    public async Task Asking_to_reset_an_address_nobody_registered_says_nothing()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var asked = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts/forgot-password", new { email = "nobody@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Empty(host.Mail.Sent);
    }

    [Fact]
    public async Task Changing_the_address_needs_the_password_and_lands_only_when_the_new_one_answers()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var cookie = Cookie(await SignUpAsync(host));

        using var wrong = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts/email", new { email = "new@example.com", password = "not-the-password" }, cookie: cookie);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        using var asked = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts/email", new { email = "new@example.com", password = Password }, cookie: cookie);
        Assert.Equal(HttpStatusCode.Accepted, asked.StatusCode);
        Assert.Equal("new@example.com", host.Mail.Last.To);

        // Still the old address until the link is used.
        using var before = await host.SendAsync(HttpMethod.Get, "/api/v1/accounts/me", cookie: cookie);
        var beforeBody = await before.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(Email, beforeBody.GetProperty("email").GetString());
        Assert.Equal("new@example.com", beforeBody.GetProperty("pendingEmail").GetString());

        using var confirmed = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts/verify-email-change", new { token = host.Mail.LastToken() });
        Assert.Equal(HttpStatusCode.NoContent, confirmed.StatusCode);

        using var after = await host.SendAsync(HttpMethod.Get, "/api/v1/accounts/me", cookie: cookie);
        var afterBody = await after.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("new@example.com", afterBody.GetProperty("email").GetString());
        Assert.Null(afterBody.GetProperty("pendingEmail").GetString());
    }

    [Fact]
    public async Task Changing_the_password_keeps_this_browser_and_drops_the_others()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var first = Cookie(await SignUpAsync(host));

        using var second = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts/session", new { email = Email, password = Password });
        var secondCookie = Cookie(SetCookie(second));

        using var changed = await host.SendAsync(
            HttpMethod.Post,
            "/api/v1/accounts/password",
            new { currentPassword = Password, password = "a-different-long-password" },
            cookie: first);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        var refreshed = Cookie(SetCookie(changed));

        using var stillIn = await host.SendAsync(HttpMethod.Get, "/api/v1/accounts/me", cookie: refreshed);
        Assert.Equal(HttpStatusCode.OK, stillIn.StatusCode);

        using var other = await host.SendAsync(HttpMethod.Get, "/api/v1/accounts/me", cookie: secondCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, other.StatusCode);
    }

    [Fact]
    public async Task A_short_password_is_refused()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        using var response = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts", new { email = Email, password = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Mail.Sent);
    }

    [Fact]
    public async Task With_no_mail_configured_registering_is_refused_rather_than_swallowed()
    {
        await using var host = await CloudTestHost.StartAsync(db, canSendMail: false);

        using var response = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts", new { email = Email, password = Password });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task An_account_session_does_not_open_admin()
    {
        await using var host = await CloudTestHost.StartAsync(db);
        var cookie = Cookie(await SignUpAsync(host));

        using var response = await host.SendAsync(HttpMethod.Get, "/api/admin/session", cookie: cookie);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Too_many_registrations_from_one_address_are_refused()
    {
        await using var host = await CloudTestHost.StartAsync(db);

        for (var i = 0; i < AccountLimits.RegistrationsPerHour; i++)
        {
            using var accepted = await host.SendAsync(
                HttpMethod.Post, "/api/v1/accounts", new { email = $"person{i}@example.com", password = Password }, ip: "203.0.113.50");
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using var refused = await host.SendAsync(
            HttpMethod.Post, "/api/v1/accounts", new { email = "one-more@example.com", password = Password }, ip: "203.0.113.50");

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Theory]
    [InlineData("nobody", false)]
    [InlineData("no@body", false)]
    [InlineData("@example.com", false)]
    [InlineData("two@at@example.com", false)]
    [InlineData("  Person@Example.COM  ", true)]
    public void An_address_is_normalised_or_refused(string raw, bool accepted)
    {
        Assert.Equal(accepted, EmailAddress.TryNormalise(raw, out var address));

        if (accepted)
            Assert.Equal("person@example.com", address);
    }

    /// <summary>Registers, verifies and signs in. Returns the <c>Set-Cookie</c> value.</summary>
    private static async Task<string> SignUpAsync(CloudTestHost host)
    {
        using var registered = await host.SendAsync(HttpMethod.Post, "/api/v1/accounts", new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);

        using var verified = await host.SendAsync(HttpMethod.Post, "/api/v1/accounts/verify", new { token = host.Mail.LastToken() });
        Assert.Equal(HttpStatusCode.NoContent, verified.StatusCode);

        using var signedIn = await host.SendAsync(HttpMethod.Post, "/api/v1/accounts/session", new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, signedIn.StatusCode);

        return SetCookie(signedIn);
    }

    private static string SetCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").First(v => v.StartsWith(AccountSessions.CookieName, StringComparison.Ordinal));

    /// <summary>The name and value only, the way a browser sends it back.</summary>
    private static string Cookie(string setCookie) => setCookie[..setCookie.IndexOf(';', StringComparison.Ordinal)];
}
