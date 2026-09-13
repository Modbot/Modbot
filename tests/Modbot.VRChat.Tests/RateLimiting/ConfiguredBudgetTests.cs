using Modbot.TestSupport;
using Modbot.VRChat.Pacing;
using Modbot.VRChat.RateLimiting;
using Modbot.VRChat.Tests.Fakes;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// Spec 4.2.1's rates reaching the limiter, without a restart and without touching spec 4.3.2's
/// penalty state.
/// </summary>
public class ConfiguredBudgetTests
{
    private static readonly VRChatEndpoint Members =
        new(VRChatEndpointClass.GroupsMembers, "grp_1");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (InProcessRateLimiter Limiter, FakeClock Clock, FakeSyncPacing Pacing,
        RecordingRateLimitStore Store) Build()
    {
        var clock = new FakeClock();
        var pacing = new FakeSyncPacing();
        var store = new RecordingRateLimitStore();
        var limiter = new InProcessRateLimiter(
            store, clock, new TestDelayScheduler(clock), options: null, pacing);

        return (limiter, clock, pacing, store);
    }

    [Fact]
    public async Task WithNothingConfigured_TheBucketsRunAtSpecFourTwosRates()
    {
        var (limiter, _, _, _) = Build();

        await using (await limiter.AcquireAsync(Members, ct: Ct)) { }

        var health = await limiter.DescribeAsync(Ct);
        var members = health.Single(b => b.Name == VRChatEndpointClass.GroupsMembers);

        Assert.Equal(0.5, members.EffectiveRatePerSecond, 6);
    }

    /// <summary>
    /// A lowered rate reaches a limiter that is already running.
    /// </summary>
    /// <remarks>
    /// The limiter is a process-wide singleton created at startup, so without this the setting
    /// would be one that quietly needed a restart — and the operator most likely to move it is
    /// the one who has just been rate limited and cannot afford to guess.
    /// </remarks>
    [Fact]
    public async Task ALoweredRateAppliesWithoutARestart()
    {
        var (limiter, _, pacing, _) = Build();

        await using (await limiter.AcquireAsync(Members, ct: Ct)) { }

        pacing.Set(new SyncPacingDocument
        {
            ClassCeilingsPerSecond = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [VRChatEndpointClass.GroupsMembers] = 0.1,
            },
        });

        var health = await limiter.DescribeAsync(Ct);

        // Both the class bucket and the resource bucket beneath it: a rate applied only to the
        // parent would be silently ignored, because a request has to take a token at every level.
        Assert.Equal(
            0.06,
            health.Single(b => b.Name == VRChatEndpointClass.GroupsMembers).EffectiveRatePerSecond,
            6);

