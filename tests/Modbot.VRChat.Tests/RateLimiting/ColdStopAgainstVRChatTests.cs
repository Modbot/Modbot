using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// The limiter driving the punitive fake, which is the only configuration in which the recovery
/// strategy is actually being judged on the thing it was designed against.
/// </summary>
public class ColdStopAgainstVRChatTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Members =
        new(VRChatEndpointClass.GroupsMembers, "grp_test");

    [Fact]
    public async Task OneProbeAfterOneLongWaitCostsExactlyOneRateLimit()
    {
        var harness = new LimiterHarness();
        var vrchat = new PunitiveVRChat(harness.Clock)
        {
            // Tighter than Modbot's own estimate, which is the case that matters: the estimate is
            // a guess about an undocumented system and will sometimes be wrong.
            AllowancePerWindow = 3,
            Window = TimeSpan.FromSeconds(10),
            PenaltyDuration = TimeSpan.FromMinutes(10),
        };

        var start = harness.Clock.UtcNow;
        await DriveAsync(harness, vrchat, TimeSpan.FromHours(1));

        var tripped = vrchat.Calls.First(c => c.Status == 429).At;

        // Exactly one: the limit was hit once, and everything after it was the cold stop doing
        // its job. The probe that ends the stop arrives after the penalty has already expired,
        // because the cold period is longer than the penalty rather than a guess at it.
        Assert.Equal(1, vrchat.RateLimitedCount);

        // And the penalty ended when VRChat said it would. Nothing Modbot did extended it, which
        // is the property the whole of spec 4.3.1 exists to buy.
        Assert.Equal(tripped + vrchat.PenaltyDuration, vrchat.PenaltyUntil(Members.Class));

        // Sync resumed rather than staying dark for the rest of the hour.
        var resumed = vrchat.Calls.Count(c => c.At > tripped && c.Status == 200);
        Assert.True(resumed > 1, $"expected sync to resume after the cold stop, saw {resumed} calls");
        Assert.True(harness.Clock.UtcNow - start >= TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task RetryingInsteadOfColdStoppingWouldHaveMadeItWorse()
    {
        // The counterfactual, run against the same fake: the old implementation's three-minute
        // retry loop. This is not testing Modbot -- it is testing that the fake is punishing, so
        // that the assertion above means something.
        var clock = new Modbot.TestSupport.FakeClock();
        var vrchat = new PunitiveVRChat(clock)
        {
            AllowancePerWindow = 3,
            PenaltyDuration = TimeSpan.FromMinutes(10),
        };

        for (var i = 0; i < 4; i++)
            vrchat.Call(Members.Class);

        var tripped = clock.UtcNow;

        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(3));
            vrchat.Call(Members.Class);
        }

        Assert.True(
            vrchat.PenaltyUntil(Members.Class) > tripped + vrchat.PenaltyDuration,
            "the fake is not modelling the penalty extension; every assertion about recovery is worthless");
        Assert.True(vrchat.RateLimitedCount > 1);
    }

    /// <summary>
    /// Runs a background sync the way a job would: ask the limiter, call if allowed, and when
    /// refused, go away and come back on the next tick rather than waiting in line.
    /// </summary>
    private static async Task DriveAsync(LimiterHarness harness, PunitiveVRChat vrchat, TimeSpan duration)
    {
        var deadline = harness.Clock.UtcNow + duration;

        while (harness.Clock.UtcNow < deadline)
        {
            var lease = await harness.Limiter.AcquireAsync(Members, ct: Ct);
            await using (lease)
            {
                if (!lease.IsAcquired)
                {
                    harness.Clock.Advance(TimeSpan.FromMinutes(1));
                    continue;
                }

                await lease.ReportAsync(vrchat.Call(Members.Class), Ct);
            }
        }
    }
}
