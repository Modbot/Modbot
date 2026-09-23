using System.Net;
using Modbot.Api.Tests.Fakes;
using Modbot.TestSupport;
using Modbot.VRChat;
using VRChat.API.Model;

namespace Modbot.Api.Tests.Features.Onboarding;

/// <summary>Spec 7.1 step 4.</summary>
[Collection(nameof(PostgresCollection))]
public class SelectGroupTests
{
    private readonly PostgresFixture _db;

    public SelectGroupTests(PostgresFixture db) => _db = db;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static LimitedUserGroups Group(string id, string name, int members) =>
        new() { GroupId = id, Name = name, MemberCount = members, ShortCode = name[..3].ToUpperInvariant() };

    private static FakeVRChatGate GateWith(
        List<LimitedUserGroups> groups, Dictionary<string, List<GroupPermissions>> permissions)
    {
        var gate = new FakeVRChatGate().SignedInAs("ModbotBot", "usr_bot");
        gate.Returns("GetUserGroups", groups);
        gate.Returns("GetUserAllGroupPermissions", permissions);
        return gate;
    }

    [Fact]
    public async Task OnlyGroupsWhereTheAccountCanModerateAreOffered()
    {
        var gate = GateWith(
            [
                Group("grp_kings", "VRC Kings", 14208),
                Group("grp_cat", "The Black Cat", 3104),
                Group("grp_lurker", "Somewhere I Just Joined", 42),
            ],
            new()
            {
                ["grp_kings"] = [GroupPermissions.group_members_viewall, GroupPermissions.group_bans_manage],
                ["grp_cat"] = [GroupPermissions.group_audit_view],
                ["grp_lurker"] = [GroupPermissions.group_instance_join],
            });

        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var body = await (await host.GetAsync("/api/onboarding/groups", cookie, Ct)).ReadJsonAsync(Ct);

        var names = body.GetProperty("groups").EnumerateArray()
            .Select(g => g.GetProperty("name").GetString())
            .ToList();

        Assert.Equal(["VRC Kings", "The Black Cat"], names);

        // The total is reported too, so "one of your three groups is missing" can be explained
        // rather than silently happening.
        Assert.Equal(3, body.GetProperty("totalGroups").GetInt32());
    }

    [Fact]
    public async Task NoQualifyingGroupsIsExplainedRatherThanShownAsAnEmptyList()
    {
        var gate = GateWith(
            [Group("grp_lurker", "Somewhere I Just Joined", 42)],
            new() { ["grp_lurker"] = [GroupPermissions.group_instance_join] });

        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var body = await (await host.GetAsync("/api/onboarding/groups", cookie, Ct)).ReadJsonAsync(Ct);

        Assert.Empty(body.GetProperty("groups").EnumerateArray());
        Assert.Equal(1, body.GetProperty("totalGroups").GetInt32());

        // Spec 7.1 step 4: "If none qualify, say so explicitly and explain the required
        // permissions." An empty list on its own leaves the operator with nothing to act on.
        var required = body.GetProperty("requiredPermissions").EnumerateArray()
            .Select(p => p.GetString())
            .ToList();

        Assert.Contains("group-members-viewall", required);
        Assert.Contains("group-audit-view", required);
    }

    [Fact]
    public async Task TheWildcardPermissionQualifiesWithNothingMissing()
    {
        var gate = GateWith(
            [Group("grp_kings", "VRC Kings", 14208)],
            new() { ["grp_kings"] = [GroupPermissions.group_all] });

        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var body = await (await host.GetAsync("/api/onboarding/groups", cookie, Ct)).ReadJsonAsync(Ct);
        var group = body.GetProperty("groups").EnumerateArray().Single();

        Assert.Empty(group.GetProperty("missingPermissions").EnumerateArray());
    }

    [Fact]
    public async Task AQualifyingGroupWithGapsNamesThem()
    {
        var gate = GateWith(
            [Group("grp_cat", "The Black Cat", 3104)],
            new() { ["grp_cat"] = [GroupPermissions.group_audit_view] });

        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var body = await (await host.GetAsync("/api/onboarding/groups", cookie, Ct)).ReadJsonAsync(Ct);
        var group = body.GetProperty("groups").EnumerateArray().Single();

        var missing = group.GetProperty("missingPermissions").EnumerateArray()
            .Select(p => p.GetString())
            .ToList();

        // Offered, because reading the audit log alone is a real and reasonable delegation -- but
        // with the gaps named, so nobody discovers after setup that bans will never sync.
        Assert.Contains("group-members-viewall", missing);
        Assert.Contains("group-bans-manage", missing);
        Assert.DoesNotContain("group-audit-view", missing);
    }

