using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Modbot.Api.Tests.Fakes;
using Modbot.Core.Security;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>
/// Spec 7.1 step 5 and the finish line: the optional step is genuinely optional.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class OptionalStepTests
{
    private readonly PostgresFixture _db;

    public OptionalStepTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(ApiTestHost Host, string Cookie)> ReadyToFinishAsync(
        PostgresFixture db, CancellationToken ct)
    {
        var gate = new FakeVRChatGate().SignedInAs("ModbotBot");
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(db, gate, ct);

        await host.PostAsync(
            "/api/onboarding/vrchat",
            new { username = "modbot@example.com", password = "a-vrchat-password" },
            cookie,
            ct);

        await host.PostAsync(
            "/api/onboarding/group", new { groupId = "grp_kings", name = "VRC Kings" }, cookie, ct);

        return (host, cookie);
    }

    [Fact]
    public async Task SetupFinishesWithNeitherDiscordNorSmtp()
    {
        var (host, cookie) = await ReadyToFinishAsync(_db, Ct);
        await using var _host = host;

        var response = await host.PostAsync("/api/onboarding/complete", null, cookie, Ct);

        // A deployment with no Discord bot and no SMTP is a complete, working Modbot. If
        // finishing required either, "skippable" would be a lie the wizard tells.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.True(settings.OnboardingComplete);
    }

    [Fact]
    public async Task FinishingIsRefusedWhileTheVRChatAccountIsUnverified()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        await host.PostAsync("/api/onboarding/group", new { groupId = "grp_kings" }, cookie, Ct);

        var response = await host.PostAsync("/api/onboarding/complete", null, cookie, Ct);

        // Marking a deployment "set up" when it cannot log in produces a Modbot that looks broken
        // with no explanation of what is missing.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task FinishingIsRefusedWithoutAGroup()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await host.PostAsync(
            "/api/onboarding/vrchat",
            new { username = "modbot@example.com", password = "a-vrchat-password" },
            cookie,
            Ct);

        var response = await host.PostAsync("/api/onboarding/complete", null, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TheDiscordTokenIsEncryptedAndNeverReturned()
    {
        var (host, cookie) = await ReadyToFinishAsync(_db, Ct);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/integrations",
            new { discord = new { botToken = "a-discord-bot-token", guildId = "123456789" } },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(
            "a-discord-bot-token", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        var protector = host.Services.GetRequiredService<ISecretProtector>();

        Assert.Equal("a-discord-bot-token", protector.Unprotect(settings.DiscordBotTokenEncrypted));

        // An opaque snowflake, stored as given -- the same rule as VRChat ids (spec 3.1.1).
        Assert.Equal("123456789", settings.DiscordGuildId);
    }

    [Fact]
    public async Task OmittingAFieldLeavesTheStoredValueAlone()
    {
        var (host, cookie) = await ReadyToFinishAsync(_db, Ct);
        await using var _host = host;

        await host.PostAsync(
            "/api/onboarding/integrations",
            new { discord = new { botToken = "a-discord-bot-token", guildId = "123456789" } },
            cookie,
            Ct);

        // Re-running the step to change only the guild. The browser cannot read the token back,
        // so if omitting it cleared it, editing one field would silently switch the bot off.
        await host.PostAsync(
            "/api/onboarding/integrations",
            new { discord = new { guildId = "987654321" } },
            cookie,
            Ct);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        var protector = host.Services.GetRequiredService<ISecretProtector>();

        Assert.Equal("a-discord-bot-token", protector.Unprotect(settings.DiscordBotTokenEncrypted));
        Assert.Equal("987654321", settings.DiscordGuildId);
    }

    [Fact]
    public async Task AnEmptyValueClearsIt()
    {
        var (host, cookie) = await ReadyToFinishAsync(_db, Ct);
        await using var _host = host;

        await host.PostAsync(
            "/api/onboarding/integrations",
            new { discord = new { botToken = "a-discord-bot-token" } },
            cookie,
            Ct);

        // Omitted means "leave it", empty means "clear it". Without the second there is no way to
        // turn the bot off again once it has been turned on.
        await host.PostAsync(
            "/api/onboarding/integrations",
            new { discord = new { botToken = "" } },
            cookie,
            Ct);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.Null(settings.DiscordBotTokenEncrypted);
    }

    [Fact]
    public async Task AnSmtpHostWithNoPortGetsTheSubmissionPort()
    {
        var (host, cookie) = await ReadyToFinishAsync(_db, Ct);
        await using var _host = host;

        await host.PostAsync(
            "/api/onboarding/integrations",
            new { smtp = new { host = "smtp.example.com", fromAddress = "modbot@example.com" } },
            cookie,
            Ct);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);

        // A host with no port is the commonest way to end up with mail that never sends, and 587
        // is what almost every relay wants.
        Assert.Equal(587, settings.SmtpPort);
        Assert.True(settings.SmtpUseTls);
    }

    [Fact]
    public async Task AnImpossibleSmtpPortIsRefused()
    {
        var (host, cookie) = await ReadyToFinishAsync(_db, Ct);
        await using var _host = host;

        var response = await host.PostAsync(
            "/api/onboarding/integrations",
            new { smtp = new { host = "smtp.example.com", port = 70000 } },
            cookie,
            Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
