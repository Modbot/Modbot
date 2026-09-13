using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// The limiter's behaviour under spec 4.3.1: hierarchy, cold stop, one probe, AIMD.
/// </summary>
public class RateLimiterTests
{
    private static readonly VRChatEndpoint Members =
        new(VRChatEndpointClass.GroupsMembers, "grp_test", "GetGroupMembers");

    private static readonly VRChatEndpoint AuditLog =
        new(VRChatEndpointClass.GroupsAuditLog, "grp_test", "GetGroupAuditLogs");

    private static readonly VRChatEndpoint Profile =
        new(VRChatEndpointClass.UsersRead, Operation: "GetUser");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ABucketNeverIssuesFasterThanItsConfiguredFraction()
    {
        var harness = new LimiterHarness();
        var limits = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsMembers];
        var allowed = Math.Min(
            limits.HardMaxPerSecond,
            limits.DefaultCeilingPerSecond * RateLimitOptions.DefaultFraction);

        var start = harness.Clock.UtcNow;
        const int calls = 10;

        for (var i = 0; i < calls; i++)
            await harness.CallAsync(Members, ct: Ct);

        var elapsed = (harness.Clock.UtcNow - start).TotalSeconds;

        // The first call spends the bucket's single burst token, so the rate the limiter sustained
        // is the remaining calls over the elapsed time.
        Assert.True(
            (calls - 1) / elapsed <= allowed + 1e-6,
            $"issued {calls - 1} paced calls in {elapsed}s, above the configured {allowed} req/s");

