using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// Spec 4.3.2: a restart must not be able to escalate a ten-minute rate limit into an
/// account-level problem.
/// </summary>
/// <remarks>
/// The dangerous case is not a single redeploy, it is a crash-loop: a process that dies and comes
/// back every few seconds, each time with a full budget and no memory of the penalty it is
/// sitting in. These tests recreate the limiter over the same store, which is exactly what that
/// looks like from the state's point of view.
/// </remarks>
public class RestartSafetyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VRChatEndpoint Members =
        new(VRChatEndpointClass.GroupsMembers, "grp_test");

    [Fact]
    public async Task ARestartResumesTheColdWaitAndIssuesNothing()
    {
        var harness = new LimiterHarness();
        await harness.CallAsync(Members, status: 429, ct: Ct);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        harness.Restart();

        var lease = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (lease)
        {
            Assert.False(lease.IsAcquired);
            Assert.Equal(RateLimitDenialReason.ColdStop, lease.Denial!.Reason);

            // Ten minutes of the fifteen remain: the clock kept running while the process was
            // not, and the persisted deadline is absolute rather than a countdown.
            Assert.Equal(TimeSpan.FromMinutes(10), lease.Denial.RetryAfter);
        }
    }

    [Fact]
    public async Task ARestartDoesNotRestoreTheBudgetThe429TookAway()
    {
        var harness = new LimiterHarness();
        await harness.CallAsync(Members, status: 429, ct: Ct);

        harness.Restart();

        var health = await harness.HealthAsync(VRChatEndpointClass.GroupsMembers);
        Assert.Equal(0.5, health.BudgetMultiplier, 6);
        Assert.Equal(1, health.RateLimitHits);
    }

    [Fact]
    public async Task ACrashLoopCannotIssueMoreThanOneProbePerWaitingPeriod()
    {
        var harness = new LimiterHarness();
        var vrchat = new PunitiveVRChat(harness.Clock) { AllowancePerWindow = 1 };

        // Trip it for real, so the fake is holding a live penalty throughout.
        var first = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (first)
        {
            await first.ReportAsync(vrchat.Call(Members.Class), Ct);
        }

        var second = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (second)
        {
            await second.ReportAsync(vrchat.Call(Members.Class), Ct);
        }

        Assert.Equal(429, vrchat.Calls[^1].Status);
        var penaltyAfterTrip = vrchat.PenaltyUntil(Members.Class);

        harness.Clock.Advance(harness.Options.ColdStopBase);

        // Now die and come back, repeatedly, in the window where a probe is due. The first
        // attempt gets the one probe; it is killed before it can report, which is the worst case
        // -- the request may have reached VRChat, so it has to be paid for as though it did.
        var probes = 0;
        for (var restart = 0; restart < 20; restart++)
        {
            harness.Restart();

            var lease = await harness.Limiter.AcquireAsync(Members, ct: Ct);
            if (lease.IsAcquired)
            {
                probes++;
                vrchat.Call(Members.Class);
            }

            // No ReportAsync and no graceful dispose: the process is gone.
            harness.Clock.Advance(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, probes);

        // Twenty restarts, one request. The penalty is exactly where VRChat put it -- twenty
        // probes would have pushed it roughly twenty minutes further out, which is how a
        // ten-minute limit becomes an afternoon.
        Assert.Equal(penaltyAfterTrip, vrchat.PenaltyUntil(Members.Class));
    }

    [Fact]
    public async Task AProbeThatSucceedsBeforeTheCrashStillClearsTheStopForTheNextProcess()
    {
        var harness = new LimiterHarness();
        await harness.CallAsync(Members, status: 429, ct: Ct);
        harness.Clock.Advance(harness.Options.ColdStopBase);

        var probe = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await probe.ReportAsync(200, Ct);
        await probe.DisposeAsync();

        harness.Restart();

        var lease = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (lease)
        {
            Assert.True(lease.IsAcquired);
        }
    }
}
