using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// Calendar design §5: calendar writes at one a minute, instance creation at one per five seconds,
/// each on a lane of its own, and a 429 on either stops only that bucket.
/// </summary>
public class CalendarBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Write = new(VRChatEndpointClass.CalendarWrite, "grp_test");
    private static readonly VRChatEndpoint Create = new(VRChatEndpointClass.InstancesCreate);
    private static readonly VRChatEndpoint Members = new(VRChatEndpointClass.GroupsMembers, "grp_test");

    [Fact]
    public void TheBudgetsAreTheOnesTheMaintainerSet()
    {
        var write = VRChatRateLimits.Defaults[VRChatEndpointClass.CalendarWrite];
        var read = VRChatRateLimits.Defaults[VRChatEndpointClass.CalendarRead];
        var create = VRChatRateLimits.Defaults[VRChatEndpointClass.InstancesCreate];

        Assert.Equal(1.0 / 60, write.HardMaxPerSecond, 9);
        Assert.Equal(1.0 / 10, read.HardMaxPerSecond, 9);
        Assert.Equal(1.0 / 5, create.HardMaxPerSecond, 9);

        // Each on a lane of its own, so a write waiting its minute holds nothing else up.
        var lanes = new[] { write.Lane, read.Lane, create.Lane };
        Assert.Equal(3, lanes.Distinct().Count());
        Assert.DoesNotContain(VRChatRateLimits.GroupLane, lanes);

        // Unmeasured and newly used: no exemption from the global backstop.
        Assert.True(write.CountsAgainstGlobal);
        Assert.True(read.CountsAgainstGlobal);
        Assert.True(create.CountsAgainstGlobal);
        Assert.Equal(1, write.BurstTokens);
    }

    [Fact]
    public async Task CalendarWritesGoOutAtMostOnceAMinute()
    {
        var harness = new LimiterHarness();
        var start = harness.Clock.UtcNow;

        for (var i = 0; i < 4; i++)
            await harness.CallAsync(Write, ct: Ct);

        // The first spends the burst token; the other three each wait out a minute.
        Assert.True(harness.Clock.UtcNow - start >= TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task InstancesAreCreatedAtMostOncePerFiveSeconds()
    {
        var harness = new LimiterHarness();
        var start = harness.Clock.UtcNow;

        for (var i = 0; i < 5; i++)
            await harness.CallAsync(Create, ct: Ct);

        Assert.True(harness.Clock.UtcNow - start >= TimeSpan.FromSeconds(20) - TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public async Task A429OnTheCalendarStopsTheCalendarAndNothingElse()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Write, status: 429, ct: Ct);

        var refused = await harness.CallAsync(Write, ct: Ct);
        Assert.False(refused.IsAcquired);
        Assert.Equal(RateLimitDenialReason.ColdStop, refused.Denial?.Reason);

        Assert.True((await harness.CallAsync(Members, ct: Ct)).IsAcquired);
        Assert.True((await harness.CallAsync(Create, ct: Ct)).IsAcquired);
    }
}