        // And it is genuinely spending that time waiting, rather than passing the check by being
        // slower than anyone would accept.
        Assert.True(elapsed <= (calls - 1) / allowed * 1.05);
    }

    [Fact]
    public async Task TheGlobalCeilingBindsAcrossEndpointClasses()
    {
        var harness = new LimiterHarness();
        var global = VRChatRateLimits.Defaults[VRChatEndpointClass.Global];

        var start = harness.Clock.UtcNow;
        for (var i = 0; i < 6; i++)
        {
            await harness.CallAsync(Members, ct: Ct);
            await harness.CallAsync(AuditLog, ct: Ct);
        }

        var elapsed = (harness.Clock.UtcNow - start).TotalSeconds;
        Assert.True(11 / elapsed <= global.HardMaxPerSecond + 1e-6);
    }

    [Fact]
    public async Task UsersReadIsExemptFromTheGlobalCeiling()
    {
        var harness = new LimiterHarness();

        for (var i = 0; i < 5; i++)
            await harness.CallAsync(Profile, ct: Ct);

        var buckets = await harness.Limiter.DescribeAsync(Ct);

        // Spec 4.2.5: the users lane does not pass through the backstop at all, so the global
        // bucket has never been touched and does not even exist yet.
        Assert.DoesNotContain(buckets, b => b.Name == VRChatEndpointClass.Global);
    }

    [Fact]
    public async Task A429ColdStopsOnlyTheMostSpecificBucket()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Members, status: 429, ct: Ct);

        var blocked = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (blocked)
        {
            Assert.False(blocked.IsAcquired);
            Assert.Equal(RateLimitDenialReason.ColdStop, blocked.Denial!.Reason);
            Assert.Equal($"{VRChatEndpointClass.GroupsMembers}:grp_test", blocked.Denial.Bucket);
            Assert.Equal(harness.Options.ColdStopBase, blocked.Denial.RetryAfter);
        }

        // The audit log is cheap and is the authoritative fact source; a members limit must not
        // stop it (spec 4.3.1).
        var audit = await harness.Limiter.AcquireAsync(AuditLog, ct: Ct);
        await using (audit)
        {
            Assert.True(audit.IsAcquired);
        }
    }

    [Fact]
    public async Task A429HalvesTheBudgetOfEveryAncestor()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Members, status: 429, ct: Ct);

        Assert.Equal(0.5, (await harness.HealthAsync(VRChatEndpointClass.Global)).BudgetMultiplier, 6);
        Assert.Equal(0.5, (await harness.HealthAsync(VRChatEndpointClass.GroupsMembers)).BudgetMultiplier, 6);
        Assert.Equal(
            0.5,
            (await harness.HealthAsync($"{VRChatEndpointClass.GroupsMembers}:grp_test")).BudgetMultiplier,
            6);
    }

    [Fact]
    public async Task A429OnTheExemptUsersLaneStillPenalisesTheGlobalBucket()
    {
        var harness = new LimiterHarness();

        await harness.CallAsync(Profile, status: 429, ct: Ct);

        // Exempt from the ceiling, not from the evidence: a limit hit anywhere says the whole
        // model is optimistic, and this is the signal spec 4.2.5 asks to be watched for.
        Assert.Equal(0.5, (await harness.HealthAsync(VRChatEndpointClass.Global)).BudgetMultiplier, 6);
    }

    [Fact]
    public async Task NothingIsIssuedForTheWholeWaitingPeriodAndThenExactlyOneProbe()
    {
        var harness = new LimiterHarness();
        await harness.CallAsync(Members, status: 429, ct: Ct);

        var attempts = 0;
        var issued = 0;

        // A minute at a time across the whole cold period, which is how a sync job would behave.
        for (var minute = 1; minute <= 14; minute++)
        {
            harness.Clock.Advance(TimeSpan.FromMinutes(1));
            attempts++;

            var lease = await harness.Limiter.AcquireAsync(Members, ct: Ct);
            await using (lease)
            {
                if (lease.IsAcquired)
                    issued++;
            }
        }

        Assert.Equal(14, attempts);
        Assert.Equal(0, issued);

        harness.Clock.Advance(TimeSpan.FromMinutes(1));

        var probe = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (probe)
        {
            Assert.True(probe.IsAcquired);
            Assert.True(probe.IsProbe);
            await probe.ReportAsync(200, Ct);
        }

        var resumed = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (resumed)
        {
            Assert.True(resumed.IsAcquired);
            Assert.False(resumed.IsProbe);
        }
    }

    [Fact]
    public async Task AFailedProbeWaitsLongerAndOnlyProbesOnceMore()
    {
        var harness = new LimiterHarness();
        await harness.CallAsync(Members, status: 429, ct: Ct);

        harness.Clock.Advance(harness.Options.ColdStopBase);

        var first = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (first)
        {
            Assert.True(first.IsProbe);
            await first.ReportAsync(429, Ct);
        }

        // The wait grows linearly, not exponentially: the objective is fewer probes, and
        // exponential backoff optimises for converging quickly, which is the wrong goal here.
        harness.Clock.Advance(harness.Options.ColdStopBase);
        var tooSoon = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (tooSoon)
        {
            Assert.False(tooSoon.IsAcquired);
        }

        harness.Clock.Advance(harness.Options.ColdStopIncrement);
        var second = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (second)
        {
            Assert.True(second.IsProbe);
            await second.ReportAsync(200, Ct);
        }
    }

    [Fact]
    public async Task ProbingIsAbandonedAndTheOperatorAlertedAfterRepeatedFailures()
    {
        var harness = new LimiterHarness();
        await harness.CallAsync(Members, status: 429, ct: Ct);

        for (var attempt = 1; attempt <= harness.Options.MaxProbeFailures; attempt++)
        {
            harness.Clock.Advance(
                harness.Options.ColdStopBase + (harness.Options.ColdStopIncrement * (attempt - 1)));

            var probe = await harness.Limiter.AcquireAsync(Members, ct: Ct);
            await using (probe)
            {
                Assert.True(probe.IsProbe);
                await probe.ReportAsync(429, Ct);
            }
        }

        harness.Clock.Advance(TimeSpan.FromHours(24));

        var denied = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (denied)
        {
            Assert.False(denied.IsAcquired);
            Assert.Equal(RateLimitDenialReason.Alerting, denied.Denial!.Reason);
        }

        var health = await harness.HealthAsync($"{VRChatEndpointClass.GroupsMembers}:grp_test");
        Assert.True(health.Alerting);
    }

    [Fact]
    public async Task AProbeWhoseOutcomeIsNeverReportedDoesNotBuyAnotherOne()
    {
        var harness = new LimiterHarness();
        await harness.CallAsync(Members, status: 429, ct: Ct);
        harness.Clock.Advance(harness.Options.ColdStopBase);

        // Acquired and then abandoned -- a cancellation, or a request that never returned. The
        // request may well have reached VRChat, so it has to be paid for as though it did.
        var probe = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        Assert.True(probe.IsProbe);
        await probe.DisposeAsync();

        var next = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (next)
        {
            Assert.False(next.IsAcquired);
            Assert.Equal(RateLimitDenialReason.ProbeInFlight, next.Denial!.Reason);
        }
    }

    [Fact]
    public async Task BudgetRecoversAdditivelyOverHoursNotAtOnce()
    {
        var harness = new LimiterHarness();
        await harness.CallAsync(Members, status: 429, ct: Ct);

        harness.Clock.Advance(harness.Options.ColdStopBase);
        var probe = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (probe)
        {
            await probe.ReportAsync(200, Ct);
        }

        // Resumed, but at half budget: a successful probe is not evidence that the old estimate
        // was right, only that the penalty has expired.
        Assert.Equal(0.5, (await harness.HealthAsync(VRChatEndpointClass.GroupsMembers)).BudgetMultiplier, 6);

        harness.Clock.Advance(TimeSpan.FromHours(1));
        await harness.CallAsync(Members, ct: Ct);
        Assert.Equal(
            0.5 + harness.Options.IncreasePerHour,
            (await harness.HealthAsync(VRChatEndpointClass.GroupsMembers)).BudgetMultiplier,
            3);

        harness.Clock.Advance(TimeSpan.FromHours(10));
        await harness.CallAsync(Members, ct: Ct);
        Assert.Equal(1.0, (await harness.HealthAsync(VRChatEndpointClass.GroupsMembers)).BudgetMultiplier, 6);
    }

    [Fact]
    public async Task TheHalvedBudgetActuallyHalvesTheIssueRate()
    {
        var harness = new LimiterHarness();
        var baseline = await MeasureRateAsync(new LimiterHarness());

        await harness.CallAsync(Members, status: 429, ct: Ct);
        harness.Clock.Advance(harness.Options.ColdStopBase);

        var probe = await harness.Limiter.AcquireAsync(Members, ct: Ct);
        await using (probe)
        {
            await probe.ReportAsync(200, Ct);
        }

        var reduced = await MeasureRateAsync(harness);
        Assert.Equal(baseline / 2, reduced, 3);

        static async Task<double> MeasureRateAsync(LimiterHarness harness)
        {
            // One call to spend whatever is in the bucket, then measure the paced rate. Without
            // the warm-up the two measurements would not be comparable: the first has a burst
            // token to spend and a bucket resuming from a cold stop does not.
            await harness.CallAsync(Members, ct: Ct);

            var start = harness.Clock.UtcNow;
            const int calls = 4;

            for (var i = 0; i < calls; i++)
                await harness.CallAsync(Members, ct: Ct);

            return calls / (harness.Clock.UtcNow - start).TotalSeconds;
        }
    }

    [Fact]
    public async Task AnEndpointClassWithNoBudgetIsRefusedRatherThanGuessedAt()
    {
        var harness = new LimiterHarness();

        // Spec 4.3.4 is a standing instruction to ask before using a new endpoint. Inferring a
        // limit from a neighbour is exactly what must not happen silently.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Limiter.AcquireAsync(new VRChatEndpoint("worlds.read"), ct: Ct));

        Assert.Contains("worlds.read", error.Message, StringComparison.Ordinal);
    }
}
