using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// M4 §4: kicks, bans and unbans share one unmeasured budget of one request per two seconds, on a
/// lane of their own, and a 429 there stops only them.
/// </summary>
/// <remarks>
/// The number is a guess the maintainer set deliberately low, not a finding — nobody has asked
/// VRChat what these three allow (spec 4.3.4). It is pinned here so that changing it is a decision
/// somebody makes on purpose rather than a line that drifts.
/// </remarks>
public class ModerationBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Moderate = new(VRChatEndpointClass.GroupsModerate, "grp_test");
    private static readonly VRChatEndpoint Members = new(VRChatEndpointClass.GroupsMembers, "grp_test");
    private static readonly VRChatEndpoint Bans = new(VRChatEndpointClass.GroupsBans, "grp_test");

    [Fact]
    public void TheBudgetIsOnePerTwoSeconds_OnItsOwnLane_CountedAgainstTheGlobalCeiling()
    {
        var moderate = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsModerate];

        Assert.Equal(0.5, moderate.HardMaxPerSecond, 9);

        // The effective rate is the ceiling times the fraction, and the defaults are set so that
        // out of the box it is exactly the pacing above.
        Assert.Equal(
            0.5,
            Math.Min(moderate.HardMaxPerSecond, moderate.DefaultCeilingPerSecond * RateLimitOptions.DefaultFraction),
            9);

        // Its own lane, so a moderator's ban never queues behind a member sweep.
        Assert.Equal(VRChatRateLimits.GroupsModerateLane, moderate.Lane);
        Assert.NotEqual(VRChatRateLimits.GroupLane, moderate.Lane);

        // Unmeasured and brand new: no exemption from the backstop, one token, scoped to the group.
        Assert.True(moderate.CountsAgainstGlobal);
        Assert.True(moderate.ResourceScoped);
        Assert.Equal(1, moderate.BurstTokens);

        // Not part of what the settings screen sums as scheduled background sync: a moderator
        // drives these, so they are what the room left over is for.
        Assert.DoesNotContain(VRChatEndpointClass.GroupsModerate, VRChatRateLimits.Scheduled);
    }

    [Fact]
    public async Task ModerationActionsGoOutAtMostOncePerTwoSeconds()
    {
        var harness = new LimiterHarness();
        var start = harness.Clock.UtcNow;

        for (var i = 0; i < 4; i++)
            await harness.CallAsync(Moderate, ct: Ct);

        // The first spends the burst token; the other three each wait out two seconds.
        Assert.True(
            harness.Clock.UtcNow - start >= TimeSpan.FromSeconds(6) - TimeSpan.FromMilliseconds(100),
            $"four actions took {harness.Clock.UtcNow - start}, faster than one per two seconds");
    }

    [Fact]
    public async Task A429OnAModerationActionStopsModerationAndNothingElse()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Moderate, status: 429, ct: Ct);

        // Never retried: the next action is refused outright rather than queued behind a penalty
        // that trying again would extend (spec 4.3.1).
        var refused = await harness.CallAsync(Moderate, ct: Ct);
        Assert.False(refused.IsAcquired);
        Assert.Equal(RateLimitDenialReason.ColdStop, refused.Denial?.Reason);

        // The sweeps carry on. A cold moderation bucket must not stop Modbot reading the group.
        Assert.True((await harness.CallAsync(Members, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(Bans, ct: Ct)).IsAcquired);
    }

    [Fact]
    public async Task AColdMemberSweepDoesNotStopAModerationAction()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Members, status: 429, ct: Ct);

        Assert.False((await harness.CallAsync(Members, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(Moderate, ct: Ct)).IsAcquired);
    }
}
