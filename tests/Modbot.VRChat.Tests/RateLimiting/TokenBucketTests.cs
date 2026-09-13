using Modbot.TestSupport;
using Modbot.VRChat.RateLimiting;

namespace Modbot.VRChat.Tests.RateLimiting;

/// <summary>
/// One bucket on its own: the arithmetic, and the cap that configuration cannot get past.
/// </summary>
public class TokenBucketTests
{
    private static readonly RateLimitClassOptions Members =
        VRChatRateLimits.Defaults[VRChatEndpointClass.GroupsMembers];

    private static readonly DateTimeOffset Start = new FakeClock().UtcNow;

    [Fact]
    public void ConfigurationCanLowerTheRate()
    {
        var bucket = NewBucket();
        bucket.Configure(Members.DefaultCeilingPerSecond / 4, RateLimitOptions.DefaultFraction);

        Assert.Equal(Members.HardMaxPerSecond / 4, bucket.EffectiveRatePerSecond, 6);
    }

    [Fact]
    public void ConfigurationCannotRaiseItPastSpecFourTwosCap()
    {
        var bucket = NewBucket();

        // Spec 4.2.1: the table's values are hard maxima and configuration may only lower them.
        // The clamp lives on the bucket rather than only at the settings boundary, so no write
        // path -- an API call, a hand-edited row, an import invented later -- can get past it.
        bucket.Configure(ceilingPerSecond: 1000, fraction: 1.0);

        Assert.Equal(Members.HardMaxPerSecond, bucket.EffectiveRatePerSecond, 6);
    }

    [Fact]
    public void NonsenseConfigurationIsIgnoredRatherThanApplied()
    {
        var bucket = NewBucket();
        var before = bucket.EffectiveRatePerSecond;

        bucket.Configure(ceilingPerSecond: 0, fraction: 0);
        bucket.Configure(ceilingPerSecond: -5, fraction: 4);

        // A zero rate is a bucket that never opens again, and only successes restore budget --
        // so a bad write would be permanent rather than merely wrong.
        Assert.Equal(before, bucket.EffectiveRatePerSecond, 6);
    }

    [Fact]
    public void RefillNeverExceedsTheBurst()
    {
        var bucket = NewBucket();
        bucket.Take(Start);

        Assert.Equal(TimeSpan.Zero, bucket.TimeUntilToken(Start + TimeSpan.FromHours(1)));
        Assert.Equal(Members.BurstTokens, bucket.Tokens, 6);
    }

    [Fact]
    public void AClockThatMovesBackwardsDoesNotMintTokens()
    {
        var bucket = NewBucket();
        bucket.Take(Start);

        // A corrected server clock, or a restored snapshot. The window restarts; it does not pay
        // out, and it does not strand the bucket either.
        var wait = bucket.TimeUntilToken(Start - TimeSpan.FromMinutes(5));

        Assert.True(wait > TimeSpan.Zero);
        Assert.Equal(0, bucket.Tokens, 6);
    }

    [Fact]
    public void AColdStopDiscardsBankedTokens()
    {
        var bucket = NewBucket();

        // Otherwise the bucket reopens with a burst in hand and spends it immediately, straight
        // back into the limit it just hit.
        bucket.ColdStop(Start);

        Assert.Equal(0, bucket.Tokens, 6);
        Assert.True(bucket.IsColdStopped);
    }

    [Fact]
    public void TheBudgetNeverFallsToZeroHoweverManyTimesItIsHalved()
    {
        var options = new RateLimitOptions();
        var bucket = NewBucket(options);

        for (var i = 0; i < 50; i++)
            bucket.ApplyMultiplicativeDecrease(Start);

        // A rate of zero issues nothing, and only successes restore budget -- so a floor is what
        // keeps a bad hour from becoming a permanently dark bucket.
        Assert.Equal(options.MinimumBudgetMultiplier, bucket.BudgetMultiplier, 6);
        Assert.True(bucket.EffectiveRatePerSecond > 0);
    }

    private static TokenBucket NewBucket(RateLimitOptions? options = null) =>
        new(VRChatEndpointClass.GroupsMembers, Members, options ?? new RateLimitOptions(), Start);
}