    [Fact]
    public async Task TheWholeStepCostsTwoVRChatRequests()
    {
        var gate = GateWith(
            [
                Group("grp_a", "Group A", 10), Group("grp_b", "Group B", 20),
                Group("grp_c", "Group C", 30), Group("grp_d", "Group D", 40),
            ],
            new()
            {
                ["grp_a"] = [GroupPermissions.group_all],
                ["grp_b"] = [GroupPermissions.group_all],
                ["grp_c"] = [GroupPermissions.group_all],
                ["grp_d"] = [GroupPermissions.group_all],
            });

        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await host.GetAsync("/api/onboarding/groups", cookie, Ct);

        // One request per group would be the obvious implementation and, at the groups.read
        // pacing of one request per five seconds, would turn this step into minutes of spinner
        // for an account in twenty groups.
        Assert.Equal(2, gate.Calls.Count);

        // Their own class, not users.read. Neither endpoint has a measured limit (spec 4.3.4.1),
        // and users.read is the most permissive class there is -- it is exempt from the global
        // ceiling on evidence these two have none of. Isolated, a 429 here stops group selection
        // and nothing else.
        Assert.All(gate.Calls, call => Assert.Equal(VRChatEndpointClass.UsersGroups, call.Endpoint.Class));
    }

    [Fact]
    public async Task ListingRunsAtInteractivePriority()
    {
        var gate = GateWith(
            [Group("grp_kings", "VRC Kings", 1)],
            new() { ["grp_kings"] = [GroupPermissions.group_all] });

        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        await host.GetAsync("/api/onboarding/groups", cookie, Ct);

        // A human is watching a spinner in a setup wizard, so this preempts background sync
        // rather than queueing behind it (spec 4.3.3). Background would mean the step blocks
        // behind whatever sync happened to be mid-flight.
        Assert.NotEmpty(gate.Calls);
        Assert.All(gate.Calls, call => Assert.Equal(VRChatCallPriority.Interactive, call.Priority));
    }

    [Fact]
    public async Task ALegacyGroupIdIsStoredExactlyAsGiven()
    {
        var gate = new FakeVRChatGate().SignedInAs();
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        // Spec 3.1.1: legacy ids follow no structured format at all. A shape check here would
        // pass every test written against modern ids and then silently exclude exactly the oldest
        // and most established communities in VRChat.
        var response = await host.PostAsync(
            "/api/onboarding/group", new { groupId = "8JoV9XEdpo", name = "An Old Group" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var settings = await OnboardingTestContext.ReadSettingsAsync(_db, Ct);
        Assert.Equal("8JoV9XEdpo", settings.ManagedGroupId);
        Assert.Equal("An Old Group", settings.ManagedGroupName);
    }

    [Fact]
    public async Task ChoosingNoGroupIsRefused()
    {
        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, null, Ct);
        await using var _host = host;

        var response = await host.PostAsync("/api/onboarding/group", new { groupId = "" }, cookie, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUnreachableApiIsDiagnosedRatherThanReturnedAsNoGroups()
    {
        var gate = new FakeVRChatGate
        {
            SignIn = VRChatResult<CurrentUserLoginResponse>.Failure(
                403, "Cloudflare blocked this request.", wafCode: 1020,
                kind: VRChatFailureKind.WafBlocked),
        };

        var (host, cookie) = await OnboardingTestContext.SetUpAsync(_db, gate, Ct);
        await using var _host = host;

        var response = await host.GetAsync("/api/onboarding/groups", cookie, Ct);

        // "You have no groups" and "Cloudflare is blocking this host" must not look the same, or
        // the operator goes to check their group roles for a network problem.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.ReadJsonAsync(Ct);
        Assert.Equal("WafBlocked", body.GetProperty("outcome").GetString());
        Assert.True(body.GetProperty("proxyWouldHelp").GetBoolean());
    }
}
