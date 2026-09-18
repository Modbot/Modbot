using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// VRChat proxy design: forwarded requests have a class and a lane of their own, apart from sync
/// and from what a moderator presses; the service account's go through the global backstop and
/// a caller's own do not; and a 429 on either stops only the proxy.
/// </summary>
public class ProxyBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Proxy = new(VRChatEndpointClass.Proxy, null, "GET /api/1/users/usr_test");
    private static readonly VRChatEndpoint Passthrough = new(VRChatEndpointClass.ProxyPassthrough, null, "GET /api/1/auth/user");
    private static readonly VRChatEndpoint Members = new(VRChatEndpointClass.GroupsMembers, "grp_test");
    private static readonly VRChatEndpoint Moderate = new(VRChatEndpointClass.GroupsModerate, "grp_test");

    [Fact]
    public void TheProxyHasItsOwnClassAndLane_OnTheGlobalBackstop_AtAConservativeRate()
    {
        var proxy = VRChatRateLimits.Defaults[VRChatEndpointClass.Proxy];
        var passthrough = VRChatRateLimits.Defaults[VRChatEndpointClass.ProxyPassthrough];

        // The bottom of spec 4.3.4's provisional range: the limiter cannot see which VRChat
        // endpoint a forwarded request reaches, so it gets the least any class gets.
        Assert.Equal(0.3, proxy.HardMaxPerSecond, 9);
        Assert.Equal(0.5, passthrough.HardMaxPerSecond, 9);

        // Apart from the sweeps and from what a moderator presses, and from each other.
        var lanes = new[] { proxy.Lane, passthrough.Lane };
        Assert.Equal(2, lanes.Distinct().Count());
        Assert.DoesNotContain(VRChatRateLimits.GroupLane, lanes);
        Assert.DoesNotContain(VRChatRateLimits.GroupsModerateLane, lanes);

        // As the service account: counted against its backstop, and evidence about it.
        Assert.Equal(VRChatEndpointClass.Global, proxy.Backstop);
        Assert.True(proxy.ServiceAccount);

        // As somebody else: neither.
        Assert.Null(passthrough.Backstop);
        Assert.False(passthrough.ServiceAccount);

        Assert.DoesNotContain(VRChatEndpointClass.Proxy, VRChatRateLimits.Scheduled);
        Assert.DoesNotContain(VRChatEndpointClass.ProxyPassthrough, VRChatRateLimits.Scheduled);
    }

    [Fact]
    public async Task ProxiedRequestsGoOutAtMostOncePerThreeSeconds()
    {
        var harness = new LimiterHarness();
        var start = harness.Clock.UtcNow;

        for (var i = 0; i < 4; i++)
            await harness.CallAsync(Proxy, ct: Ct);

        Assert.True(
            harness.Clock.UtcNow - start >= TimeSpan.FromSeconds(10) - TimeSpan.FromMilliseconds(100),
            $"four proxied requests took {harness.Clock.UtcNow - start}, faster than 0.3 a second");
    }

    [Fact]
    public async Task A429OnTheProxyStopsTheProxyAndNothingElse_AndHalvesGlobal()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Proxy, status: 429, ct: Ct);

        var refused = await harness.CallAsync(Proxy, ct: Ct);
        Assert.False(refused.IsAcquired);
        Assert.Equal(RateLimitDenialReason.ColdStop, refused.Denial?.Reason);

        // The sweeps and the moderator carry on.
        Assert.True((await harness.CallAsync(Members, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(Moderate, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(Passthrough, ct: Ct)).IsAcquired);

        // The service account was rate limited, and the backstop learns that (spec 4.3.1).
        Assert.Equal(0.5, (await harness.HealthAsync(VRChatEndpointClass.Global)).BudgetMultiplier, 9);
    }

    [Fact]
    public async Task A429OnACallersOwnCookieTouchesNothingOfTheServiceAccounts()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Members, ct: Ct);
        await harness.CallAsync(Passthrough, status: 429, ct: Ct);

        Assert.False((await harness.CallAsync(Passthrough, ct: Ct)).IsAcquired);

        // A stranger's rate limit is not evidence about Modbot's account: global keeps its
        // budget, and the service account's own proxy class is untouched.
        Assert.Equal(1.0, (await harness.HealthAsync(VRChatEndpointClass.Global)).BudgetMultiplier, 9);
        Assert.True((await harness.CallAsync(Proxy, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(Members, ct: Ct)).IsAcquired);
    }

    [Fact]
    public async Task AColdMemberSweepDoesNotStopTheProxy()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Members, status: 429, ct: Ct);

        Assert.False((await harness.CallAsync(Members, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(Proxy, ct: Ct)).IsAcquired);
    }
}
