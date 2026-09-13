using Modbot.TestSupport;
using Modbot.VRChat.Sync;

namespace Modbot.VRChat.Tests.Sync;

/// <summary>
/// Spec 4.2 paces the audit log at one request per 8 seconds; it does not say to spend that
/// allowance on a group where nothing is happening.
/// </summary>
public class AdaptiveCadenceTests
{
    private static readonly AuditLogSyncOptions Options = new()
    {
        MinInterval = TimeSpan.FromSeconds(8),
        MaxInterval = TimeSpan.FromMinutes(5),
        QuietBackoff = 2.0,
        JitterFraction = 0.1,
    };

    [Fact]
    public void ItStartsAtTheFastestPermittedRate()
    {
        var cadence = New();

        Assert.Equal(Options.MinInterval, cadence.Current.Interval);
        Assert.NotEmpty(cadence.Current.Reason);
    }

    [Fact]
    public void QuietPollsBackOffGeometricallyAndStopAtTheMaximum()
    {
        var cadence = New();
        var intervals = new List<TimeSpan>();

        for (var i = 0; i < 12; i++)
            intervals.Add(cadence.Observe(new AuditLogRunResult(SyncOutcome.Quiet)).Interval);

        Assert.Equal(Options.MinInterval, intervals[0]);
        Assert.Equal(Options.MinInterval * 2, intervals[1]);
        Assert.Equal(Options.MinInterval * 4, intervals[2]);

        // Capped, and it stays capped: an interval that kept doubling would eventually mean a ban
        // recorded a day after it happened.
        Assert.Equal(Options.MaxInterval, intervals[^1]);
        Assert.All(intervals, i => Assert.True(i <= Options.MaxInterval));
    }

    /// <summary>
    /// The case that makes the whole thing worth having. Moderation happens in bursts -- a raid,
    /// a wave of bans -- and a producer that had settled on five minutes and then eased back
    /// toward eight seconds would miss the burst it is for.
    /// </summary>
    [Fact]
    public void OneProductivePollSnapsStraightBackToTheFastestRate()
    {
        var cadence = New();

        for (var i = 0; i < 10; i++)
            cadence.Observe(new AuditLogRunResult(SyncOutcome.Quiet));

        Assert.Equal(Options.MaxInterval, cadence.Current.Interval);

        var decision = cadence.Observe(new AuditLogRunResult(SyncOutcome.Produced, FactsWritten: 3));

        Assert.Equal(Options.MinInterval, decision.Interval);
        Assert.Equal(0, decision.ConsecutiveQuietPolls);
    }

    /// <summary>
    /// Spec 4.3.1: never retry a 429. The bucket will refuse to send anything anyway, so polling
    /// faster only fills the log with refusals -- and if the gate ever did send one, the penalty
    /// grows by 45-80 seconds.
    /// </summary>
    [Fact]
    public void ARateLimitedPollWaitsAsLongAsItIsAllowedTo()
    {
        var cadence = New();
        var decision = cadence.Observe(new AuditLogRunResult(SyncOutcome.RateLimited));

        Assert.Equal(Options.MaxInterval, decision.Interval);
        Assert.Contains("rate limited", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A failure is not a quiet group. Backing all the way off on a transient error would cost
    /// five minutes of audit history for a blip.
    /// </summary>
    [Fact]
    public void AFailedPollRetriesSoonerThanAQuietOneEventuallyWould()
    {
        var cadence = New();
        var decision = cadence.Observe(new AuditLogRunResult(SyncOutcome.Failed, Message: "boom"));

        Assert.InRange(decision.Interval, Options.MinInterval, Options.MaxInterval);
        Assert.True(decision.Interval < Options.MaxInterval);
        Assert.Contains("boom", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnconfiguredDeploymentIdlesRatherThanPollingNothingEveryEightSeconds()
    {
        var cadence = New();
        var decision = cadence.Observe(new AuditLogRunResult(SyncOutcome.NotConfigured));

        Assert.Equal(Options.MaxInterval, decision.Interval);
    }

    /// <summary>
    /// Spec 4.2.2: jitter spreads a fleet, it does not find headroom. A negative swing that took
    /// the producer under the pacing cap would be the schedule quietly overriding the budget.
    /// </summary>
    [Fact]
    public void JitterNeverTakesTheProducerBelowThePacingFloor()
    {
        var cadence = New(new Random(42));
        var delays = Enumerable.Range(0, 500).Select(_ => cadence.NextDelay()).ToList();

        Assert.All(delays, d => Assert.True(d >= Options.MinInterval, $"{d} is under the floor"));

        // And it is actually jittering: a schedule that settled into an exact comb would slowly
        // re-align with every other Modbot in the world.
        Assert.True(delays.Distinct().Count() > 400);
    }

    /// <summary>
    /// Spec 4.2.1: configuration may only ever make Modbot gentler. An operator -- or a future
    /// settings page -- must not be able to poll faster than the cap by lowering a number.
    /// </summary>
    [Fact]
    public void ConfigurationCannotPollFasterThanSpecFourTwosCap()
    {
        var reckless = new AuditLogSyncOptions
        {
            MinInterval = TimeSpan.FromMilliseconds(50),
            MaxInterval = TimeSpan.FromMilliseconds(100),
        }.Clamped();

        Assert.Equal(AuditLogSyncOptions.PacingFloor, reckless.MinInterval);
        Assert.True(reckless.MaxInterval >= reckless.MinInterval);
    }

    private static AdaptiveCadence New(Random? random = null)
        => new(Options, new FakeClock(), random ?? new Random(7));
}
