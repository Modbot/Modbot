using System.Net;
using Modbot.Api.Tests.Fakes;
using Modbot.Api.Tests.Features.Audit;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;
using Modbot.VRChat;
using Modbot.VRChat.Session;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>
/// Foundation spec 4.1.2 from the API's side: the wizard and Settings keep to the sign-in wait, the
/// health endpoint reports it, and the session cookie never leaves the server.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class SignInWaitTests(PostgresFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    private const string SessionCookie = "the-live-vrchat-session-cookie";

    [Fact]
    public async Task NewCredentialsDuringAWaitGetAShortErrorAndAreKeptForTheAttemptAfter()
    {
        var gate = new FakeVRChatGate().WaitingToSignIn(Now, Now.AddSeconds(3572));
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(db, gate, Ct);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/vrchat",
            new { username = "modbot@example.com", password = "a-new-password", totpSecret = (string?)null },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.ReadJsonAsync(Ct);
        Assert.Equal("SignInWaiting", body.GetProperty("outcome").GetString());
        Assert.Equal("Waiting to sign in to VRChat.", body.GetProperty("headline").GetString());
        Assert.Equal(string.Empty, body.GetProperty("detail").GetString());
        Assert.False(body.GetProperty("proxyWouldHelp").GetBoolean());

        // The gate was asked and said no; nothing here went round it.
        Assert.Equal(1, gate.SignInCalls);

        var settings = await OnboardingTestContext.ReadSettingsAsync(db, Ct);
        Assert.Equal("modbot@example.com", settings.VRChatUsername);
        Assert.Null(settings.VRChatVerifiedAt);
    }

    [Fact]
    public async Task ChangingTheCredentialsDiscardsTheStoredSession()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(db, gate, Ct);
        await using var _host = host;

        await host.PostAsync("/api/onboarding/vrchat", new { username = "modbot@example.com", password = "first" }, cookie, Ct);
        await SeedSessionAsync("modbot@example.com");

        await host.PostAsync("/api/onboarding/vrchat", new { username = "modbot@example.com", password = "second" }, cookie, Ct);

        var settings = await OnboardingTestContext.ReadSettingsAsync(db, Ct);
        Assert.Null(settings.VRChatAuthCookieEncrypted);
        Assert.Null(settings.VRChatSessionAccount);
        Assert.Null(settings.VRChatSessionUserId);
    }

    [Fact]
    public async Task EnteringTheSameCredentialsAgainKeepsTheSession()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(db, gate, Ct);
        await using var _host = host;

        var credentials = new { username = "modbot@example.com", password = "same", totpSecret = "JBSW Y3DP EHPK 3PXP" };

        await host.PostAsync("/api/onboarding/vrchat", credentials, cookie, Ct);
        await SeedSessionAsync("modbot@example.com");

        var again = await host.PostAsync("/api/onboarding/vrchat", credentials, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        // Pressing the button twice should not spend a sign-in: the session is kept and checked.
        var settings = await OnboardingTestContext.ReadSettingsAsync(db, Ct);
        Assert.Equal(SessionCookie, settings.VRChatAuthCookieEncrypted);
    }

    [Fact]
    public async Task TheGateHealthCarriesTheWaitCountedOnTheServersClock()
    {
        var gate = new FakeVRChatGate().WaitingToSignIn(Now, Now.AddSeconds(3572));
        gate.SignInStatus = gate.SignInStatus! with { LastSignedInAt = Now.AddMinutes(-3) };

        await using var host = await ReadSurfaceTestHost.StartAsync(db, gate);

        // Any signed-in account, whatever it may do: the banner is for everyone.
        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var response = await host.GetAsync("/api/health/gate", cookie, Ct);
        var body = await response.ReadJsonAsync(Ct);

        Assert.Equal("SignInWaiting", body.GetProperty("state").GetString());
        Assert.Equal("WaitingOnPurpose", body.GetProperty("status").GetString());

        var wait = body.GetProperty("signInWait");
        Assert.Equal("RateLimitedByVRChat", wait.GetProperty("reason").GetString());
        Assert.Equal(Now.AddSeconds(3572), wait.GetProperty("retryAt").GetDateTimeOffset());
        Assert.Equal(3572, wait.GetProperty("secondsLeft").GetInt32());

        Assert.Equal(Now.AddMinutes(-3), body.GetProperty("lastSignedInAt").GetDateTimeOffset());
        Assert.Equal(4, body.GetProperty("signInsInLastHour").GetInt32());
        Assert.Equal(4, body.GetProperty("signInLimit").GetInt32());
    }

    [Fact]
    public async Task WithoutAWaitTheBannerFieldIsNull()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        await using var host = await ReadSurfaceTestHost.StartAsync(db, gate);

        var cookie = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var body = await (await host.GetAsync("/api/health/gate", cookie, Ct)).ReadJsonAsync(Ct);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("signInWait").ValueKind);
    }

    [Fact]
    public async Task TheSessionCookieIsNeverReturned()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        await using var host = await ReadSurfaceTestHost.StartAsync(db, gate);
        await SeedSessionAsync("modbot@example.com");

        var cookie = await host.SignedInAsync(ModbotPermissions.Administrator, Ct);

        foreach (var path in new[] { "/api/onboarding/status", "/api/health/gate", "/api/health/sync" })
        {
            var response = await host.GetAsync(path, cookie, Ct);
            var raw = await response.Content.ReadAsStringAsync(Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(SessionCookie, raw, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A stored session, written straight to the row. Stored unencrypted here on purpose: the
    /// assertion is that the column's contents never reach a response, whatever they are.
    /// </summary>
    private async Task SeedSessionAsync(string account)
    {
        await using var context = db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);
        settings.VRChatAuthCookieEncrypted = SessionCookie;
        settings.VRChatSessionAccount = account;
        settings.VRChatSessionUserId = "usr_modbot";
        await context.SaveChangesAsync(Ct);
    }
}
