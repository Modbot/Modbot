using System.Net;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>
/// Spec 7.1's access rule, which is the security property of this whole feature.
/// </summary>
/// <remarks>
/// The wizard writes the VRChat credentials, the proxy and the managed group. Leaving it open
/// after setup would mean anyone who can reach the URL can re-point a running deployment at their
/// own group — so "open before the first account, closed after it" is not a convenience, it is
/// the difference between a setup wizard and a back door.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class OnboardingAccessTests
{
    private readonly PostgresFixture _db;

    public OnboardingAccessTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BeforeAnyAccountExists_TheWizardIsOpen()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var response = await host.PostAsync(
            "/api/onboarding/administrator",
            new { username = "first", password = "a-long-enough-password", email = "first@example.com" },
            cookie: null,
            Ct);

        // There is nobody to authenticate as yet. If this required a session, a fresh deployment
        // would be permanently unusable.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OnceAnAccountExists_TheWizardRefusesAnonymousCallers()
    {
        var (host, _) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/vrchat",
            new { username = "modbot@example.com", password = "hunter2" },
            cookie: null,
            Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AStaffAccountWithoutManageSettings_IsRefused()
    {
        var (host, _) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        await host.CreateUserAsync("viewer", "a-long-enough-password", ModbotPermissions.ViewMembers, Ct);
        var cookie = await host.LoginAsync("viewer", "a-long-enough-password", Ct);

        var response = await host.PostAsync(
            "/api/onboarding/connection-test", new { }, cookie, Ct);

        // 403, not 404 and not 401: they are signed in, they are simply not allowed. Hiding the
        // endpoint would only make the missing permission harder to diagnose.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAdministrator_IsAllowedThrough()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/connection-test", new { }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheStatusEndpointStaysOpen()
    {
        var (host, _) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        var response = await host.GetAsync("/api/onboarding/status", cookie: null, Ct);

        // The SPA has to be able to ask "is this deployment set up?" before it knows whether to
        // show a login form or a wizard, and it asks that while holding nothing.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.ReadJsonAsync(Ct);
        Assert.True(body.GetProperty("hasAdministrator").GetBoolean());
        Assert.False(body.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task TheStatusEndpointNeverReturnsASecret()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await host.PostAsync(
            "/api/onboarding/vrchat",
            new { username = "modbot@example.com", password = "the-vrchat-password", totpSecret = "JBSWY3DPEHPK3PXP" },
            cookie,
            Ct);

        await host.PostAsync(
            "/api/onboarding/connection-test",
            new { useProxy = true, proxyUrl = "http://proxy.example.com:11202", proxyUsername = "u", proxyPassword = "the-proxy-password" },
            cookie,
            Ct);

        await host.PostAsync(
            "/api/onboarding/integrations",
            new { discord = new { botToken = "the-discord-token" }, smtp = new { host = "smtp.example.com", password = "the-smtp-password" } },
            cookie,
            Ct);

        var status = await host.GetAsync("/api/onboarding/status", cookie, Ct);
        var raw = await status.Content.ReadAsStringAsync(Ct);

        // Spec 5.9.3 and 4.4.1. Read-back of a stored secret is how secrets escape into browser
        // caches, screenshots and bug reports -- so the answer is "configured: true", never the
        // value.
        foreach (var secret in new[]
                 {
                     "the-vrchat-password", "JBSWY3DPEHPK3PXP",
                     "the-proxy-password", "the-discord-token", "the-smtp-password",
                 })
        {
            Assert.DoesNotContain(secret, raw, StringComparison.Ordinal);
        }
    }
}
