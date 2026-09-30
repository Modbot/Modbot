using Modbot.Api.Tests.Fakes;
using Modbot.Core.Data;
using Modbot.Core.Data.Entities;
using Modbot.TestSupport;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>
/// Where the wizard resumes. Spec 7.1's steps are independently re-runnable, so this is a "first
/// thing not done yet" question rather than a step counter.
/// </summary>
[Collection(nameof(PostgresCollection))]
public class StatusTests
{
    private readonly PostgresFixture _db;

    public StatusTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> NextStepAsync(ApiTestHost host, string? cookie)
    {
        var body = await (await host.GetAsync("/api/onboarding/status", cookie, Ct)).ReadJsonAsync(Ct);
        return body.GetProperty("nextStep").GetString()!;
    }

    [Fact]
    public async Task AFreshDeploymentStartsAtTheAdministratorStep()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);

        var body = await (await host.GetAsync("/api/onboarding/status", null, Ct)).ReadJsonAsync(Ct);

        Assert.False(body.GetProperty("hasAdministrator").GetBoolean());
        Assert.Equal("Administrator", body.GetProperty("nextStep").GetString());
    }

    [Fact]
    public async Task EachCompletedStepAdvancesToTheNext()
    {
        var gate = new FakeVRChatGate().SignedInAs("ModbotBot");
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        Assert.Equal("VRChat", await NextStepAsync(host, cookie));

        await host.PostAsync(
            "/api/onboarding/vrchat",
            new { username = "modbot@example.com", password = "a-vrchat-password" },
            cookie,
            Ct);

        // Not Connection: a successful login *is* a completed round trip to the VRChat API, so an
        // operator whose egress plainly works is not made to prove it twice. The step stays
        // available and re-runnable, which is what it is for.
        Assert.Equal("Group", await NextStepAsync(host, cookie));

        await host.PostAsync(
            "/api/onboarding/group", new { groupId = "grp_kings", name = "VRC Kings" }, cookie, Ct);

        Assert.Equal("Optional", await NextStepAsync(host, cookie));

        await host.PostAsync("/api/onboarding/complete", null, cookie, Ct);

        Assert.Equal("Done", await NextStepAsync(host, cookie));
    }

    [Fact]
    public async Task AnUnlinkedAdministrator_IsSentToTheLinkStepAfterVRChat()
    {
        var gate = new FakeVRChatGate().SignedInAs("ModbotBot");
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct, linked: false);
        await using var _host = host;

        await host.PostAsync(
            "/api/onboarding/vrchat",
            new { username = "modbot@example.com", password = "a-vrchat-password" },
            cookie,
            Ct);

        // After the gate works and before the group: the link is proved through the gate, so it
        // cannot come earlier, and every step after it requires it (design §4.3).
        Assert.Equal("LinkVRChat", await NextStepAsync(host, cookie));

        var groups = await host.GetAsync("/api/onboarding/groups", cookie, Ct);
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, groups.StatusCode);
    }

    [Fact]
    public async Task AFailedVRChatStepDoesNotAdvance()
    {
        var gate = new FakeVRChatGate();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await host.PostAsync(
            "/api/onboarding/vrchat",
            new { username = "modbot@example.com", password = "a-vrchat-password" },
            cookie,
            Ct);

        // Credentials are stored so the proxy step can retry with them, but "stored" is not
        // "works", and the wizard must not treat it as though it were.
        Assert.Equal("VRChat", await NextStepAsync(host, cookie));
    }

    [Fact]
    public async Task TheConfiguredGroupAndAccountAreReportedBack()
    {
        var gate = new FakeVRChatGate().SignedInAs("ModbotBot");
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await host.PostAsync(
            "/api/onboarding/vrchat",
            new { username = "modbot@example.com", password = "a-vrchat-password" },
            cookie,
            Ct);

        await host.PostAsync(
            "/api/onboarding/group", new { groupId = "grp_kings", name = "VRC Kings" }, cookie, Ct);

        var body = await (await host.GetAsync("/api/onboarding/status", cookie, Ct)).ReadJsonAsync(Ct);

        // Which account Modbot acts as is worth being able to read at a glance: an operator who
        // mistyped a shared account's email finds out by reading a name, not by watching a sync
        // produce another group's data.
        Assert.Equal("ModbotBot", body.GetProperty("vrChat").GetProperty("displayName").GetString());
        Assert.Equal("modbot@example.com", body.GetProperty("vrChat").GetProperty("username").GetString());
        Assert.Equal("VRC Kings", body.GetProperty("group").GetProperty("name").GetString());
    }

    // ── Who is told the account details (the short answer and the full one).

    private const string ProxyHost = "proxy.example.net";
    private const string ProxyUser = "proxy-user-x";
    private const string MailHost = "smtp.example.net";
    private const string DiscordServer = "111222333444555666";
    private const string DiscordChannel = "777888999000111222";
    private const string AnnouncementText = "Come and join the night shift";
    private const string PublicAddress = "https://modbot.example.net";
    private const string VRChatLogin = "modbot@example.com";

    /// <summary>
    /// A set-up deployment with something saved in every part the answer can carry, so a test that
    /// says a detail is missing is saying it about a detail that exists.
    /// </summary>
    private async Task<(ApiTestHost Host, string AdminCookie)> ConfiguredAsync()
    {
        var gate = new FakeVRChatGate().SignedInAs("ModbotBot");
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);

        await host.PostAsync(
            "/api/onboarding/vrchat",
            new { username = VRChatLogin, password = "a-vrchat-password" },
            cookie,
            Ct);

        await host.PostAsync(
            "/api/onboarding/group", new { groupId = "grp_kings", name = "VRC Kings" }, cookie, Ct);

        await SaveDetailsAsync();

        return (host, cookie);
    }

    private async Task SaveDetailsAsync()
    {
        await using var context = _db.NewContext();
        var settings = await context.GetSettingsAsync(Ct);

        settings.ProxyUrl = $"http://{ProxyHost}:8080";
        settings.ProxyUsername = ProxyUser;
        settings.SmtpHost = MailHost;
        settings.DiscordGuildId = DiscordServer;
        settings.DiscordInstanceChannelId = DiscordChannel;
        settings.DiscordInstanceMessage = AnnouncementText;
        settings.PublicAddress = PublicAddress;

        await context.SaveChangesAsync(Ct);
    }

    private static readonly string[] Details =
    [
        VRChatLogin, "ModbotBot", ProxyHost, ProxyUser, MailHost, DiscordServer, DiscordChannel,
        AnnouncementText, PublicAddress,
    ];

    private static async Task<(string Raw, System.Text.Json.JsonElement Body)> ReadStatusAsync(
        ApiTestHost host, string? cookie)
    {
        var response = await host.GetAsync("/api/onboarding/status", cookie, Ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync(Ct);
        return (raw, System.Text.Json.JsonDocument.Parse(raw).RootElement.Clone());
    }

    private static void AssertShortAnswer(string raw, System.Text.Json.JsonElement body)
    {
        foreach (var detail in Details)
            Assert.DoesNotContain(detail, raw, StringComparison.Ordinal);

        // What the sign-in page and the sidebar still need.
        Assert.True(body.GetProperty("hasAdministrator").GetBoolean());
        Assert.Equal("VRC Kings", body.GetProperty("group").GetProperty("name").GetString());
        Assert.Equal("grp_kings", body.GetProperty("group").GetProperty("id").GetString());
        Assert.True(body.TryGetProperty("nextStep", out _));
        Assert.True(body.TryGetProperty("onboardingComplete", out _));

        // The parts are still there, only empty.
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("vrChat").GetProperty("username").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("vrChat").GetProperty("displayName").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("vrChat").GetProperty("lastSignedInAt").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("connection").GetProperty("proxyUrl").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("connection").GetProperty("proxyUsername").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("integrations").GetProperty("smtpHost").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("integrations").GetProperty("discordGuildId").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, body.GetProperty("integrations").GetProperty("publicAddress").ValueKind);
    }

    private static void AssertFullAnswer(System.Text.Json.JsonElement body)
    {
        Assert.Equal(VRChatLogin, body.GetProperty("vrChat").GetProperty("username").GetString());
        Assert.Equal("ModbotBot", body.GetProperty("vrChat").GetProperty("displayName").GetString());
        Assert.Equal($"http://{ProxyHost}:8080", body.GetProperty("connection").GetProperty("proxyUrl").GetString());
        Assert.Equal(ProxyUser, body.GetProperty("connection").GetProperty("proxyUsername").GetString());
        Assert.Equal(MailHost, body.GetProperty("integrations").GetProperty("smtpHost").GetString());
        Assert.Equal(DiscordServer, body.GetProperty("integrations").GetProperty("discordGuildId").GetString());
        Assert.Equal(DiscordChannel, body.GetProperty("integrations").GetProperty("discordInstanceChannelId").GetString());
        Assert.Equal(AnnouncementText, body.GetProperty("integrations").GetProperty("discordInstanceMessage").GetString());
        Assert.Equal(PublicAddress, body.GetProperty("integrations").GetProperty("publicAddress").GetString());
        Assert.Equal("VRC Kings", body.GetProperty("group").GetProperty("name").GetString());
    }

    [Fact]
    public async Task AfterSetup_SomebodyNotSignedIn_GetsNoAccountDetails()
    {
        var (host, _) = await ConfiguredAsync();
        await using var _host = host;

        var (raw, body) = await ReadStatusAsync(host, cookie: null);

        AssertShortAnswer(raw, body);
        Assert.False(body.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task ASignedInAccountWithoutManageSettings_GetsNoAccountDetails()
    {
        var (host, _) = await ConfiguredAsync();
        await using var _host = host;

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ViewMembers, Ct);
        var (raw, body) = await ReadStatusAsync(host, cookie);

        AssertShortAnswer(raw, body);
        Assert.True(body.GetProperty("authenticated").GetBoolean());
    }

    [Fact]
    public async Task ASignedInAccountWithManageSettings_GetsEveryDetail()
    {
        var (host, _) = await ConfiguredAsync();
        await using var _host = host;

        var (_, cookie) = await host.SignedInAsync(ModbotPermissions.ManageSettings, Ct);
        var (_, body) = await ReadStatusAsync(host, cookie);

        AssertFullAnswer(body);
    }

    [Fact]
    public async Task AnAdministrator_GetsEveryDetail()
    {
        var (host, adminCookie) = await ConfiguredAsync();
        await using var _host = host;

        var (_, body) = await ReadStatusAsync(host, adminCookie);

        AssertFullAnswer(body);
    }

    [Fact]
    public async Task TheShortAnswerStillSaysWhereSetupIsUpTo()
    {
        var (host, adminCookie) = await ConfiguredAsync();
        await using var _host = host;

        var (_, full) = await ReadStatusAsync(host, adminCookie);
        var (_, anonymous) = await ReadStatusAsync(host, cookie: null);

        // The step is worked out from the real values, then the values are left out.
        Assert.Equal(full.GetProperty("nextStep").GetString(), anonymous.GetProperty("nextStep").GetString());
        Assert.Equal(
            full.GetProperty("onboardingComplete").GetBoolean(),
            anonymous.GetProperty("onboardingComplete").GetBoolean());
        Assert.Equal(full.GetProperty("myModbotUrl").GetString(), anonymous.GetProperty("myModbotUrl").GetString());
    }

    [Fact]
    public async Task BeforeTheFirstAccount_AnyoneStillGetsTheFullAnswer()
    {
        await using var host = await OnboardingTestContext.FreshAsync(_db, null, Ct);
        await SaveDetailsAsync();

        var (_, body) = await ReadStatusAsync(host, cookie: null);

        // The wizard runs with nobody signed in, and the deployment's own values (an address the
        // platform suggested, a proxy from the environment) are what its steps pre-fill.
        Assert.False(body.GetProperty("hasAdministrator").GetBoolean());
        Assert.Equal($"http://{ProxyHost}:8080", body.GetProperty("connection").GetProperty("proxyUrl").GetString());
        Assert.Equal(MailHost, body.GetProperty("integrations").GetProperty("smtpHost").GetString());
        Assert.Equal(PublicAddress, body.GetProperty("integrations").GetProperty("publicAddress").GetString());
    }
}
