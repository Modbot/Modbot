using System.Net;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>Spec 7.1 step 1.</summary>
[Collection(nameof(PostgresCollection))]
public class CreateAdminTests
{
    private readonly PostgresFixture _db;

    public CreateAdminTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheFirstAccountGetsAdministratorAndIsSignedInImmediately()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new { username = "bin", password = "a-long-enough-password", email = "bin@example.com" },
            cookie: null,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.ReadJsonAsync(Ct);
        Assert.Equal("bin", body.GetProperty("username").GetString());
        Assert.Equal((long)ModbotPermissions.Administrator, body.GetProperty("permissions").GetInt64());

        // Signed in on the spot, because the next wizard step requires a session and bouncing
        // someone to a login form mid-setup reads as the setup having failed.
        var cookie = ApiTestHost.SessionCookie(response);
        var me = await host.GetAsync("/api/auth/me", cookie, Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]
    public async Task TheAccountIsUsableWithTheOrdinaryLoginEndpoint()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        await host.PostAsync(
            "/api/onboarding/administrator",
            new { username = "bin", password = "a-long-enough-password", email = "bin@example.com" },
            cookie: null,
            Ct);

        // The wizard must not create a second class of account that only it understands.
        var cookie = await host.LoginAsync("bin", "a-long-enough-password", Ct);
        Assert.NotEmpty(cookie);
    }

    [Fact]
    public async Task AShortPasswordIsRefused()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new { username = "bin", password = "short" },
            cookie: null,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AMismatchedConfirmationIsRefused()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new
            {
                username = "bin",
                password = "a-long-enough-password",
                confirmPassword = "a-different-password",
            },
            cookie: null,
            Ct);

        // Re-checked on the server, because a mismatch that slips through creates an account with
        // a password nobody knows and no way back in.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ASecondAnonymousAttemptIsRefused()
    {
        var (host, _) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new { username = "intruder", password = "a-long-enough-password" },
            cookie: null,
            Ct);

        // The window in which anyone can create an account closes the moment the first one
        // exists, and it never reopens.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ADuplicateUsernameConflictsRegardlessOfCase()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new
            {
                username = OnboardingTestContext.AdminUsername.ToUpperInvariant(),
                password = "a-long-enough-password",
                email = "someone-else@example.com",
            },
            cookie,
            Ct);

        // 409 rather than a 500 out of the unique index: "Owner" and "owner" are the same account
        // (spec 7.2's normalised username), and the operator should be told so.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task TheSecondAccountDoesNotGetAdministrator()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new { username = "colleague", password = "a-long-enough-password", email = "colleague@example.com" },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.ReadJsonAsync(Ct);
        var permissions = (ModbotPermissions)body.GetProperty("permissions").GetInt64();

        // Administrator is for the account the wizard created out of nothing. Re-running step 1
        // later must not hand out the same thing, or "add a colleague" quietly becomes
        // "add another owner".
        Assert.False(permissions.HasFlag(ModbotPermissions.Administrator));
    }

    [Fact]
    public async Task ThePasswordIsNeverEchoedBack()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new { username = "bin", password = "a-very-distinctive-password", email = "bin@example.com" },
            cookie: null,
            Ct);

        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("a-very-distinctive-password", raw, StringComparison.Ordinal);
    }
}
