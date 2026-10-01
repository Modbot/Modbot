using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// Calendar auto-invite design §4: inviting a person to an instance is one request every thirty
/// seconds, on a class and a lane of its own, and a 429 stops that class and nothing else.
/// </summary>
public class EventInviteBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Invite = new(VRChatEndpointClass.InvitesSend);
    private static readonly VRChatEndpoint GroupInvite = new(VRChatEndpointClass.GroupsInvites, "grp_test");
    private static readonly VRChatEndpoint Create = new(VRChatEndpointClass.InstancesCreate);

    [Fact]
    public void TheBudgetIsTheOneTheUserSet_OnItsOwnLane()
    {
        var invites = VRChatRateLimits.Defaults[VRChatEndpointClass.InvitesSend];

        Assert.Equal(1.0 / 30, invites.HardMaxPerSecond, 9);
        Assert.Equal(1, invites.BurstTokens);

        // Its own lane: not the group queue, and not group invites', which is another endpoint.
        Assert.Equal(VRChatRateLimits.InvitesSendLane, invites.Lane);
        Assert.NotEqual(VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsInvites].Lane, invites.Lane);
        Assert.NotEqual(VRChatRateLimits.GroupLane, invites.Lane);

        // A timer sends these: the global backstop, not the room kept for moderators.
        Assert.True(invites.CountsAgainstGlobal);
        Assert.True(invites.ServiceAccount);
    }

    [Fact]
    public async Task InvitesGoOutAtMostOnceEveryThirtySeconds()
    {
        var harness = new LimiterHarness();
        var start = harness.Clock.UtcNow;

        for (var i = 0; i < 3; i++)
            await harness.CallAsync(Invite, ct: Ct);

        // The first spends the token; the other two each wait out thirty seconds.
        Assert.True(harness.Clock.UtcNow - start >= TimeSpan.FromSeconds(60) - TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A429StopsEventInvitesAndNothingElse_AndIsNotTriedAgain()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Invite, status: 429, ct: Ct);

        var refused = await harness.CallAsync(Invite, ct: Ct);
        Assert.False(refused.IsAcquired);
        Assert.Equal(RateLimitDenialReason.ColdStop, refused.Denial?.Reason);

        Assert.True((await harness.CallAsync(GroupInvite, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(Create, ct: Ct)).IsAcquired);
    }
}
