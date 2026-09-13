using Modbot.Client.Ingest;

namespace Modbot.Client.Tests.Ingest;

public class BackoffPolicyTests
{
    private static BackoffPolicy Deterministic(double jitter = 0.0) => new(
        initial: TimeSpan.FromSeconds(2),
        ceiling: TimeSpan.FromMinutes(5),
        multiplier: 2.0,
        jitter: () => jitter);

    [Fact]
    public void TheFirstAttemptDoesNotWait()
    {
        Assert.Equal(TimeSpan.Zero, Deterministic().Delay(0));
    }

    [Fact]
    public void EachFailureDoublesTheWindow()
    {
        var policy = Deterministic();

        Assert.Equal(TimeSpan.FromSeconds(1), policy.Delay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.Delay(2));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.Delay(3));
    }

    [Fact]
    public void ItStopsGrowingAtAboutFiveMinutes()
    {
        var policy = Deterministic(jitter: 1.0);

        Assert.Equal(TimeSpan.FromMinutes(5), policy.Delay(30));
        Assert.Equal(TimeSpan.FromMinutes(5), policy.Delay(300));
    }

    [Fact]
    public void HalfTheWindowIsRandomSoSixModeratorsDoNotRetryInLockstep()
    {
        // Every moderator in one instance sees the same outage at the same moment.
        var least = Deterministic(jitter: 0.0).Delay(5);
        var most = Deterministic(jitter: 1.0).Delay(5);

        Assert.Equal(most / 2, least);
        Assert.True(most > least);
    }

    [Fact]
    public void RealJitterSpreadsRetriesOut()
    {
        var policy = new BackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(5));

        var delays = Enumerable.Range(0, 50).Select(_ => policy.Delay(4)).Distinct().Count();

        Assert.True(delays > 10, "Backoff delays should not all be identical.");
    }

    [Fact]
    public void ItRetriesAtAll_WhichIsTheOppositeOfWhatModbotDoesToVRChat()
    {
        // Foundation 4.3.1 forbids retrying VRChat's 429 -- a cold stop -- because that limiter is
        // punitive and retrying extends the penalty. A Modbot server is ordinary software under the
        // operator's own control, and the cost of giving up is presence history that cannot be
        // backfilled. The two rules look alike and are opposites.
        Assert.True(Deterministic().Delay(1) > TimeSpan.Zero);
        Assert.True(Deterministic().Delay(50) <= TimeSpan.FromMinutes(5));
    }
}
