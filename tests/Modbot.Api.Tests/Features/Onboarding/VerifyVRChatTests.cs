using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Security;
using Modbot.TestSupport;
using Modbot.VRChat;
using VRChat.API.Model;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>
/// Spec 7.1 step 2: validated live, so a wrong credential fails here and not at the first sync.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class VerifyVRChatTests
{
    private readonly PostgresFixture _db;

    public VerifyVRChatTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Credentials(string password = "the-vrchat-password") => new
    {
        username = "modbot@example.com",
        password,
        totpSecret = "JBSW Y3DP EHPK 3PXP",
    };

    [Fact]
    public async Task AcceptedCredentialsAreStoredEncryptedAndStamped()
    {
        var gate = new FakeVRChatGate().SignedInAs("ModbotBot", "usr_bot");
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var response = await host.PostAsync("/api/onboarding/vrchat", Credentials(), cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, gate.SignInCalls);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.Equal("modbot@example.com", settings.VRChatUsername);
        Assert.Equal("ModbotBot", settings.VRChatDisplayName);
        Assert.NotNull(settings.VRChatVerifiedAt);

        // Encrypted at rest (spec 8.3): the column must not be the plaintext, and it must still
        // decrypt to it. Testing only the first would pass for a column full of garbage.
        Assert.NotEqual("the-vrchat-password", settings.VRChatPasswordEncrypted);

        var protector = host.Services.GetRequiredService<ISecretProtector>();
        Assert.Equal("the-vrchat-password", protector.Unprotect(settings.VRChatPasswordEncrypted));
    }

    [Fact]
    public async Task TheTotpSecretIsStoredWithoutTheSpacesAuthenticatorAppsDisplay()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await host.PostAsync("/api/onboarding/vrchat", Credentials(), cookie, Ct);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        var protector = host.Services.GetRequiredService<ISecretProtector>();

        // Authenticator apps display the secret in groups of four and people paste it that way.
        // Base32 has no whitespace, so storing it verbatim produces codes VRChat rejects -- and
        // the symptom is "wrong 2FA secret", which is the one thing it is not.
        Assert.Equal("JBSWY3DPEHPK3PXP", protector.Unprotect(settings.VRChatTotpSecretEncrypted));
    }

    [Fact]
    public async Task RejectedCredentialsAnswer422AndDoNotStampVerified()
    {
        var gate = new FakeVRChatGate
        {
            SignIn = VRChatResult<CurrentUser>.Failure(
                401, "VRChat rejected the credentials.", kind: VRChatFailureKind.CredentialsRejected),
        };

        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var response = await host.PostAsync("/api/onboarding/vrchat", Credentials(), cookie, Ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.ReadJsonAsync(Ct);
        Assert.Equal("CredentialsRejected", body.GetProperty("outcome").GetString());

        // A proxy cannot fix a wrong password, and saying otherwise sends an operator to buy one.
        Assert.False(body.GetProperty("proxyWouldHelp").GetBoolean());

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.Null(settings.VRChatVerifiedAt);
    }

    [Fact]
    public async Task RejectedCredentialsAreStillStoredSoTheProxyStepCanRetry()
    {
        var gate = new FakeVRChatGate
        {
            SignIn = VRChatResult<CurrentUser>.Failure(
                403, "Cloudflare blocked this request.", wafCode: 1020,
                kind: VRChatFailureKind.WafBlocked),
        };

        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await host.PostAsync("/api/onboarding/vrchat", Credentials(), cookie, Ct);

        // The overwhelmingly common failure here is a WAF block, where the credentials are
        // perfectly good. Discarding them would make the operator retype a password and a TOTP
        // secret before they could try the proxy that is about to fix it.
        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.Equal("modbot@example.com", settings.VRChatUsername);
        Assert.NotNull(settings.VRChatPasswordEncrypted);
        Assert.Null(settings.VRChatVerifiedAt);
    }

    [Fact]
    public async Task AWafBlockSaysItIsNotTheOperatorsFaultAndOffersAProxy()
    {
        var gate = new FakeVRChatGate
        {
            SignIn = VRChatResult<CurrentUser>.Failure(
                403, "Cloudflare blocked this request.", wafCode: 1020,
                kind: VRChatFailureKind.WafBlocked),
        };

        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var response = await host.PostAsync("/api/onboarding/vrchat", Credentials(), cookie, Ct);
        var body = await response.ReadJsonAsync(Ct);

        Assert.Equal("WafBlocked", body.GetProperty("outcome").GetString());
        Assert.True(body.GetProperty("proxyWouldHelp").GetBoolean());
        Assert.Equal(1020, body.GetProperty("wafCode").GetInt32());

        var detail = body.GetProperty("detail").GetString();
        Assert.Contains("not a problem with your VRChat account", detail, StringComparison.Ordinal);
        Assert.Contains("VPS", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoringNewCredentialsDropsThePreviousAccountsSession()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await using (var context = _db.NewContext())
        {
            var settings = await context.GetSettingsAsync(Ct);
            settings.VRChatAuthCookieEncrypted = "a-previous-accounts-session";
            await context.SaveChangesAsync(Ct);
        }

        await host.PostAsync("/api/onboarding/vrchat", Credentials(), cookie, Ct);

        // A cookie belonging to whatever account was configured before is not merely stale: the
        // gate would present it, VRChat would accept it, and Modbot would report the previous
        // account as verified while holding the new one's password.
        var after = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.NotEqual("a-previous-accounts-session", after.VRChatAuthCookieEncrypted);
    }

    [Fact]
    public async Task AnEmptyUsernameIsRefusedWithoutReachingVRChat()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/vrchat", new { username = "  ", password = "hunter2" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, gate.SignInCalls);
    }

    [Fact]
    public async Task NoSecretIsEchoedBackOnFailure()
    {
        var gate = new FakeVRChatGate
        {
            SignIn = VRChatResult<CurrentUser>.Failure(
                401, "VRChat rejected the credentials.", kind: VRChatFailureKind.CredentialsRejected),
        };

        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var response = await host.PostAsync("/api/onboarding/vrchat", Credentials(), cookie, Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);

        // Error paths are where secrets leak, because that is where raw responses get attached.
        Assert.DoesNotContain("the-vrchat-password", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("JBSWY3DPEHPK3PXP", raw, StringComparison.Ordinal);
    }
}