        Assert.Equal(
            0.06,
            health.Single(b => b.Name == $"{VRChatEndpointClass.GroupsMembers}:grp_1").EffectiveRatePerSecond,
            6);
    }

    /// <summary>A bucket created after the rates were set is created with them.</summary>
    [Fact]
    public async Task ABucketThatDidNotExistYetPicksUpTheConfiguredRate()
    {
        var (limiter, _, pacing, _) = Build();

        pacing.Set(new SyncPacingDocument
        {
            ClassCeilingsPerSecond = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [VRChatEndpointClass.GroupsBans] = 0.1,
            },
        });

        await using (await limiter.AcquireAsync(new VRChatEndpoint(VRChatEndpointClass.GroupsBans, "grp_1"), ct: Ct)) { }

        var health = await limiter.DescribeAsync(Ct);

        Assert.Equal(
            0.06,
            health.Single(b => b.Name == $"{VRChatEndpointClass.GroupsBans}:grp_1").EffectiveRatePerSecond,
            6);
    }

    /// <summary>
    /// The cap survives the whole path from document to bucket, including the global backstop.
    /// </summary>
    [Fact]
    public async Task ARateAboveTheCapNeverReachesTheBucket()
    {
        var (limiter, _, pacing, _) = Build();

        pacing.Set(new SyncPacingDocument
        {
            ClassCeilingsPerSecond = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [VRChatEndpointClass.GroupsMembers] = 500,
                [VRChatEndpointClass.Global] = 500,
            },
        });

        await using (await limiter.AcquireAsync(Members, ct: Ct)) { }

        var health = await limiter.DescribeAsync(Ct);

        Assert.Equal(
            0.5,
            health.Single(b => b.Name == VRChatEndpointClass.GroupsMembers).EffectiveRatePerSecond,
            6);

        Assert.Equal(
            2.0,
            health.Single(b => b.Name == VRChatEndpointClass.Global).EffectiveRatePerSecond,
            6);
    }

    /// <summary>
    /// The bucket refuses a rate above spec 4.2's cap even when nothing clamped it first.
    /// </summary>
    /// <remarks>
    /// The endpoint clamps on write and the resolver clamps on read; this is the third line,
    /// which is the one that holds when the other two are bypassed — a hand-edited row, a
    /// restored backup, or a bug in either of them. Spec 4.3's asymmetry is why there are three:
    /// the cost of one of them being wrong is not a slow sync.
    /// </remarks>
    [Fact]
    public void ABucketConfiguredDirectlyAboveTheCapStillPacesAtTheCap()
    {
        var clock = new FakeClock();
        var limits = VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsMembers];
        var bucket = new TokenBucket("direct", limits, new RateLimitOptions(), clock.UtcNow);

        bucket.Configure(ceilingPerSecond: 10_000, fraction: 1.0);

        Assert.Equal(limits.HardMaxPerSecond, bucket.EffectiveRatePerSecond, 6);
    }

    /// <summary>
    /// <strong>Changing a budget does not lift a cold stop.</strong>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Spec 4.3.2 persists penalty state precisely so that a restart cannot be used to clear it —
    /// a crash-loop must not be able to turn a ten-minute rate limit into an account-level
    /// problem. A settings page that reset the stop by writing a budget would reopen that hole
    /// from the other side, with the difference that anybody with ManageSettings could reach it
    /// and that clearing a stop is exactly what an operator watching a stalled sync would try.
    /// </para>
    /// <para>
    /// The AIMD multiplier is checked here too. It is the limiter's own opinion of the estimate
    /// (spec 4.3.1), earned back over hours of success — restoring it on a configuration write
    /// would hand back in one click what the recovery schedule is deliberately slow about.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ChangingABudgetDoesNotClearAnActiveColdStop()
    {
        var (limiter, clock, pacing, _) = Build();

        var lease = await limiter.AcquireAsync(Members, ct: Ct);
        await lease.ReportAsync(429, Ct);
        await lease.DisposeAsync();

        var stopped = (await limiter.DescribeAsync(Ct))
            .Single(b => b.Name == $"{VRChatEndpointClass.GroupsMembers}:grp_1");

        Assert.True(stopped.IsColdStopped);
        Assert.Equal(0.5, stopped.BudgetMultiplier, 6);

        // The operator reacts the way an operator would: turns the rate down. This must not be a
        // way to get the sync moving again before the penalty has been waited out.
        clock.Advance(TimeSpan.FromMinutes(1));
        pacing.Set(new SyncPacingDocument
        {
            ClassCeilingsPerSecond = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [VRChatEndpointClass.GroupsMembers] = 0.05,
            },
        });

        var after = (await limiter.DescribeAsync(Ct))
            .Single(b => b.Name == $"{VRChatEndpointClass.GroupsMembers}:grp_1");

        Assert.True(after.IsColdStopped);
        Assert.Equal(stopped.StoppedUntil, after.StoppedUntil);
        Assert.Equal(stopped.RateLimitHits, after.RateLimitHits);
        Assert.Equal(stopped.LastRateLimitedAt, after.LastRateLimitedAt);

        // The multiplier is untouched by configuration, and still applies to the new rate.
        Assert.Equal(0.5, after.BudgetMultiplier, 6);
        Assert.Equal(0.05 * 0.6 * 0.5, after.EffectiveRatePerSecond, 6);

        // And nothing is issued: the stop denies rather than waiting, because a cold stop has no
        // known end (spec 4.3.1).
        var denied = await limiter.AcquireAsync(Members, ct: Ct);
        await using (denied)
        {
            Assert.False(denied.IsAcquired);
            Assert.Equal(RateLimitDenialReason.ColdStop, denied.Denial!.Reason);
        }
    }

    /// <summary>
    /// The same, across a restart: the write is persisted and the stop is still persisted.
    /// </summary>
    [Fact]
    public async Task AConfiguredRateAndAColdStopBothSurviveARestart()
    {
        var clock = new FakeClock();
        var pacing = new FakeSyncPacing();
        var store = new RecordingRateLimitStore();
        var delays = new TestDelayScheduler(clock);

        var first = new InProcessRateLimiter(store, clock, delays, options: null, pacing);

        var lease = await first.AcquireAsync(Members, ct: Ct);
        await lease.ReportAsync(429, Ct);
        await lease.DisposeAsync();

        pacing.Set(new SyncPacingDocument
        {
            ClassCeilingsPerSecond = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [VRChatEndpointClass.GroupsMembers] = 0.1,
            },
        });

        _ = await first.DescribeAsync(Ct);

        // The redeploy.
        var second = new InProcessRateLimiter(store, clock, delays, options: null, pacing);
        var restored = (await second.DescribeAsync(Ct))
            .Single(b => b.Name == $"{VRChatEndpointClass.GroupsMembers}:grp_1");

        Assert.True(restored.IsColdStopped);
        Assert.Equal(0.1 * 0.6 * 0.5, restored.EffectiveRatePerSecond, 6);
    }
}
