using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Security;
using Modbot.TestSupport;
using Modbot.VRChat;
using VRChat.API.Model;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>
/// Spec 7.1.1 — the step this feature exists for.
/// </summary>
/// <remarks>
/// One decision has to come out right: <c>proxyWouldHelp</c> is true for a Cloudflare WAF block
/// and false for everything else. Getting it wrong in one direction leaves a VPS deployment
/// permanently broken with no hint why; in the other it sends an operator with a DNS problem or a
/// typo'd password to go and buy a proxy subscription.
/// </remarks>
[Collection(nameof(PostgresCollection))]
public class TestConnectionTests
{
    private readonly PostgresFixture _db;

    public TestConnectionTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeVRChatGate Failing(VRChatFailureKind kind, int status = 0, int? wafCode = null) =>
        new()
        {
            SignIn = VRChatResult<CurrentUserLoginResponse>.Failure(status, "failed", wafCode, kind: kind),
        };

    [Theory]
    [InlineData(VRChatFailureKind.WafBlocked, "WafBlocked", true)]
    [InlineData(VRChatFailureKind.NameResolution, "DnsFailure", false)]
    [InlineData(VRChatFailureKind.Timeout, "Timeout", false)]
    [InlineData(VRChatFailureKind.Network, "NetworkFailure", false)]
    [InlineData(VRChatFailureKind.CredentialsRejected, "CredentialsRejected", false)]
    [InlineData(VRChatFailureKind.TwoFactorMissing, "TwoFactorMissing", false)]
    [InlineData(VRChatFailureKind.RateLimited, "RateLimited", false)]
    public async Task EachFailureIsToldApartAndOnlyOneOfThemOffersAProxy(
        VRChatFailureKind kind, string expectedOutcome, bool proxyWouldHelp)
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, Failing(kind), Ct);
        await using var _host = host;

        var response = await host.PostAsync("/api/onboarding/connection-test", new { }, cookie, Ct);

        // 200: a failed check is a successful diagnosis, and the diagnosis is what was asked for.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.ReadJsonAsync(Ct);
        Assert.Equal(expectedOutcome, body.GetProperty("outcome").GetString());
        Assert.Equal(proxyWouldHelp, body.GetProperty("proxyWouldHelp").GetBoolean());

        // Every failure has to say what to do next, or the operator is left with a red box.
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("nextStep").GetString()));
    }

    [Fact]
    public async Task AWafBlockNamesAKnownWorkingProxyProvider()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(
            _db, Failing(VRChatFailureKind.WafBlocked, 403, 1020), Ct);
        await using var _host = host;

        var response = await host.PostAsync("/api/onboarding/connection-test", new { }, cookie, Ct);
        var body = await response.ReadJsonAsync(Ct);

        // "Find a proxy" is not actionable advice to somebody who learned proxies exist ninety
        // seconds ago. Spec 7.1.1 names one on purpose.
        Assert.Contains("iproyal.com", body.GetProperty("nextStep").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADnsFailureSaysPlainlyThatAProxyWillNotHelp()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(
            _db, Failing(VRChatFailureKind.NameResolution), Ct);
        await using var _host = host;

        var response = await host.PostAsync("/api/onboarding/connection-test", new { }, cookie, Ct);
        var body = await response.ReadJsonAsync(Ct);

        Assert.Contains(
            "proxy will not help", body.GetProperty("nextStep").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APassingCheckRecordsWhenItPassed()
    {
        var gate = new FakeVRChatGate().SignedInAs("ModbotBot");
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var response = await host.PostAsync("/api/onboarding/connection-test", new { }, cookie, Ct);
        var body = await response.ReadJsonAsync(Ct);

        Assert.Equal("Ok", body.GetProperty("outcome").GetString());
        Assert.False(body.GetProperty("proxyWouldHelp").GetBoolean());
        Assert.Equal("ModbotBot", body.GetProperty("displayName").GetString());

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.Equal(host.Clock.UtcNow, settings.ConnectionCheckedAt);
    }

    [Fact]
    public async Task AProxyIsStoredWithItsPasswordEncrypted()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/connection-test",
            new
            {
                useProxy = true,
                proxyUrl = "http://proxy.example.com:11202",
                proxyUsername = "modbot",
                proxyPassword = "the-proxy-password",
            },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            "the-proxy-password", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.Equal("http://proxy.example.com:11202/", settings.ProxyUrl);
        Assert.Equal("modbot", settings.ProxyUsername);

        var protector = host.Services.GetRequiredService<ISecretProtector>();
        Assert.Equal("the-proxy-password", protector.Unprotect(settings.ProxyPasswordEncrypted));
    }

    [Fact]
    public async Task RetestingWithoutResendingThePasswordKeepsTheStoredOne()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await host.PostAsync(
            "/api/onboarding/connection-test",
            new { useProxy = true, proxyUrl = "http://proxy.example.com:11202", proxyUsername = "modbot", proxyPassword = "the-proxy-password" },
            cookie,
            Ct);

        // Fixing a typo in the URL and testing again. The browser cannot read the stored password
        // back, so if omitting it wiped it, the retry would fail for a new reason and the
        // operator would have no idea why.
        await host.PostAsync(
            "/api/onboarding/connection-test",
            new { useProxy = true, proxyUrl = "http://proxy.example.com:11203", proxyUsername = "modbot" },
            cookie,
            Ct);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        var protector = host.Services.GetRequiredService<ISecretProtector>();

        Assert.Equal("http://proxy.example.com:11203/", settings.ProxyUrl);
        Assert.Equal("the-proxy-password", protector.Unprotect(settings.ProxyPasswordEncrypted));
    }

    [Fact]
    public async Task TestingWithoutTheProxyClearsIt()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await host.PostAsync(
            "/api/onboarding/connection-test",
            new { useProxy = true, proxyUrl = "http://proxy.example.com:11202", proxyPassword = "p" },
            cookie,
            Ct);

        // Spec 7.1.1's own advice for a timeout is "test once without the proxy to find out which
        // side is unresponsive", so that has to be a thing the operator can actually do.
        await host.PostAsync("/api/onboarding/connection-test", new { useProxy = false }, cookie, Ct);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.Null(settings.ProxyUrl);
        Assert.Null(settings.ProxyPasswordEncrypted);
    }

    [Fact]
    public async Task AnUnusableProxyUrlIsRefusedBeforeAnythingIsStored()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/connection-test",
            new { useProxy = true, proxyUrl = "proxy.example.com" },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.Null(settings.ProxyUrl);
    }

    [Fact]
    public async Task TheCheckCanBeReRunInPlaceUntilItPasses()
    {
        var gate = Failing(VRChatFailureKind.WafBlocked, 403, 1020);
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var blocked = await (await host.PostAsync(
            "/api/onboarding/connection-test", new { }, cookie, Ct)).ReadJsonAsync(Ct);
        Assert.True(blocked.GetProperty("proxyWouldHelp").GetBoolean());

        // The operator adds the proxy and tests again -- without leaving the step, which is what
        // "re-tested in place until the check passes" means.
        gate.SignedInAs("ModbotBot");

        var passed = await (await host.PostAsync(
            "/api/onboarding/connection-test",
            new { useProxy = true, proxyUrl = "http://proxy.example.com:11202" },
            cookie,
            Ct)).ReadJsonAsync(Ct);

        Assert.Equal("Ok", passed.GetProperty("outcome").GetString());

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.NotNull(settings.ConnectionCheckedAt);
        Assert.Equal("http://proxy.example.com:11202/", settings.ProxyUrl);
    }

    /// <summary>
    /// The stored session is kept: the gate checks it through the proxy just saved, which proves the
    /// proxy as well as a sign-in would and costs none of the few VRChat allows an hour (spec 4.1.2).
    /// </summary>
    [Fact]
    public async Task TheStoredSessionIsKeptAndCheckedThroughTheProxy()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await using (var context = _db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.VRChatAuthCookieEncrypted = "a-session-from-before-the-proxy";
            await context.SaveChangesAsync(Ct);
        }

        await host.PostAsync(
            "/api/onboarding/connection-test",
            new { useProxy = true, proxyUrl = "http://proxy.example.com:11202" },
            cookie,
            Ct);

        var after = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.Equal("a-session-from-before-the-proxy", after.VRChatAuthCookieEncrypted);
        Assert.Equal(1, gate.SignInCalls);
    }
}
