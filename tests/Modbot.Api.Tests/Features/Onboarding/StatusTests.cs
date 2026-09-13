using Modbot.Api.Tests.Fakes;
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
}
